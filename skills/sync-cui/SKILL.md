---
name: sync-cui
description: Sync the pinned CUI submodule to a newer upstream release and cover the API it added — bump native/CUI, rebuild the native library, regenerate the binding, clear every PENDING entry in coverage.txt, then move the gallery, docs and gates with it. Use when bumping CUI, when bindgen reports unmigrated sections or records, or when a new CUI component has to reach C#.
---

# Sync CUI

CuiSharp binds CUI, a C library pinned in `native/CUI`. A sync is four things that must happen in
this order: bump the submodule, rebuild the native library, regenerate the binding, and **decide the
status of every function upstream added**. `docs/ARCHITECTURE.md` says why the rules exist;
`AGENTS.md` says what "done" means. This file is the procedure, and the error→fix table in step 3 is
the part that costs the most to rediscover.

## 0. Three rules before you type

- `native/CUI` is **pristine upstream** (D2). Never edit it; never add `native/patches/*.patch`.
- Never hand-edit a generated file. Every fix is in `tools/bindgen.cs` or `bindings/overrides.json`,
  then `pwsh tools/regen.ps1`.
- One C function, one decided status. A `PENDING` line in `bindings/coverage.txt` is unfinished work,
  not a known limitation. `EXCLUDED` needs a reason that is actually true.

## 1. See what moved, before reading any code

```sh
pwsh skills/sync-cui/scripts/cui-api-diff.ps1 -Notes     # pinned commit -> newest tag
```

It prints the commits, the functions and structs gained or lost, the new header sections, and
upstream's own changelog section. Read that instead of the diff — the diff is 15 000 lines of C files
being split into phases, and none of it touches the binding. A sync with **no** API change is only a
rebuild and a regenerate.

Then pin it:

```sh
cd native/CUI && git fetch --tags --all && git checkout <tag> && git describe --tags && cd ../..
```

## 2. Rebuild the native library

```sh
pwsh native/build-native.ps1      # cui.dll, cui.lib, native-provenance.txt (records cui-rev)
```

A locked `cui.dll` means an app is still running. Close it.

## 3. Regenerate — and expect the first run to fail

```sh
pwsh tools/regen.ps1 -NoNative -NoTest
```

The generator stops on the first thing it cannot justify. Each message names its own fix:

