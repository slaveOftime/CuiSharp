# Changelog

Every user-visible change, newest first. Pre-1.0 the minor number carries new surface and the patch
carries fixes. The section for a tag is what the GitHub release publishes, so the two cannot say
different things.

## [0.4.0] — unreleased

A published binary carries only what it uses, and the package decides that for you.

### Changed

- **A size profile in the package.** `build/CuiSharp.Size.targets` compiles the headless channel and
  the framework diagnostics a shipped app never reads out of a **self-contained AOT publish**: the
  smallest app goes from 1 498 112 B to 1 189 376 B (−20.6%) and the gallery from 2 581 504 B to
  2 294 784 B (−11.1%). Migration: nothing to do, unless a published exe is driven by a tool —
  publish it with `-p:CuiSharpHeadless=true`, which is what `dotnet cui` now passes to the builds it
  runs itself. `dotnet run` is untouched: a build that is not a self-contained publish keeps the
  channel and the framework's exception messages.
- **`CuiSharpSizeProfile=false`** turns the whole set off; **`CuiSharpVerboseErrors=true`** keeps
  exception messages and stack traces while leaving the rest on. Both are properties, not API, and
  the measured values are in [docs/SIZE.md](docs/SIZE.md).
- **`dotnet new cui-mini`** sets `OptimizationPreference=Size` and `InvariantGlobalization=true` in
  the generated file with `#:property`, the two switches a file-based app cannot inherit from a
  project file. Its published single file is 1 159 680 B.
- **`dotnet cui` says what to do** when a published app never prints its banner, instead of only
  reporting that it waited.

## [0.3.1] — 2026-11-10

The date components speak another language, and the data grid owns its state.

### Added

- **Manual benchmark controls** keep BenchmarkDotNet suites and the frame-time measurement off
  push/PR checks; select `benchmarks` or `budget` when dispatching CI. Linux dependency setup skips
  already-installed packages and bounds apt/network waits without removing consumer verification.
- **IDE quick fixes for CUI002–CUI005** add `using`, guard conditional container bodies, key
  repeated widgets, and remove an awaited BCL `ConfigureAwait(bool)`. The CUI004 fix scopes the
  widget's actual receiver, preserves a using declaration's children, and only offers a foreach
  item or an unambiguous changing integer counter; nested loops and while/do loops need explicit
  keys. Rewrites that introduce compiler errors are refused. Overlapping scope fix-all and fixes
  for the other rules remain open. `CUI002` also catches a discarded scope in a value-less
  expression body. `CUI004` recognizes C# 14 extension members and ordinary Ui members, without
  treating plain display text or calls targeting existing ids as row-state collisions.
- **Windows/Linux ARM64 builds** select each RID's own native toolchain, archive, linker inputs and
  provenance, and reject an archive whose build stamp names another CPU. Verified on hosted
  `windows-11-arm` and `ubuntu-24.04-arm` runners: native build, full suite, NativeAOT render and
  packaged consumer all pass for `win-arm64` and `linux-arm64`.
- **`dotnet new cui-mini`** generates a file-based C# app with `#:package CuiSharp@*`:
  `dotnet new cui-mini -n demo1` creates only `demo1.cs` in the current directory, without a new
  folder. `dotnet run demo1.cs` opens a native window with a counter and theme switch, no project
  or solution. An optional `-o <folder>` selects a separate output directory.
  `--CuiSharpVersion` pins the package when needed. The template gate checks the generated file's
  headless button interaction and, on Windows, observes its visible window opening and closing.
- **`Localization` and `ui.SetLocalization(table)`** translate the month, weekday and "Today" text
  the date picker, the calendars, the gantt and the file browser show — the table `cui_set_strings`
  takes, which stayed `EXCLUDED` in 0.3.0 because CUI keeps the pointer and reads it every frame.
  A `Localization` object holds the managed names (twelve months, their short forms, three weekday
  forms and `Today`, each entry nullable so a partial table falls back to English) and owns one
  unmanaged block, rebuilt only when a name changes; the surface keeps the object alive the way it
  keeps the theme, and `ui.Localization` reads back what is published. `null` restores English, and
  a table with nothing in it costs no block at all. One call is enough — a name changed afterwards
  reaches the next frame by itself — publishing the same table every frame allocates 0 B, and a
  table disposed while it is still published is freed at the frame boundary that follows, never
  under the frame reading it. A table belongs to one surface: publishing it to a second one throws.
