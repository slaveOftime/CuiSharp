---
name: cui-sync
description: Sync the pinned CUI submodule to a newer upstream release and give every function it added a decided status — bump native/CUI, rebuild the native library, regenerate the binding, clear every PENDING in coverage.txt, then move the gallery, docs, numbers and gates with it. Use when bumping CUI, when bindgen stops on an unmigrated section, record or override, when coverage.txt gains a PENDING, or when a new CUI component has to reach C#.
---

# Sync CUI

CuiSharp binds CUI, a C library pinned in `native/CUI`. A sync is five moves in a fixed order, and
one of them — **deciding the status of everything upstream added** — is the whole job; the rest is
mechanism.

Read this file every time. The two references under `references/` are for the moments this file
points at them, not for the start.

| Reference | Read it when | Cost |
|---|---|---|
| [`references/bindings.md`](references/bindings.md) | bindgen stopped, or a new field/record/parameter needs an override. Every message → its fix, and the rule behind each. | one read |
| [`references/traps.md`](references/traps.md) | something looks like a generator bug, a gate fails oddly, or a wrapper misbehaves at run time. | one read |

## Where the sync stands

```sh
pwsh skills/cui-sync/scripts/sync-status.ps1
```

One command, no greps: the pinned tag against upstream's newest, version drift between
`Directory.Build.props` and the template, the coverage totals, and whether the numbers quoted in
`README.md` and `bindings/README.md` still match `coverage.txt`. It exits non-zero on a `PENDING` or a
stale number, so it is also the last thing to run.

## 1. See what moved, before reading any code

```sh
pwsh skills/cui-sync/scripts/cui-api-diff.ps1 -Notes     # pinned commit -> newest tag
```

It prints the commits, the functions, structs and header sections gained or lost, and upstream's own
changelog section for the release. Read that instead of the diff: the diff is 15 000 lines of C files
being split into phases and says nothing the binding answers for. **A sync with no API change is a
rebuild and a regenerate** — stop reading here and run steps 2 and 3.

```sh
cd native/CUI && git fetch --tags --all && git checkout <tag> && git describe --tags && cd ../..
```

## 2. Rebuild the native library

```sh
pwsh native/build-native.ps1        # cui.dll, cui.lib, runtimes/native-provenance.txt (cui-rev)
```

A locked `cui.dll` means an app is still running; close it. On Linux the CI path is
`native/build-native-container.sh`, which builds inside the glibc baseline image — local syncs on
Windows use the ps1, and the provenance file must name the new revision before you go on.

## 3. Regenerate — and expect the first run to fail

```sh
pwsh tools/regen.ps1 -NoNative -NoTest
```

The generator stops on the first thing it cannot justify, naming the declaration and the reason. Take
the message to [`references/bindings.md`](references/bindings.md), fix `bindings/overrides.json` or
`tools/bindgen.cs`, run it again. The loop is done when this prints `consistent`:

```sh
pwsh skills/cui-sync/scripts/sync-status.ps1     # exits 1 on a PENDING or a stale number
```

**Exit criteria for the loop** — every function upstream added or changed has exactly one of four
statuses, and each is a decision someone made:

| Status | Means | Who decides |
|---|---|---|
| `GENERATED` | the generator wrote the wrapper | automatic |
| `MANUAL` | a hand-written partial covers it, and `coverage.txt` names the member **and** the file | you |
| `EXCLUDED` | nothing wraps it, and the `why` is true | you, with a reason a reviewer would accept |
| `RAW-SKIP` | variadic: `cui_textf`-style, unreachable from a managed caller | automatic |

`PENDING` is not a status. It is unfinished work, and `CoverageAccountTests` fails the build on it.

If a function is a pure C helper with no `cui_ctx`, it is not a `Ui` member: write it as a static
helper beside its siblings (`src/CuiSharp/Ui/CuiMath.cs`, `CuiRegex.cs`, `CuiCron.cs`, `CuiHttp.cs`,
`CuiFiles.cs`, `CuiQuery.cs`) and register it in **both** `manualWidgets` and `excludedFunctions`.

## 4. Move the four things that follow the API

| What | Where | Check |
|---|---|---|
| the numbers in prose | `README.md` "Status", `bindings/README.md` "Current state" | `sync-status.ps1` compares them with `coverage.txt` |
| the public surface | `docs/API-SURFACE.txt` | `pwsh tools/api-surface.ps1`, then the set difference below |
| a gallery page | `samples/CuiSharp.Gallery/Pages/*.cs`, `PageCount` / `PageNames` / `PageIcons`, the `switch` in `GalleryApp.Shell.cs`, the row in `tools/gallery-pages.ps1` | the page draws and clicking it records nothing |
| screenshots | `docs/images/gallery/`, the page list in `tools/snapshots.ps1` | `pwsh tools/snapshots.ps1`; read one PNG |
| the authoring guide | `skills/cui-dotnet/SKILL.md` | a component a *user* meets for the first time gets a bullet; the reference under `api/` and `llms.txt` are generated |

