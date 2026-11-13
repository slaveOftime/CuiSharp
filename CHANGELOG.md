# Changelog

Every user-visible change, newest first. Pre-1.0 the minor number carries new surface and the patch
carries fixes. The section for a tag is what the GitHub release publishes, so the two cannot say
different things.

## [0.4.2] — 2026-11-13

### Added

- **`dotnet cui -a` drives a window on every backend.** Live attach threw anywhere but Windows, so
  `-a` has meant "on this one OS" and both templates asked for it under a platform condition. What
  Windows uniquely offers is a safe point between frames and a way to reach a sleeping message loop,
  not the ability to drive the window itself: every backend now runs the boundary at the tail of its
  draw callback — the step that ends the frame, which is what lays it out and gives the recorded
  widgets their rectangles — before the next `cui_begin_frame` consumes the input it injects. Nothing
  outside the window thread can wake the other backends, so the frame that just drew asks for the
  next one while a command is pending and for a 500 ms heartbeat when the queue is empty. That
  heartbeat is the only idle cost of an attached window, and the reason a command reaching a
  sleeping one can take half a second. Injecting mid-frame was the other option, and it loses Tab
  navigation: `cui_begin_frame` reads the key state before the callback runs, so the key arrives
  after the frame that should have acted on it. Both templates now set `LiveAttach` unconditionally;
  the file-based one cannot gate it on `DEBUG`, because `dotnet run` builds a file-based app in
  Release.

### Changed

- **Pixel capture says so when it cannot.** `png -a` still needs `cui_app_screenshot` and the
  `cui_app*` the app loop keeps private, which only win32's window user data hands back; elsewhere
  it now answers with that reason instead of faulting inside native code, and the refusal does not
  poison the next capture. The tree, the rectangles and every input step work on all platforms.
- **A close request is handled at the frame boundary**, not on the pipe thread touching CUI's
  current-app pointer and running flag, which the UI thread's teardown could free underneath it.
- **The starter app names its shell regions `header`, `page` and `footer`.** Unnamed containers bump
  the recorded depth without recording a node, so on a freshly generated app the page content nested
  under the header's theme `Segmented` in `dotnet cui tree`, and a path query could not reach a
  region by name.
- **The two documents a generated app ships now have one job each.** The README owns the commands and
  the structure — one command is one process, a session keeps its lines in one app, `-a` needs the
  window `LiveAttach` opens, and a published exe keeps a channel only if it was published for it.
  `AGENTS.md` owns how to work: the four edits a new screen really takes, the six immediate-mode
  rules, and how the allocation gate and the input helpers are used. The repetition is where they had
  started to disagree — the README named a `TestApp` harness that `dotnet cui api` cannot resolve, and
  `AGENTS.md` said a new screen was one entry in `App.cs`.

### Fixed

- **The path grammar in the docs is the grammar `WidgetQuery.Parse` accepts.** Every doc comment and
  generated sample that showed a path wrote `Card "editor" > Button "Save"`, and since only the final
  step of a path keeps a kind, the example as printed never resolved: a reader copying it was told
  "No widget". Paths now print `editor > Button "Save"`, containers bare, and `SemanticNode.Path`
  keeps the readable `Card "editor" > …` while saying that a query drops the container kinds.
- **`dotnet new` no longer copies `bin/` and `obj/` into the generated app.** Those excludes belong in
  `sources.modifiers`; a source-side glob is not read for them.
- **The template's 0 B-per-frame gate can fail.** It measured a raw `CuiSurface.Draw`, so a page that
  allocated only in the hosted path passed, and an error boundary that draws an alert allocates, so a
  throwing page could pass by spending 0 B. It now measures `CuiSurface.LastFrame.AllocatedBytes`
  around `HeadlessApp.Frame()`, takes `prepare:` to drive a page into the state worth measuring and
  `verify:` to assert it survived the measured frames, and asserts no fault after every frame. The
  starter tests mirror `Pages/` and `Services/` and share one `TestHarness.cs` instead of five flat
  files each re-declaring the same fixtures.

## [0.4.1] — 2026-11-12

### Fixed

- **A hot reload reaches the screen without touching the window.** An idle CuiSharp window sleeps
  inside the OS message loop, so an edit applied by `dotnet watch` was live in the process and
  invisible: the window kept presenting the frame it drew before the edit until the user moved the
  mouse. The library now registers itself with the runtime's `MetadataUpdateHandler` — `ClearCache`
  before the delta, `UpdateApplication` once it is live — and wakes the window with a posted message
  that the win32 backend's wait already listens for. No polling and no idle cost: a window that is
  neither animating nor reloaded still draws nothing. A backend that cannot be woken from another
  thread keeps a 500 ms idle cap while `dotnet watch` runs the app.

## [0.4.0] — 2026-11-12

