# CuiSharp.App

A native desktop app in plain C#, built on [CuiSharp](https://www.nuget.org/packages/CuiSharp):
no XAML, no WPF, no Avalonia, no web view. The published exe is one file and depends
only on OS libraries.

```
CuiSharp.App.slnx
CuiSharp.App/          the app: Program.cs, App.cs, Pages/, Services/
CuiSharp.App.Tests/    the tests to copy the pattern from
AGENTS.md              the rules, short, for whoever (or whatever) edits this next
README.md              this file
```

## Run

```sh
dotnet run --project CuiSharp.App                     # JIT, fast iteration
dotnet watch --project CuiSharp.App                   # hot reload: edit Pages/ and the window updates
dotnet test                                           # no window needed
dotnet publish CuiSharp.App -c Release -r win-x64     # one self-contained exe

CUI_TRACE=trace.txt dotnet run --project CuiSharp.App # frame, draw and allocation numbers
```

## The mental model

Immediate mode in one sentence: **rebuild the whole UI every frame from your data.**

There is no control tree, no `InitializeComponent`, no binding, no `Invalidate()`.
`Draw` runs every frame and calls component functions. To change something, change a
field; the next frame draws it.

```csharp
public sealed class TodoPage(TodoService todos) : Component
{
    private readonly Mutation _save = new(todos.SaveAsync);   // created once
    private string _draft = "";

    public override string Title => "Todos";

    protected override void Draw(Ui ui)
    {
        if (ui.Input("draft", ref _draft).Submitted) Add();
        if (ui.Button("Save", new() { Loading = _save.IsRunning })) _save.Run();
        if (_save.Error is { } error) ui.Badge(error.Message, new() { Tone = Tone.Danger });
    }
}
```

Composition is a generic host, the way an ASP.NET or Blazor app is wired:

```csharp
var builder = CuiApplication.CreateBuilder(args);
builder.Services.AddSingleton<TodoService>();
builder.Services.AddSingleton<TodoPage>();
builder.Window.Title = "Todos";
builder.Window.Width = 640;

using var app = builder.Build();
return app.Run<TodoPage>();
```

## How the code is split

| Layer | File | Knows CuiSharp? |
|---|---|---|
| Business rules | `Services/TodoService.cs` | No |
| Live data and ring buffers | `Services/PriceFeed.cs`, `Services/PriceHistory.cs`, `Services/MarketFeed.cs` | No |
| Screens: state and drawing | `Pages/TodoPage.cs`, `Pages/MarketsPage.cs` | Yes |
| The window: navigation, theme, frame numbers | `App.cs` | Yes |
| Composition root | `Program.cs` | Yes |

Only `Services/` is CuiSharp-free, and that is where the rules worth testing live.
Each test file shows one kind of test:

| Test | What it shows |
|---|---|
| `TodoServiceTests` | rules in the small: blank input, toggling, a list that changes while it is read |
| `PriceHistoryTests` | a ring buffer: wrap-around, ordering, the change calculation |
| `MarketFeedTests` | a simulator driven by a fake clock: tick rate, a stall, the per-frame cap |
| `PageRenderTests` | a real frame drawn headlessly: `surface.Faults`, what was painted, and **0 B per frame** |
| `PageInteractionTests` | typing, clicking a tab, a filter or the theme switch — and the state the click changed |

A page is testable because `CuiSurface` renders one real frame into a pixel buffer
with the same layout, shaping and rasterizer the window uses:

```csharp
using var surface = CuiSurface.Create(640, 480);
var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
using var page = new TodoPage(new TodoService());

surface.Draw(page.Render, pixels);

Assert.Contains(pixels, value => value != 0);
Assert.Empty(surface.Faults);       // a component that threw is reported here
```

The performance rule is a test too, so a regression is a red build instead of a
stutter:

```csharp
for (var i = 0; i < 3; i++) surface.Draw(draw, pixels);          // warm up
long before = GC.GetAllocatedBytesForCurrentThread();
for (var i = 0; i < 10; i++) surface.Draw(draw, pixels);
Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
```

## Async work

The frame loop is synchronous, so nothing in `Draw` can `await`. The rule is: **start
the work, keep drawing, and take the result on a later frame.**

```csharp
// a field, created once
private readonly Mutation<MarketSnapshot> _load = new(feed.LoadAsync, feed.Apply);

// in Draw: no polling, no awaiting — the state just updates
if (_load.IsRunning) ui.Button("Loading history", new() { Loading = true });
else if (feed.Live) ui.Badge("live", new() { Tone = Tone.Success });
```

- The work runs on the pool and never touches `Ui`.
- `IsRunning` and `Error` are current on read; a fire-and-forget mutation needs
  nothing else.
- The `apply` callback runs on the UI thread, before the next frame, so a result
  never has to be polled.
- The host keeps frames coming while work is pending, so the result appears at once
  instead of on the next mouse move.