- `GridState` and `ui.DataGrid(id, columns, rowCount, state[, body], options)`: persistent selection,
  visible range, sorting and keyboard state, with a `GridScope` that writes interactions back when
  it closes, including on exceptions. CUI002/CUI003 enforce disposing and checking this scope.
- `ui.Segmented(id, items, value)` returns the new index on a change, otherwise `null`. The `ref`
  form remains available.

### Changed

- **Breaking:** DataGrid's six-`ref` form is replaced by `GridState`. The old wrapper supplied one
  selection byte for all rows and retained stack pointers beyond the begin call. The new wrapper
  owns one byte per row and keeps all native state in the frame arena until the scope closes.
- **Breaking:** `QueryField.ChoiceCount`, `ApiEndpoint.ParamCount`, `UptimeService.DayCount`,
  `StackFrame.SourceCount` and `FilterField.OptionCount` (including constructor arguments) are
  removed. Their native counts derive from their companion arrays; explicit `recordFieldCounts`
  overrides identify these input-only records, leaving result counts intact.

## [0.3.0] — 2026-11-10

CUI 1.3: the developer tools it added are wrapped, and nothing is left `PENDING`.

### Added

- **The twenty developer tools CUI 1.3 ships**, generated from the header: `TraceView`,
  `Waterfall`, `GraphView`, `CommitGraph`, `Pipeline`, `Problems`, `KvEditor`, `QueryBuilder`,
  `SchedulePicker`, `Keymap`, `TestExplorer`, `StackTrace`, `Blame`, `Coverage`, `RegexTester`,
  `RequestBar` with `ResponseStatus`, `ApiExplorer`, `FeatureFlags`, `Uptime` and
  `TimeHistogram`. Each returns a typed record (`ProblemsResult`, `PipelineResult`, …) instead of a
  flags integer, and each is a gallery page: *Developer tools* and *More developer tools*.
- **The pure helpers behind them**, usable with no `Ui` and no frame: `CuiRegex.TrySearch` returns
  the match with its named groups; `CuiCron.TryNext` the next run as a `DateTimeOffset`;
  `CuiHttp.ParseQuery`/`SetQuery`/`Reason`/`MethodName` keep a URL's query in step with a table;
  `CuiQuery.Text`/`Match`/`OperatorName` read and test a filter; `CuiFiles.SortTraceSpans` orders
  spans for the trace viewer.
- `ui.CronDescribe(cron)` and the `KeyValue`, `QueryField`, `QueryNode`, `FeatureFlag`, `Commit`,
  `ProblemItem`, `ApiEndpoint` and the rest of the new records: a field CUI keeps in a fixed buffer
  (`char value[64]`, `bool on[8]`) is text or an array to the caller, copied in and out.
- The month, weekday and "Today" names CUI 1.2 put in one place are reachable only through the raw
  layer for now: `cui_set_strings` keeps the pointer it is given and reads it every frame, so the
  wrapper needs storage that outlives the call. `cui_set_strings`/`cui_get_strings` are `EXCLUDED`
  with that reason, and the planned `Localization` owner is in [PLAN](docs/PLAN.md).

### Changed

- **`CurveEditor`, `GradientEditor`, `KvEditor` and `QueryBuilder` take their count by `ref`.** They
  copy the array back after the widget runs (the widget adds, removes and toggles entries), and the
  count is part of that: passing it by value silently dropped the new length.
- **`FeatureFlags` no longer takes a count.** `cui_feature_flags_ex` reads one, and it can only be
  the length of the array the wrapper just filled, so `ui.FeatureFlags(id, flags, envs)` derives it.
  The same applies to every array a widget edits: a by-value count is the array's length.
- **`Keymap`, `RegexTester` and `RequestBar` take your enums, not an `int`.** `ref Mods mods`,
  `ref RegexFlags flags` and `ref HttpMethod method` replace the `ref int` parameters CUI's header
  describes, so a page stops casting at the call site.
- `SchedulePicker` and `RequestBar` default their buffer size (`cap = 128` and `2048`), as
  `FileBrowser` and `FilePicker` already did.
- **Extension classes follow the header's own sections.** CUI marks its sections twice — a rule
  above and below a title, and a title inside a single rule — and the generator read the second
  form as a bare rule, so a whole run of sections landed in `""` and, earlier, several were merged
  into one class (`AppChromeMenuBarToolbarStatusBarDocumentTabs`). Every member is still reachable
  through `using CuiSharp;`; only the class a member hangs off changed, and the new classes are
  named for the section they came from (`DockingUiExtensions`, `TerminalUiExtensions`, …).