CUI 1.3.1 → 1.4.0. Four functions, four components, and one option array the generator had never
had to express.

### Added

- **CUI's own documentation, wherever it wrote it.** A wrapper used to carry only the sentence above
  the declaration, so an options field, an enum constant or a function documented *beside* its
  declaration reached C# with nothing at all. The generator now reads all four places upstream
  writes in — above the declaration, beside it, above the definition in the C source, and the
  component's description above its options struct — and the result is in the package's IntelliSense
  file, not just in the source: 1 200 option and struct fields that said `const char * unit` now say
  what the field means, 112 wrapped functions gained a sentence (206 down to 94 that have none),
  every constant that CUI comments on says what it is for (`AxisUnits.Hours`: *hours shown as a time
  of day*), and options structs open with the component's description instead of `cui_split_opts`
  (`BlameOpts` now says what Blame is). A section title bar is recognised and dropped rather than
  shown as if it were prose, and CUI's notes to its own next reader — `left to right in any
  interface: see cui__keep_ltr`, written above fifteen thin wrappers — are recognised as such and
  never used as a summary; those wrappers take the description from the header instead.
- **The instruments**: a group of components that read like a panel rather than a page.
  `DotChart(id, series, count, opts)` draws values as a grid of dots with the pointer or the arrow
  keys reading the value under them, and takes `ChartAnnotation`s drawn over it as lines,
  thresholds and labelled points; `EventTrack(id, times, ref count, min, max, ref selected, opts)`
  puts moments on a time axis that a click adds to, a double-click removes and a drag moves, and it
  lines up under a dot chart of the same width; `Readout(label, value, opts)` is one figure with
  its unit and a line of context; `DotDisplay(text, opts)` draws text in a 5 by 7 dot font that
  shrinks to fit or scrolls. The gallery's *Instruments* page shows all four on one screen.
- **`CuiAppDesc.AppId`** names the app to a Linux desktop: the window's `WM_CLASS`, and the Wayland
  app ID, so the dock, the taskbar and `Alt+Tab` show the app's own name and icon instead of `CUI`.
- **`Ui.DotChart` takes `ReadOnlySpan<DotSeries>`**, the way every other chart takes its series, so a
  call site can stack the series inline (`[new(values, "Caffeine", accent: true)]`) and allocate
  nothing per frame. The signature change is the one public-surface edit in this release.
- **Generated documentation.** CUI 1.4 documents every field of every options and data struct in
  the header, so 208 generated wrappers that could only say the C function's name now say what the
  call does, and the option fields carry the header's own words.

### Changed

- **A capacity is the array's length, so it leaves the signature.** `CurveEditor`, `GradientEditor`,
  `KvEditor`, `QueryBuilder` and `EventTrack` no longer take a `capacity`: the wrapper passes the
  span's length. Supplying both let them disagree, and a capacity larger than the span was a
  buffer overrun the type system did not catch.
- **A count beside a record array is now the array's length.** `DotChartOpts.Annotations` and every
  other option struct with a record array derive their count, as string and float arrays already
  did; the field is no longer public, so the two cannot drift apart.
- **An in-place primitive array is copied back every frame**, not only on the frames the widget
  reports a change: native sorts and clamps the array it is given before it draws, and a wrapper
  that kept the caller's unsorted copy would disagree with the index it reports. Converting a
  record still only happens on a change, because that is the conversion that allocates.

## [0.3.3] — 2026-11-12

### Added

- **`dotnet cui docs`** prints the guide, the reference and the behaviour pages that ship *inside*
  the tool: no repository, no network, nothing built and nothing started. `docs read <topic|doc|doc#section>`
  reads one of fourteen task topics or any page whole, `docs search <words>` ranks the sections that
  answer a question and names the read that opens each one, and `--json` makes any of them
  machine-readable. The topics are pointers into the shipped pages, so the tool cannot say anything
  the documentation does not.
- **`dotnet cui session`** relays one protocol command per line from stdin against a single build,
  replacing the `--stdin` flag.
- **A demo icon in both templates.** `dotnet new cui-app` ships `<Project>.ico` wired in with
  `ApplicationIcon`, and `dotnet new cui-mini` generates `<name>.cs` plus a matching `<name>.ico`;
  replacing the file and rebuilding changes the exe icon and the icon on the title bar, taskbar
  button and `Alt+Tab` card. The shipped icon is a black square reading CUI.

### Changed

- **The command line is declared with System.CommandLine**, so `dotnet cui <command> --help` and the
  parser cannot disagree. What the old hand-written help promised and the parser ignored now works:
  `-a <pid|title>` takes its value, `-t/--target` is gone in its favour, `--version` answers with a
  version, and a mistyped verb is answered with the verb that was meant (`clik` → ``Did you mean
  `dotnet cui click`?``). Usage errors exit 2 with the reason on stderr; a failed launch exits 1 with
  one sentence instead of a stack trace.