Review the surface diff by **set difference**, never by reading 1 000 lines:

```sh
git show HEAD:docs/API-SURFACE.txt | sed 's/^ *//' | sort -u > /tmp/old.txt
sed 's/^ *//' docs/API-SURFACE.txt | sort -u > /tmp/new.txt
comm -23 /tmp/old.txt /tmp/new.txt     # removals: must be empty, or exactly what you meant
comm -13 /tmp/old.txt /tmp/new.txt | wc -l
```

A new section changes which extension class a member hangs off, so a big diff is normal — a member
that only moved appears in both sets and cancels out. A removal you did not intend is a wrapper you
lost.

## 5. Version, changelog, budgets

- New surface is a **minor** bump pre-1.0: `Directory.Build.props` `<Version>` **and** the template's
  `template.json` `defaultValue`; `TemplateVersionTests` pins the two together.
- `CHANGELOG.md` needs a section for the version being built or `ReleaseNotesTests` fails. Check it
  renders: `pwsh tools/release-notes.ps1 -Version x.y.z`.
- A budget that moved is a decision, not a warning: measure it, update the row in
  `docs/PERFORMANCE.md` / `docs/SIZE.md` **with the cause**, and say so in the changelog. Frame time
  in `docs/PERFORMANCE.md` must not move without the same treatment.

## 6. The gates, in this order

```sh
dotnet build  CuiSharp.slnx -c Release -warnaserror
dotnet test   CuiSharp.slnx -c Release
dotnet format CuiSharp.slnx --verify-no-changes
pwsh tools/regen.ps1 -NoNative -NoDiff          # + the generator's 15 contract checks
pwsh tools/api-surface.ps1                      # then the set difference in step 4
pwsh tools/verify-template.ps1                  # pack -> new -> build -> test -> AOT -> dotnet cui
dotnet publish samples/CuiSharp.Gallery -c Release -r win-x64    # zero IL2xxx/IL3xxx
pwsh tools/budget-check.ps1 -AotBytes <size>    # against docs/PERFORMANCE.md
pwsh tools/budget-check.ps1 -SizeCsv artifacts/size.csv -RequireAll
dotnet run -c Release --project src/CuiSharp.Benchmarks -- --fps 900
pwsh skills/cui-sync/scripts/sync-status.ps1    # PENDING 0, numbers fresh
```

Report what each printed. **A gate you did not run is untested, not passing** — and say which you ran
in isolation rather than as part of a full pass.

## Done means

1. `coverage.txt` has no `PENDING`, and every new function has a status you could defend to a reviewer.
2. `api.json`'s diff is understood: added functions wrapped or excluded *with a reason*, removed ones
   gone from `overrides.json` (a stale key fails the generator, so silence here is evidence).
3. `sync-status.ps1` exits 0.
4. The public surface diff is empty except what you meant to change.
5. The gallery draws any new component, and its page is in the snapshots.
6. Every gate above printed its own success line — not "probably fine".

## Hard rules

- `native/CUI` is **pristine upstream** (ARCHITECTURE D2). Never edit it, never add a patch; native
  behaviour belongs upstream. If you think you need one, stop and ask.
- **Never hand-edit a generated file** — `src/CuiSharp/Interop/*.g.cs`, `src/CuiSharp/Ui/*.g.cs`,
  `src/CuiSharp/Icons.g.cs`, `src/CuiSharp.Tests/*.g.cs`, `skills/cui-dotnet/{llms.txt,api/*.md}`,
  `bindings/{api.json,coverage.txt}`. Change `tools/bindgen.cs` or `bindings/overrides.json`, then
  regenerate.
- **One C function, one decided status.** An override is a decision with a `why` a reviewer accepts;
  `coverage.txt` is the record of that decision, not a progress bar.
- **Never guess ownership or length.** A pointer parameter does not prove a `ref` is safe, and a
  struct field's `int` is not automatically its array's length. Review the C, then express the
  decision as an override; `references/bindings.md` says which one.
- The frame path allocates **0 B**, and `Ui` is a `ref struct`: no fields, no capture, no boxing, no
  `await`. A sync that breaks either shows up as a failing test, not a warning — do not silence it.