- The wrapper count is now **278 generated + 167 hand-written, 0 pending** across 492 C functions,
  with 166 option structs and 100 records ([coverage](bindings/coverage.txt)).
- **The native library is bigger**: the twenty components are ~5 000 lines of new C, so the gallery's
  NativeAOT executable goes from 2 141 184 B to 2 577 920 B and the written ceiling with it
  ([PERFORMANCE](docs/PERFORMANCE.md)). Frame time is unchanged at 0.82 ms and 0 B per frame.

### Fixed

- **The regex tester searched its text in itself.** A wrapper keyed its editable text by the
  widget's id, and the tester edits two strings at once (the pattern and the text), so both were
  handed the same pooled buffer: the second overwrote the first and CUI matched the pattern against
  the pattern. The key is the widget id mixed with the parameter, and the pooled buffer grows when a
  caller asks for more room than it has.
- **The gallery's time histogram allocated on every frame** (three `float[24]` and the series array)
  and kept its selection in a local, so a drag was undone on the next frame. Both are page state now.
- **A helper that borrows the frame arena gives the bytes back.** `CuiRegex`, `CuiCron`, `CuiHttp`,
  `CuiQuery` and `CuiFiles` are documented as usable without a frame, and nothing resets the arena
  outside one: a page that parsed a URL on every click grew it by the click. Each now restores the
  arena it found, and `DeveloperToolTests` holds them to 0 B over a hundred calls.
- **`CuiRegex.Slice` no longer re-encodes the whole text per call**, and takes a `RegexGroup`
  directly, so marking every group of a match costs one substring each. Text that is not ASCII is
  mapped through the runes instead of a throwaway `byte[]`.
- **An edited array is copied back only on the frame the widget reports an edit.** Converting a
  record costs an allocation per string and per fixed array it holds, and the wrappers were
  converting every record on every frame: `FeatureFlags` cost 160 B and `KvEditor` 128 B for a
  steady frame that changed nothing. Both are 0 B now, and `DeveloperToolTests` holds all twenty
  tools to it.
- A record field CUI keeps in a fixed C array is copied both ways: a `FeatureFlag`'s switches, a
  `Commit`'s parents and a `KeyValue`'s key and value need no hand-written wrapper, and the nested
  form (`char group_name[10][24]`, in the regex matcher) no longer stops the generator.

## [0.2.0] — 2026-11-10

### Fixed

- **Window resize keeps up with the mouse.** A drag asked for two full frames and two display waits
  per mouse step, inside the OS modal loop, so the window fell behind the cursor. Now one frame per
  coalesced paint, with the display wait dropped for the length of the drag.
- `OnInitialized` runs on the first frame that draws the component, as documented — it used to run
  before the window existed under `CuiApplication`, so branding set there did nothing.
- A missing or mismatched native library says which RIDs were packaged, the glibc floor and the fix,
  instead of a bare loader message.
- A click no longer leaves its tooltip in `SavePng`; the pointer is parked first.
- `CUI001` also reports `File.*`, `Directory.*` and `Task.Run` inside a frame.

### Added

- `CuiWindow` — the native handle, backend, renderer, size, title, minimize, maximize and close —
  on `CuiApp.Window`, `CuiApplication.Window` and `CuiAppOptions.WindowCreated`.
- `HeadlessApp.Advance(TimeSpan, frames)`, `Settle()`, `Animating` and `ParkPointer()`: animations
  settle without a sleep loop, and a snapshot no longer needs the pointer moved by hand.
- `FindById(id)` and `FindItem(id, index)` on the test harness, `id:` in a query, and `Id` on
  `SemanticWidget`/`SemanticNode` — narrow enough to survive translation.
- `ui.UseSystemFontFallback("中文")`: finds the face this machine has for a script the bundled Latin
  faces do not cover, and registers it. Call it once at startup.
- The package ships `skills/cui-dotnet/` — the guide, the reference, the API pages, CUI's component
  documentation, the hosting guide and the ABI promise — at the path it has in the repository.
- `CuiWindow.PickFile` and `CuiWindow.OpenUrl`: the OS file/folder/save dialog, which had no managed
  API at all, returning `FilePickResult` (`Picked`, `Cancelled`, `Error`) instead of a bool and an
  empty string. `IFileDialogProvider` replaces it in a test, so a picker flow runs with no display.
- `CuiDiagnostics.Report()`: platform, backend, renderer, display, dialog helper, ABI floor. The
  template prints it on `--check`.