### The one rule that makes a live page live

CUI sleeps between frames when nothing needs drawing — that is why an idle app costs
no CPU. A page whose content changes on its own therefore has to ask for frames:

```csharp
protected override bool WantsFrames => feed.Live;
```

Todos asks for nothing (the window sleeps); Markets asks every frame (the feed
advances at 50 Hz). A finished mutation is counted for **one** frame, not forever.

## The theme, and the app's own numbers

CUI owns the theme, so a switch is not state the app has to keep: read it for the
selected segment, assign it back when the segment changes, and CUI animates the whole
palette across the change. `Ctrl+J` is one line.

```csharp
private static readonly string[] Modes = ["Light", "Dark"];

int mode = ui.Theme == Theme.Dark ? 1 : 0;
if (ui.Segmented("theme", Modes, ref mode)) ui.Theme = mode == 1 ? Theme.Dark : Theme.Light;
```

The host measures every frame around `Draw`, so the footer can show what the last
frame cost without the app owning a clock. `alloc` is the one to watch: it reads
`0 B`, and any other number is something in `Draw` that allocates.

```csharp
ui.Badge(FrameStats.IsLive ? $"{FrameStats.Fps:0} fps" : "idle", new() { Dot = FrameStats.IsLive });
ui.Secondary($"draw {FrameStats.DrawMs:0.00} ms");
ui.Secondary($"frame {FrameStats.FrameMs:0.0} ms");
ui.Secondary($"alloc {FrameStats.AllocatedBytes} B");
```

`idle` is not a missing number: a window sleeps while nothing changes, so a frame rate
would describe nothing. `IsLive` is true exactly when something asked for the frame —
a page with `WantsFrames`, or work in flight.

## Performance, and the rules that produce it

`Pages/MarketsPage.cs` is the throughput half: eight symbols at 50 Hz, a table with a
sparkline per row, a decimated chart, and the frame's own numbers. Measured on a
normal desktop: **0 B per frame**, 0.03–0.10 ms of draw. The frame interval itself is
whatever the OS present path allows — a software-rasterized remote session bounds it
near 32 ms, and that is the rasterizer, not `Draw`.

1. **Bounded memory.** `PriceHistory` owns one preallocated ring per symbol; the feed
   overwrites the oldest sample and nothing grows.
2. **One copy per frame, into a reused buffer.** The chart wants contiguous samples,
   so the page copies the ring tail into a field it owns and hands that span over.
3. **Ticks follow the clock.** `MarketFeed` generates what the elapsed time owes, caps
   a frame at four ticks, and *counts* the rest as skipped instead of replaying it —
   replaying six minutes of ticks in one frame stalls the window longer than the sleep
   it recovers from.
4. **Long series are decimated for free.** `ui.LineChart` collapses anything beyond
   `ChartOpts.MaxPoints` into min/max pairs inside the frame arena, so spikes survive.

The allocation traps, all avoided here:

- `foreach` over an `IReadOnlyList<T>` through the interface allocates an enumerator.
  `TodoService.Items` returns a list and the page indexes it — free, and still correct
  if a row removes an item while the loop runs. A `foreach` on a **sealed** `List<T>`
  is also free.
- Formatting a string per frame allocates it. Interpolating into a widget parameter
  (`$"{count} open"`) formats straight into the frame arena.
- A callback that captures local state allocates per call. Prefer a data-driven widget
  (`ui.Sparkline`, `ui.LineChart`).
- Two rows with the same label share an id. `using (ui.Id(item.Id))` gives each its own.

## Resizing, and where it renders

`CuiApp.Run` hands the loop to CUI, whose frame callback the OS's modal resize loop
calls, so every resize frame re-runs your `Draw` and the layout follows the window
live. `CuiApp.Create` + `Frame` is the loop for tests and tools, not for a window a
user drags.

`CuiAppOptions.Renderer` decides once, at window creation: `Auto` (OpenGL if the
driver has it, otherwise the software rasterizer), `OpenGL`, or `Software`. The
fallback matters in VMs and over remote desktop. Layout, geometry and text shaping are
always CPU — the GPU only rasterizes the triangles, so a frame budget is a CPU problem
first.

## Driving it without a window

The app is inspectable and clickable as it runs, which is what makes it workable for an
agent as well as a person:

```sh
dotnet tool install -g CuiSharp.Tools
dotnet cui tree --project CuiSharp.App    # the controls of the last frame, nested
dotnet cui click Dark                     # a control, by the label the tree printed
dotnet cui png shot.png                   # the frame as a PNG
```

The framework serves that protocol when the app is started headless, so nothing in this
app knows about it.

## Next

- `AGENTS.md` in this folder: the rules, for a person or an agent editing this app.
- `skills/cui-dotnet/SKILL.md` in the CuiSharp repository: the component cheat sheet.
- `ui.Canvas(...)` plus the drawing primitives when a control you need does not exist.
