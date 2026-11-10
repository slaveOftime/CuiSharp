# Changelog

Every user-visible change, newest first. Pre-1.0 the minor number carries new surface and the patch
carries fixes. The section for a tag is what the GitHub release publishes, so the two cannot say
different things.

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