- `GoldenPng.Compare(app, path, tolerance)`: a checked-in image with a tolerance instead of an
  exact match; `CUI_UPDATE_GOLDENS=1` accepts a deliberate change.
- `CUI008`: warns on reflection-based `JsonSerializer` calls, which Native AOT trims away.
- `tools/budget-check.ps1` reads the frame-time and binary-size ceilings out of
  `docs/PERFORMANCE.md`; CI runs the size check on every pull request and the frame check on demand.

## [0.1.0] — 2026-11-09

First release: the binding, the runtime, hosting, analyzers, a template, and a way to drive a real
app from a shell.

### The library

- `net10.0` only, NativeAOT and trim-clean, one package: `CuiSharp`.
- Every CUI function has a decided status: 356 generated wrappers, 157 deliberate hand-written
  ones, 4 excluded with a written reason, 0 pending ([coverage](bindings/coverage.txt)).
- Idiomatic generated API: `UiText` everywhere, `ReadOnlySpan` for arrays, `ref` for values,
  `LayoutScope`/`OpenScope` for containers, typed results (`InputResult`, `TreeTableEvents`) instead
  of flag integers.
- Hand-written controls the generator cannot infer: text input with `ref string`, charts with
  automatic decimation, `Canvas` and the drawing primitives, `Terminal`, `LogBuffer`/`LogView`,
  `HexView`, `TreeTable`, per-id `State<T>`, `Api`/AI-interfaces, app chrome, planning boards.
- Steady-state frames allocate **0 B**; the gallery draws in 0.85 ms at 1240×820 (`--fps 900`).

### Hosting

- `CuiApplication.CreateBuilder(args)` — generic host, DI, one `Run<TRoot>()` on the calling thread.
- `Component` with an inline error boundary, `Mutation`/`Mutation<T>` for async work that publishes
  its result at a frame boundary, `WantsFrames` for pages that change on their own.
- `FrameStats` reports what the frame that just drew cost — `DrawMs`, `FrameMs`, `AllocatedBytes`,
  and `Fps`/`IsLive`, which stay honest across the sleeps an idle window takes. `CuiSurface.LastFrame`
  is the same sample for one surface, for tests.
- `ui.Theme`, `ui.Accent` and `ui.ToggleTheme()` — the theme is CUI's, so switching it is an
  assignment inside a frame rather than a dirty flag plus a wire-up. (`CurrentTheme`, `CurrentAccent`,
  `SetTheme` and `SetAccent` are gone.)

### Testing

- New package `CuiSharp.Testing`: `TestApp.Start(component)` drives the real component with no
  window — `FindButton("Save").Click()`, `Type("Milk")`, `Faults`, `PaintedAnything()`, `SavePng()`.
- `app.Tree` / `app.Print()` show the frame as the app drew it, nesting included.
- A tab strip and a segmented control record each item, so `Find("Segment", "Dark")` and
  `dotnet cui click Markets` address the item instead of the middle of the strip. Recording is still
  free: the text of a frame is read when it ends, so a recorded frame reports 0 B as well.
- `CuiSharp.Tools` writes a redirected stdout as UTF-8, so a label with a non-ASCII character
  arrives intact in a pipe, a file, or an agent.

### Tools

- New package `CuiSharp.Tools`: `dotnet cui tree|find|click|type|key|focus|move|frame|faults|png`,
  with `--stdin` for a whole session, `--app` for a published build and `--no-build`.
- Any app is drivable: `CUISHARP_HEADLESS=1` makes `CuiApp.Run` serve the protocol instead of
  opening a window, so there is no debug mode and no second code path. Apps that want the bytes
  back set `CuiSharp.Headless=false`.

### Analyzers

- Seven rules ship inside `CuiSharp`: blocking call in a frame, dropped container scope, unread
  `Open`, constant id in a loop, `ConfigureAwait(false)`, `async void`, allocation in a frame.

### Templates

- `dotnet new cui-app` scaffolds a solution: a two-page app (todos and a simulated market feed) with
  a light/dark switch, a frame-rate and allocation footer, a test project that clicks through real
  frames, and an `AGENTS.md`.

### Breaking changes since 0.1

Nothing was published before this version.

## How to read this file

- Added / Changed / Fixed / Removed, user-facing only. If a change needs a code edit in a user's
  app, it says which.
- A number in this file must come from a gate in [docs/RELEASING.md](docs/RELEASING.md), not from a
  one-off run.