| bindgen says | it means | fix in `overrides.json` / generator |
|---|---|---|
| `Unhandled exception … CXType_ConstantArray 'char[24]'` | a struct holds a **nested** C array (`char name[10][24]`) | generator: `ReadStruct` emits one `[InlineArray]` per dimension |
| `PENDING x: header section not migrated: ` (empty) | the section parser read a title line as a bare rule | generator: `Module()` must take both `/* ---- Title ---- */` and rule/title/rule, plus `/* ---- Title ----` with no close |
| `PENDING x: header section not migrated: <section>` | a real section nobody migrated | `WidgetModules` in `bindgen.cs` |
| `Unknown module: <file>` | `excludedModules` names a section that no longer exists, because upstream added titles to that file | rename the exclusion to the new section names |
| `PENDING <opts>: <field>: <type> needs explicit ownership/length semantics` | a pointer field in an options struct | `optionPointers` (`"type": "int"`, a `ref`), `hiddenOptionFields` (a callback) |
| `PENDING <opts>: <field>: array length depends on widget parameters` | an array field whose count has a different name | `optionFieldCounts` (`{"count": "<field>"}`) — or, for `const <record> *`, a `recordNames` entry for the element struct, because a bare struct has no `ToFrameArray` |
| `PENDING <opts>: <field>: const <struct> * needs explicit ownership/length semantics` | the element struct of a record array is not a record | `recordNames` for that struct (`cui_chart_annotation` → `ChartAnnotation`); the count then derives on its own |
| `PENDING <fn>: <param> is an in-place array of double *, which has no span type yet` | an `inOutArrays` entry for a primitive whose kind the generator does not map yet | add the kind to `PrimitiveElement` (and `IsDoubleArray` if the whole type is missing) |
| `PENDING <fn>: <param>: const <struct> * needs explicit pointer semantics` where the struct holds a bare `const float *` and no count | the samples' length is the *call's*, not the struct's, so it can never be a self-describing record | a hand-written wrapper (`manualWidgets`) that builds the native array in the frame arena, as the charts do |
| `Argument N must be passed with the 'ref' keyword` from the **raw** layer | `Parameter()` read a non-const plural pointer as one moved scalar | an `inOutArrays` (or `arrayParams`) entry: the override is consulted before the heuristic |
| `PENDING <opts>.<field>` where a field is `char *`/`_Bool *` | an in/out value | `optionTextFields` (buffer + `ref string`), `optionPointers` |
| `PENDING <opts>`, and a public option struct that vanished from `Options.g.cs` | a pending option is skipped whole, so the hand-written partial that uses it fails to compile somewhere else entirely | the same fix as any other pending option; the coverage test fails on the `PENDING` line itself, not on the missing type |
| `PENDING <fn>: <param>: char * needs explicit pointer semantics` | editable text: `char *buf, size_t cap` | `textFields` (`"capacity": "<cap>"`, optional `"id"`) |
| `PENDING <fn>: return needs typed result/ownership semantics` | the widget returns a struct by value, or a `const char *` | `recordNames` (the result struct), `stringResults` (the C string) |
| `PENDING <fn>: <param>: native record needs conversion/write-back` | an in/out record parameter | `parameterStorage` (`"storage": "value"`) + `recordNames` for the struct |
| `PENDING <fn>: <param>: const char *const * needs explicit pointer semantics` | a string array whose count is not adjacent | `arrayLengths` (`{"length": "<count>", "emit": false}`) |
| `PENDING <fn>: not a UI-context function` | no `cui_ctx`: a pure C helper | a hand-written static helper, then `manualWidgets` **and** `excludedFunctions`, both naming the member and the file |
| `… needs a mapping` from `FixedArrayField` | a fixed array of a type the record mapper does not know | extend `FixedArrayField` |
| `stale override rejected` / `reasonless override rejected` | an override names something upstream renamed or removed — or has no `why` | fix or delete it. This self-check is why a rename cannot pass silently |

Snippets worth knowing:

- **`inOutArrays`** is a record array the widget edits: `{"count": "count", "changedWhen": "__result != 0"}`.
  `changedWhen` is not decoration — converting a record allocates every string and fixed array it
  holds, so an ungated write-back costs bytes on every frame. The frame-budget test catches it.
- A **count CUI takes as a pointer** comes back to the caller (`ref int count`); a count it takes by
  value cannot change, so the wrapper copies the records back only.
- **A capacity is the array's length**, so it never reaches the caller: `capacityParams`
  (`{"array": "times"}`) drops the parameter and passes `times.Length`. Check any `int capacity` a
  new function grows — the widget writes up to it.
- **A primitive in-place array is copied back unconditionally**, even with `changedWhen` set: native
  sorts and clamps in place before it draws, and the index it reports refers to *its* order. Only a
  record conversion is gated, because that is the one that allocates.
- **Result structs are records too.** `recordNames` gives them `FromNative`, and the wrapper returns
  the friendly type instead of a `Native*` struct — that is the whole "typed result" convention.
- A fixed buffer in a record is text (`char[N]` → `string?`) or an array (`bool[N]` → `bool[]?`),
  copied both ways. `CUI_FLAG_ENVS`-style widths mean the caller may get back a longer array than it
  passed; that is the C struct's width, not a bug.

Loop until the coverage is empty, then confirm:

```sh
pwsh tools/regen.ps1 -NoNative -NoTest
grep -c PENDING bindings/coverage.txt        # must print 0
sed -n '1,4p' bindings/coverage.txt          # option gaps also 0
```

## 4. Cover what the generator refuses to guess

Anything in the `not a UI-context function` row is a static helper, not a `Ui` member. Put it beside
its siblings (`src/CuiSharp/Ui/CuiMath.cs`, `CuiFiles.cs`, and one file per domain — `CuiRegex.cs`,
`CuiCron.cs`, `CuiHttp.cs`, `CuiQuery.cs`), give it XML docs on **every** parameter (`-warnaserror`
fails with CS1573 otherwise), and register it in both `manualWidgets` and `excludedFunctions`.

