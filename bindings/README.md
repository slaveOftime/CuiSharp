# bindings/

| File | Purpose |
|---|---|
| `api.json` | Generated IR: raw signatures, structured C types (pointer, const, bool, typedef), header sections/comments, struct sizes and offsets, enums and icons. Review its diff when updating CUI. |
| `overrides.json` | Generator input: exceptions the header cannot express. Edit this or the generator, never generated output. |
| `coverage.txt` | Generated status for options, records and functions. `PENDING` is a real migration gap; `MANUAL` is a deliberate hand-written partial with a reason; `EXCLUDED` means nothing wraps it — it has to say why. `RAW-SKIP` is a variadic function a managed caller cannot express. There are no `PENDING` entries. |

## Regeneration and checks

```sh
pwsh tools/regen.ps1                    # native build, generator, checks, build, test
pwsh tools/regen.ps1 -NoNative -NoDiff  # current artifacts; fail if output changes
# Isolated generator-contract tests; no generated files written:
dotnet run tools/bindgen.cs -- --self-test
```

`-NoDiff` hashes the IR, coverage, raw bindings, every generated UI module,
icons and generated tests before/after. New UI output files are detected too.
Contract checks use isolated IR/config copies to verify new-field mapping,
invalid overrides, ambiguous pointers, scope pairing, derived counts and
retained-pointer storage.

## Generated outputs

| Output | Contents |
|---|---|
| `src/CuiSharp/Interop/Native.g.cs` | `[LibraryImport]` for every non-variadic CUI function. This is the unsafe, low-level surface. |
| `src/CuiSharp/Interop/Structs.g.cs` | Blittable structs: C bools as bytes, pointers as addresses, fixed buffers and inline arrays. |
| `src/CuiSharp/Interop/Enums.g.cs` | Named enums, shared C prefixes removed. |
| `src/CuiSharp/Interop/Constants.g.cs` | Anonymous-enum values under their C names. |
| `src/CuiSharp/Icons.g.cs` | Embedded icon glyphs under PascalCase names (`Icons.Check`). |
| `src/CuiSharp/Ui/Options.g.cs` | Friendly options and allocation-free `ToNative()`, with header comments. Array counts derive from managed arrays; bool/Size/Color/string conversion is mechanical. |
| `src/CuiSharp/Ui/<Section>.g.cs` | One C# 14 `extension(Ui ui)` block per header section. Text uses UiText, item arrays use spans, values use ref, begin/end pairs use scopes, and each wrapper records itself for the semantic tree. |
| `src/CuiSharp/Ui/ScopeEnds.g.cs` | Native scope-end dispatch, derived from the exported functions. |
| `src/CuiSharp.Tests/Abi.g.cs` | Managed sizes/offsets against libclang. |
| `src/CuiSharp/Ui/Records.g.cs` | Data records used as array items (`Choice`, `Command`, `TableColumn`, `TableSort`): friendly constructor, `ToNative()`, and `FromNative()` where write-back is meaningful. |
| `src/CuiSharp.Tests/OptionMappings.g.cs` | One consolidated test covering every generated option's populated fields, sentinel and null/false defaults. A swapped field is a single generator bug, so it is one test, not one per option. |

Current state: **166** option structs, **100** data records, **278** generated wrappers, **167**
deliberate hand-written ones, **6** excluded, **4** variadic skipped, **0** pending — 492 C
functions, every one with a status. Pointer-heavy options are withheld rather than exposed with guessed
ownership. Charts, texture handles, text-buffer inputs and reorder are hand-written on purpose, and
`coverage.txt` names the member and the file for every one of them.

## Raw naming/configuration keys

| Key | Meaning |
|---|---|
| `enumRenames` / `structRenames` | Rename raw enum/record types. |
| `memberRenames` | Member spelling such as `OpenGL`. |
| `extraEnumMembers` | C# platform member `Mods.Primary = Ctrl`. |
| `anonymousEnumNames` | Promote a C anonymous enum, keyed by its first constant. |
| `flagsEnums` | Enums with `[Flags]`. |
| `skipFunctions` / `skipStructs` | Explicit raw exclusions. Variadic functions are reported automatically. |
| `suppressGcTransition` | Reviewed leaf calls only: never callbacks or blocking. Empty until reviewed. |

## Idiomatic exception keys

Every entry below is an object with a non-empty `why`. Unknown declarations
and missing reasons fail generation.