- **Options belong to the commands that honour them.** `docs`, `api`, `icons` and `ps` answer
  offline and declare no `--project`, `--app`, `--attach`, `--no-build`, `--frames` or `--wait`, so
  they cannot be given one that would be ignored. Options follow the command they belong to:
  `dotnet cui tree --frames 2`, not `dotnet cui --frames 2 tree`.
- **A query is one quoted argument.** `dotnet cui click "Button Save"` is a click on the button
  labelled *Save*; the unquoted `dotnet cui click Button Save` was two words and is now a usage
  error rather than a different query. `dotnet cui click "editor > Save"` quotes the path separator
  that the shell would otherwise read as redirection.
- **`dwell` and `waittext` require their duration**, which the protocol always needed: bare `dwell`
  used to start an app and then fail with an unknown command.

### Fixed

- **The embedded icon reaches the window.** An apphost embeds `ApplicationIcon` as `IDI_APPLICATION`,
  which Explorer reads off the exe but the CUI window class does not find, so a published app wore
  the system's generic logo in the title bar and taskbar. `CuiApp` now loads that resource and sends
  `WM_SETICON` itself when the window opens, small and large, at the system's icon metrics.
- **`dotnet new cui-mini` no longer opens a console window.** The generated file-based app now
  sets `#:property OutputType=WinExe`, so `dotnet publish` produces a GUI-subsystem executable
  like the one `dotnet new cui-app` builds; `tools/verify-template.ps1` reads that subsystem out
  of the published binary.

## [0.3.2] — 2026-11-11

The gallery now draws everything CUI ships, and a published binary carries only what it uses.

### Added

- **Fifteen gallery pages** port the rest of `examples/gallery_more.c`: actions, smart inputs,
  pickers, capture, disclosure, reader, data grid, code, social and media, planning, more charts,
  motion, AI, audio and files. The gallery is twenty-eight pages, one file per page, and
  `tools/snapshots.ps1` clicks and verifies all of them.
- **`wheel <x> <y> <dy> [dx]`** in the headless protocol and `dotnet cui`: scroll at a point, in
  wheel notches. It is how a control below a scroll area's fold is reached before it can be
  clicked — `HeadlessApp.ScrollAt` under it.
- **`pulse [count]`** draws frames without printing the tree — a sampler should not pay for
  serialising the interface it is measuring — and answers with the frame count and the app's
  managed heap size.
- **`dwell <seconds>`** in a `dotnet cui --stdin` session: the tool keeps the app drawing for
  that long and answers with one `STATS` line of wall and cpu milliseconds, frames, fps, heap,
  working set and private bytes.
- **`tools/page-costs.ps1`** walks all twenty-eight gallery pages with a three-second dwell each
  and samples the app's process; the page-by-page table in `docs/PERFORMANCE.md` is its output.
- **`ui.CreateImage` / `ui.LoadImage` / `ui.DestroyImage`** carry `cui_image_*` as typed handles
  instead of raw ints; the cropper, viewer and compare widgets take and return them.
- **`ui.ToggleGroup`, `ui.MultiSelect` and `ui.FilterBar`** wrap the controls that write N
  booleans through one pointer — the generator cannot express them, so they are `MANUAL`.
  `ui.ImageCompare` and `ui.Waveform` keep the caller's values across a frame (`ref`, not the
  zero-on-every-frame `out` the generator would have produced).
- **`ui.DroppedFileCount` / `ui.DroppedFile(i)` / `ui.FileDragActive`** read the files an OS drag
  dropped on the frame and say whether one is over the window right now.
- **The `int` a widget returns for its own events is now an enum.** `ui.NowPlaying`,
  `ui.ChatComposer`, `ui.ResponseActions` and `ui.Comment` return `PlayerEvents`,
  `ComposerEvents`, `ResponseEvents` and `CommentEvents`; `ui.Tour` returns `TourAction`. They are
  generated from the header's own `CUI_*` blocks, so `(events & PlayerEvents.Next) != 0` replaces
  a cast into `Interop` for a constant.
- **`ui.ChatMessage(text, opts)`** is the usual call: the markdown result is an optional `ref`, so
  a plain bubble no longer needs a throwaway local. `ui.InlineEdit(id, ref text)` gets the same
  treatment from a default buffer size.