Name public types as if an app imports two libraries: `Diagnostic` collides with Roslyn and had to be
`ProblemItem`. Check the type name against the namespaces a user of that component would also import.

## 5. Move the four things that follow the API

| What | Where | Check |
|---|---|---|
| counts in prose | `bindings/README.md` "Current state", `README.md` "Status" | they must equal `coverage.txt` and the function total |
| the public surface | `docs/API-SURFACE.txt` | `pwsh tools/api-surface.ps1`, then review |
| a gallery page | `samples/CuiSharp.Gallery/Pages/*.cs`, `PageCount`, `PageNames`, `PageIcons`, the `switch` in `GalleryApp.Shell.cs` | the page draws and reports nothing clicked |
| screenshots | `docs/images/gallery/`, `tools/snapshots.ps1` page list | `pwsh tools/snapshots.ps1`; read one PNG |

Review the surface diff by **set difference**, not by reading 1000 lines:

```sh
git show HEAD:docs/API-SURFACE.txt | sed 's/^ *//' | sort -u > /tmp/old.txt
sed 's/^ *//' docs/API-SURFACE.txt | sort -u > /tmp/new.txt
comm -23 /tmp/old.txt /tmp/new.txt        # must be empty, or exactly what you meant to change
comm -13 /tmp/old.txt /tmp/new.txt | wc -l
```

A new section changes which extension class a member hangs off, so a large diff is expected: members
that only moved appear in both sets and cancel out. A removal that is *not* intentional is a wrapper
you lost.

## 6. Version, changelog, budgets

- New surface is a **minor** bump pre-1.0: `Directory.Build.props` `<Version>` **and** the template's
  `template.json` `CuiSharpVersion` (a test pins the two together).
- `CHANGELOG.md` needs a dated section for the version being built or `ReleaseNotesTests` fails. Check
  it renders as a release page: `pwsh tools/release-notes.ps1 -Version x.y.z`.
- An AOT binary that grew past its written budget is a decision, not a warning: measure it, update
  the row in `docs/PERFORMANCE.md` **with the cause**, and say so in the changelog. Frame time must
  not move.

## 7. The gates, in this order

```sh
dotnet build  CuiSharp.slnx -c Release -warnaserror
dotnet test   CuiSharp.slnx -c Release
dotnet format CuiSharp.slnx --verify-no-changes
pwsh tools/regen.ps1 -NoNative -NoDiff          # + the 13 generator contract checks
pwsh tools/api-surface.ps1                      # and the set-difference review in step 5
pwsh tools/verify-template.ps1                  # pack -> new -> build -> test -> AOT -> analyzers -> dotnet cui
dotnet publish samples/CuiSharp.Gallery -c Release -r win-x64    # zero IL2xxx/IL3xxx
pwsh tools/budget-check.ps1 -AotBytes <size>    # against docs/PERFORMANCE.md
dotnet run -c Release --project src/CuiSharp.Benchmarks -- --fps 900
```

Report what each printed. A gate you did not run is untested, not passing.

## 8. Traps that cost time

- **`dotnet pack` and `[System.IO.File]::ReadAllLines`**: not a legal MSBuild property function; the
  provenance step reads the file with `ReadLinesFromFile`. If `verify-template.ps1` dies at "packing
  CuiSharp", that is where to look.
- **A new widget that edits an array**: if the count is adjacent and by value, the default read-only
  array mapping wins over `inOutArrays` unless the override is checked first. A toggle that does
  nothing is that bug.
- **`ref int count`**: a widget that can add or remove entries must hand the new length back. A
  by-value count with a `count = __count;` write-back is dead code — the generator used to emit
  exactly that.
- **Multi-line declarations** (`cui_waterfall_ex`) and **declarations split across `#define` macros**
  (`cui_kv_editor` is a macro over `cui_kv_editor_ex`): the generator works from the `_ex` function;
  the macro is the C ergonomics default-argument trick and needs nothing.
- **PowerShell is case-insensitive**: a local called `$notes` *is* the `-Notes` switch. The same trap
  is documented in `tools/snapshots.ps1`.
- **A comment is not a section.** The generator only takes a title from a line with a run of dashes,
  because CUI's header is full of `/* dt is the time since the previous frame */`.