| Key | Entry fields | Meaning |
|---|---|---|
| `typeAliases` | `name`, `why` | `cui_font` is an int typedef separate from its role constants; map it to Font. |
| `optionNames` | `name`, `why` | Friendly option name, e.g. Card rather than CardOpts. |
| `optionFieldNames` | `name`, `why` | Combined-word casing, e.g. NoWrap / AutoFocus. |
| `parameterDefaults` | `value`, `why` | Defaults not described by the signature, e.g. Heading level 2. |
| `injectedFields` | `type`, `why` | Internal native value provided by a wrapper, not a public dangling address. TableSort remains wrapper-owned. |
| `parameterStorage` | `storage`, `why` | `frame` for ref primitives whose addresses native code retains; `value` for native records converted through `UiMarshal` and written back. |
| `scopes` | `end`, `clicked`, `why` | Non-standard pairs, or a bool that means clicked rather than open. |
| `recordNames` | `name`, `why` | Native structs used as `const T *` array items, e.g. `cui_choice` to `Choice`. |
| `outParams` | `why` | Parameters the header documents as filled in; the wrapper takes `out` instead of `ref`. |
| `injectedFields` | `type`, `param`, `why` | Wrapper-owned native value such as TableSort; `param` names the `ref` parameter. |
| `optionalInjectedParams` | `why` | Pointer to an injected record a caller may legitimately not have (an unsorted table has no sort state). The wrapper also emits an overload that passes a null pointer. |
| `excludedModules` | `why` | Sections that never become `Ui` members: frame lifecycle, platform input, app, renderer, draw data, identity, theme, fonts, drawing primitives. |
| `arrayLengths` | `length`, `cast`, `emit`, `why` | A `const T *` parameter whose length is an expression (`rows * cols`) or a differently-named parameter (`size`). `emit: false` means native derives it and the wrapper only checks it. |
| `arrayParams` | `why` | A pointer parameter that is an array although its name is singular, e.g. `const cui_swipe_action *left` (`name@function`). |
| `inOutArrays` | `count`, `why`, `changedWhen` | A record array the widget edits, plus the count it may change: copied in, called, copied back. `changedWhen` is the condition the widget reports an edit by (`__result != 0`, `__result.Changed != 0`, `__result.Flag >= 0`) and the copy back only runs then, because converting a record allocates the strings and fixed arrays it holds. |
| `textFields` | `capacity`, `why` | A `char * buf` pair the caller sees as one `ref string`; the wrapper rents a pooled buffer and reads the text back only when it changed. |
| `optionPointers` | `type`, `why` | An option field that is a pointer to a primitive (`int *`, `_Bool *`), exposed as a `ref` parameter. |
| `optionArrayChecks` | `atLeast`, `why` | An option array whose length is a widget parameter; the wrapper rejects a short array before the call. |
| `scopeResults` | `type`, `out`, `why` | A `begin` whose `end` reports a value: the wrapper emits the body overload and returns it. |
| `stringResults` | `why` | A function that returns a C string; the wrapper copies it. |
| `excludedFunctions` | `why` | One C function deliberately not wrapped: the pure helpers (diff computation, date maths, the log writer) belong in a static API, not on a frame-local `Ui`. |
| `hiddenOptionFields` | `why` | An option field that never crosses the boundary: a callback (`cui_file_list_fn`, the tree table's label) or its `void *`. Left zero, so native uses its own default. |
| `optionFieldCounts` | `count`, `why` | An option array whose count field is not `x`/`x_count`, e.g. `meter` ↔ `meter_channels`. |
| `optionTextFields` | `capacity`, `why` | An option `char *` buffer the widget reads and writes: a `ref string` beside the options, backed by the pooled text buffers. |
| `constantArgs` | `value`, `why` | A parameter the wrapper fills itself, e.g. a length of `-1` for a string it already NUL-terminated, so the caller never sees it. |
| `manualWidgets` | `why` | Reviewed manual partials, distinct from unresolved PENDING functions. |

### What the generator writes

Code: `src/CuiSharp/Interop/*.g.cs`, `src/CuiSharp/Ui/*.g.cs`, `src/CuiSharp/Icons.g.cs`,
`src/CuiSharp.Tests/Abi.g.cs`, `src/CuiSharp.Tests/OptionMappings.g.cs`.

Reference: `skills/cui-dotnet/api/<section>.md`, `.../api/Options.md`, `.../api/Records.md`, and
`llms.txt` — the same plans that emit the code emit the documentation, so the
reference cannot describe an API that does not exist. `bindings/coverage.txt`
accounts for every C function: `GENERATED`, `MANUAL`, `EXCLUDED` or `PENDING`, each
with a reason, and a self-check fails if the counts stop adding up.

The public surface itself is checked by a different gate: `docs/API-SURFACE.txt`
plus `PublicSurfaceTests`, refreshed with `pwsh tools/api-surface.ps1`.

### Retained pointers require source review

A C pointer parameter does **not** prove a ref wrapper is safe. For example,
`cui_split_begin` retains `ratio` until `cui_split_next` reads it. Pinning a
managed field for just the begin call would leave a dangling address after GC.
The explicit storage override copies the ratio into the frame arena, writes
back changes made during begin, and keeps that snapshot valid for SplitNext.
Changes to application state inside the body take effect next frame.

Prefer a reusable convention over an exception, but never guess ownership or
length. Review source, add the reason, and verify observable native behavior.
