# CuiSharp.App

Native desktop app in plain C# built on [CuiSharp](https://www.nuget.org/packages/CuiSharp) — no XAML, no WPF/Avalonia, no web view. Publishes to a single NativeAOT executable.

## Quick start

```sh
dotnet run --project CuiSharp.App # run (JIT)
dotnet watch --project CuiSharp.App # hot reload on save
dotnet test # service + headless UI tests
dotnet publish CuiSharp.App -c Release -r win-x64 # single-file NativeAOT exe (drops the driving channels: see below)
dotnet run --project CuiSharp.App -- --check # renderer, backend, native ABI, then exit
CUI_TRACE=trace.txt dotnet run --project CuiSharp.App # frame/draw/alloc stats every 60 frames
```

## Drive the app from a terminal

Nothing is added to the app for this (install once: `dotnet tool install -g CuiSharp.Tools`). A
command builds the app in **Release** and runs it headless, so `--no-build` is only useful after a
Release build of your own — `dotnet build` alone produces Debug.

```sh
dotnet cui tree -p CuiSharp.App # the frame's controls, nested
dotnet cui click 'Tab "Markets"' -p CuiSharp.App # click one, print the frame that reacted
dotnet cui png shot.png -p CuiSharp.App # the frame as an image
dotnet cui layout -p CuiSharp.App # rectangles, and anything that overflowed
dotnet cui session -p CuiSharp.App < steps.txt # many commands, one process
dotnet cui docs # the framework guide, offline
```

- **Each headless invocation starts a fresh app.** A click on a tab does not survive into the
  next command: a flow that needs the previous step is a `session`, whose lines share one app.
  `png` inside a session photographs the page the earlier lines reached.
- Session lines are protocol commands: `click Tab "Markets"`, then `png shot.png`. Quotes are literal; a
  line that fails stops the rest of the session, so read the top of its output first.
- Disambiguate by kind (`Tab "Markets"`) or by container path when kinds also match:
  `dotnet cui click 'theme > Segment "Dark"' -p CuiSharp.App`. Only the final path step names a kind.
- `-a` drives a window that is already open, which `LiveAttach` in `Program.cs` enables in Debug:
  start the app (or pass `--live` in Release), then `dotnet cui click 'theme > Dark' -a`.
  Live commands preserve its state and leave
  it open; `dotnet cui quit -a` closes it. A headless run has no window to attach to.
- A published exe keeps neither channel unless it was published for it — a Release publish strips
  both to keep the binary small: `-p:CuiSharpHeadless=true` for `--app`, `-p:CuiSharpLive=true` for
  `-a`.
- `dotnet cui api <Name>` answers for the `CuiSharp` catalog only: `HeadlessApp` is in it,
  `TestApp` (the alias in the separate `CuiSharp.Testing` package) is not.

## Project structure

| Path | Purpose |
|---|---|
| `CuiSharp.App/Program.cs` | Composition root: DI container, window options, `--check`, live attach |
| `CuiSharp.App/App.cs` | Shell: named `header`/`page`/`footer` regions, page tabs (`Ctrl+Tab`), theme (`Ctrl+J`) |
| `CuiSharp.App/Pages/TodoPage.cs` | Form & list screen: `ref` input, keyed row loop, async `Mutation` save |
| `CuiSharp.App/Pages/MarketsPage.cs` | Live 50 Hz dashboard: `WantsFrames`, sparklines, decimated `LineChart` |
| `CuiSharp.App/Services/*` | Pure C# domain & feed logic (`TodoService`, `PriceHistory`, `MarketFeed`) |
| `CuiSharp.App/CuiSharp.App.ico` | App and window icon — replace this file and rebuild to rebrand |
| `CuiSharp.App.Tests/TestHarness.cs` | `FakeClock`, snapshot fixtures, and the 0 B/frame allocation gate |
| `CuiSharp.App.Tests/Pages/*` | Headless render, interaction, and allocation tests |
| `CuiSharp.App.Tests/Services/*` | Unit tests for domain rules, ring buffer wrap-around, and feed cadence |

Tests drive the app through `HeadlessApp` (in `CuiSharp.Hosting`, already referenced), and
`TestHarness.AssertZeroAllocations` is the 0 B/frame gate. See [AGENTS.md](AGENTS.md) for the
immediate-mode rules and how a screen is extended.