- **`ui.Style` exposes CUI's design tokens.** `ThemeStyle` is a live view over the theme the frame
  draws with — `RadiusControl`/`RadiusPanel`/`RadiusSurface`, `ControlSm`/`ControlMd`/`ControlLg`,
  `TextXs`/`TextSm`/`TextBody`/`TextLg`, `GetHeading(1..4)`/`SetHeading`, `FontScale`, `Motion`, the
  palette through `style[Token.Accent]`, the four tone colours a widget reads live
  (`ToneInfo`, `ToneTeal`, `ToneViolet`, `ToneRose`) and the two shadow layers of each elevation.
  It holds CUI's own pointer, so nothing is copied, an assignment allocates nothing and applies on
  the next frame. A theme or accent switch keeps the radii, the motion multiplier and the font
  scale, which upstream does on purpose. Padding and gap are not tokens — a container carries its
  own in its `Box` — and the guide shows the preset struct that gives an app one rhythm.
- **`Color` is comparable and printable.** `IEquatable<Color>`, `==`, `!=` and a `ToString` that
  reads `#rrggbb`, `#rrggbbaa` or `Token.Accent`. A token compares as the token, not as the channels
  it resolves to, which is what a widget draws with.

### Changed

- **A size profile in the package.** `build/CuiSharp.Size.targets` compiles the headless channel and
  the framework diagnostics a shipped app never reads out of a **self-contained AOT publish**: the
  smallest app goes from 1 498 112 B to 1 165 312 B (−22.2%) and the gallery from 3 167 232 B to
  2 862 592 B (−9.6%), counting the fix in the third bullet. Migration: nothing to do, unless a
  published exe is driven by a tool — publish it with `-p:CuiSharpHeadless=true`, which is what
  `dotnet cui` now passes to the builds it runs itself. `dotnet run` is untouched: a build that is
  not a self-contained publish keeps the channel and the framework's exception messages.
- **`CuiSharpSizeProfile=false`** turns the whole set off; **`CuiSharpVerboseErrors=true`** keeps
  exception messages and stack traces while leaving the rest on. `DebuggerSupport` is off in a
  published binary too, and is not part of `CuiSharpVerboseErrors`: that property is about what an
  exception says, not about whether one can be stepped through on a machine that is not the user's.
  All three are properties, not API, and the measured values are in [docs/SIZE.md](docs/SIZE.md).
- **A container no longer links every other container.** `LayoutScope.Dispose` used to call one
  `switch` naming all 39 `*_end` functions, so an app that opened a box also linked the end
  functions (and translation units) of pickers, docking and the rest: −24 064 B for the smallest
  app. The end function is now taken in the wrapper that opened the container,
  so a widget is reachable exactly when it is used. No API change; `LayoutScope` and `OpenScope`
  are still what a `using` closes.
- **`dotnet new cui-mini`** ships a single binary by default. The generated file sets
  `PublishAot`, `OptimizationPreference=Size`, `InvariantGlobalization=true`, `StripSymbols` and
  `DebugType=none` with `#:property` — the switches a file-based app has no project file to inherit
  them from — so `dotnet publish demo1.cs -c Release -o .` leaves **one** file of about 1.1 MB
  beside the source instead of an exe and a 6 MB `.pdb`. Its header says how to run it in a second
  (a JIT build of one file) and how to publish it.
- **`dotnet cui` says what to do** when a published app never prints its banner, instead of only
  reporting that it waited.
- **A size budget per app shape.** `tools/size-report.ps1` measures five shapes (a table, a chart,
  form controls, Markdown and code) and `tools/budget-check.ps1 -SizeCsv` compares them with the
  ceilings in [docs/SIZE.md](docs/SIZE.md), so the next CUI release that grows the floor fails a
  check instead of arriving in a release note. Every pull request measures the floor and the
  gallery; the on-demand `budget` job measures every shape with `-RequireAll`.

### Fixed

- **A frame does not collect completions from work started on another thread.** `Mutation` handed
  its completion to one process-wide queue that every surface drained, so the next frame to draw
  could run another thread's `apply` callback against state it did not own, and pay for the allocation
  itself. Completions now queue into the `FrameDispatcher` of the thread that called `Run`; only
  frames on that thread drain them. No API change.
- **`ui.Tour` no longer needs its result assigned to the step.** `cui_tour_ex` returns the action
  the user took and moves the step through its `ref`, so `step = ui.Tour(…)` overwrote the step
  with `CUI_TOUR_NONE` every frame: the tours on the gallery's Tools and Audio pages could not
  advance, and Done left them on screen. `TourAction` makes the mistake a compile error.
- **`pulse`/`frame` refuse a frame count that cannot mean anything** — `pulse 0` answered with
  `-3 heap …` for a negative count, and a count too large for an `int` took the whole session down
  with an unhandled `OverflowException` instead of answering `CUI-ERR`.
- **`dwell` refuses zero, a negative number, `Infinity` and `NaN`** instead of spinning forever or
  reporting a row of zeros, and it works as a one-shot `dotnet cui dwell 3` as well as in a
  `--stdin` session.
- **`ui.ImageCompare` and `ui.Waveform` are generated wrappers again**, not hand-written ones:
  the generator treated a non-const `float *pos` as an array because its name ends in an `s`.

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
