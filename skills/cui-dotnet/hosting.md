# Hosting

`CuiSharp.Hosting` is a namespace inside the `CuiSharp` package, not a package of its own: a
builder, a service container, and one call that runs the root component. Trimming removes it from an
app that never references it, which is why the gallery — which uses no hosting — publishes at the
same size with or without the dependency.

```csharp
var builder = CuiApplication.CreateBuilder(args);
builder.Services.AddSingleton<TodoService>();
builder.Services.AddSingleton<TodoPage>();
builder.Window.Title = "Todos";

using var app = builder.Build();
return app.Run<TodoPage>();
```

| Type | Purpose |
|---|---|
| `CuiApplication` | `CreateBuilder`, `Build`, `Run<TRoot>()` / `Run(component)` on the calling thread |
| `CuiApplicationBuilder` | `Services`, `AddPage<TPage>()`, `Window`, `Args` |
| `Component` | `Draw(Ui)`, `OnInitialized`, `ComponentToken`, an inline error boundary, `WantsFrames` |
| `CuiApp.Run([options,] component)` | runs a root `Component` in a window without a DI container |
| `ui.Component(child)` | draws a child component inside the current container |
| `ui.ActionButton(label, op, ...)` | draws a button bound to a `Mutation` or `Progressive` (loading, error badge, retry) |
| `Mutation` / `Mutation<T>` | one async operation: `Run` / `Restart` / `Cancel`, `Latest`, `IsRunning`, `Error` |
| `Progressive<T>` | work that reports as it runs: `Run` / `Restart` / `Cancel`, `Latest`, `IsRunning`, `Error`, `FrameRate` |

A component is the screen: fields for state, a `Draw(Ui)` method, and the mutations it owns. There
is no view model, no binding, no `INotifyPropertyChanged`, and no reflection in the frame loop.

## The frame contract

`CuiApp.Run` hands the loop to CUI, which drives every frame — including the frames the OS requests
while the user drags a window edge. `CuiApplication.Run` is `CuiApp.Run` with a root component, and
the manual `CuiApp.Create`/`Frame` loop uses the same boundary, so every host behaves the same. Each
frame:

1. work that finished on a pool thread is applied (`Completions.Drain`), on the UI thread;
2. the root component renders; a throw is contained to that component and drawn as an inline error;
3. if a mutation is still running, a progressive run is active, or a rendered component reported
   `WantsFrames`, the host keeps the loop awake — a sleeping window cannot be woken by a pool thread.
   A run that asked for a slower cadence is woken after that interval instead of every frame;
4. the owed wake-up is spent, so the loop can sleep again once nothing is outstanding.

In a `Run` app this happens around each draw callback. In a manual loop it happens around each
`Frame()` call: the frame the caller drew is ended at the start of the next call, and the next frame
begins before `Frame()` returns, so work that finished meanwhile is already applied when the caller
draws.

`WantsFrames` is sampled *after* a component draws, so the frame that makes a page live is the frame
that asks for the next one.

The host also measures the frame it just drew, so any page can show its own numbers without a clock:

```csharp
ui.Badge(FrameStats.IsLive ? $"{FrameStats.Fps:0} fps" : "idle", new() { Dot = FrameStats.IsLive });
ui.Secondary($"draw {FrameStats.DrawMs:0.00} ms · alloc {FrameStats.AllocatedBytes} B");
```

`DrawMs` and `AllocatedBytes` describe the component's own draw, so `0 B` is a statement about app
code. `FrameMs` is wall clock, which for a sleeping window is the sleep, so `Fps` averages runs of
frames and `IsLive` says whether the app is in one — a footer that showed 0.5 fps after an idle
minute would be telling the truth about nothing.

Nothing in a component may block: no `.Result`, no `.Wait()`, no `Thread.Sleep`. The analyzers treat
those as errors inside a frame (`CUI001`).

## Async work

A `Mutation` is created once and run from an event:

```csharp
public sealed class Editor(Notes notes) : Component
{
    private readonly Mutation<Note> _save = new(token => notes.SaveAsync(token), _ => Saved = true);

    protected override void Draw(Ui ui)
    {
        ui.ActionButton("Save", _save, new() { Icon = Icons.Save });
    }
}
```

`ui.ActionButton` draws the button, turns its spinner on while `_save.IsRunning`, and when
`_save.Error` is set draws a danger badge plus a retry button. When custom markup is needed, read
`IsRunning`, `Error` and `Latest` directly (`if (ui.Button("Save", new() { Loading = _save.IsRunning })) _save.Run();`).
Like `Progressive<T>`, a `Mutation` also supports `Run(work)`, `Restart()`, `Restart(work)` and
`Cancel()`, and `Mutation<T>.Latest` holds the last completed result so a component that does not
need an `apply` callback can read `_load.Latest` straight from `Draw`.

The work runs on the pool — `Run` hands it to `Task.Run`, so even the synchronous prefix of an
`async` method (a parse, a file read before the first `await`) is off the frame. It never touches
`Ui`, and it hands its result over at a frame boundary. There is no synchronization context: a
continuation does not run on the UI thread, it publishes a value the UI thread reads next frame. That
is the whole async story, and it is why nothing deadlocks.

The frame that applies it must run on the thread that called `Run`. Create a mutation on the UI
thread (a component's field, as above), and that is where its completion is queued and applied.

A mutation owns one run at a time and remembers which one. If a result arrives after the component
was disposed — the user navigated away while the request was in flight — it is dropped rather than
applied into a page that no longer exists, and a run that has been superseded cannot clear the state
of the newer one. `IsRunning` and `Error` are always current, so `Draw` never has to poll.

### Work that reports as it goes

A scan's file count, a download's bytes and a spectrum table filling in have no single result.
`Progressive<T>` is the same shape as `Mutation` for those: the work runs on the pool and calls a
`publish` callback whenever it has something new; `Draw` reads `Latest`.

```csharp
public sealed class DiskPage(DiskScanner scanner) : Component
{
    private readonly Progressive<ScanResult> _scan = new(scanner.ScanAsync) { FrameRate = 5 };

    protected override void Draw(Ui ui)
    {
        if (ui.Button("Scan", new() { Loading = _scan.IsRunning })) _scan.Run();
        if (_scan.IsRunning && ui.Button("Cancel")) _scan.Cancel();

        if (_scan.Latest is { } result) DrawScan(result);
        if (_scan.Error is { } error) ui.Badge(error.Message, new() { Tone = Tone.Danger });
    }
}
```

When the work depends on an argument that changes at run time — another folder, another track —
pass the work to `Run(work)` or `Restart(work, initial)` instead of the constructor.

Every report writes one reference, which the frame reads without a lock or an allocation. A report
that arrives between two frames is deliberately dropped: the frame wants the newest state, not a
queue of every step, and a fast producer cannot make the frame do unbounded work. There is no
per-report callback for the same reason; invoking one would put the producer's rate into frame cost.
For the opposite case — every line of a log matters — use a `ConcurrentQueue` that `Draw` drains;
that is a different problem.

`T` is a class because publishing a reference is atomic. Wrap a small struct in a record class if
that is what you have; boxing it per report would allocate on the producer and turn a cheap write
into garbage.

`Run` starts only when nothing is running, so a second click cannot restart a job by accident.
`Restart` supersedes the current run — use it when the input changed, another track or another
folder — and drops anything the old producer publishes afterwards; pass `initial` when the first
frame should already show a seed snapshot before the pool thread reports. `Cancel` stops the active
run and clears `Latest` and `Error` while keeping the instance reusable; `Dispose` cancels and drops
the value when the owner goes away, exactly like `Mutation`. A cancellation caused by `Restart`,
`Cancel` or `Dispose` is not an `Error`. Each run is a `Task.Run`, not a thread of its own, exactly
like `Mutation`; a producer that needs a thread or a `Parallel.For` starts it inside its own work.

`FrameRate` is how often the host wakes while the run is active: `0` (the default) draws at the
window's own pace, which is what a visualizer or an animation wants; `10` or `5` is enough for a
progress bar or a scan and leaves the CPU for the work. When the run completes, one more frame is
owed so the final value is drawn before the window sleeps.

Like `Mutation`, start it on the UI thread: the frame that starts the work is the frame that reads
it.

## Testing a hosted app

`TestApp` drives a component directly, without the host, which is what a UI test wants:

```csharp
using var app = TestApp.Start(new TodoPage(new FakeTodoService()), 800, 600);
app.FindButton("Add").Click();
Assert.Single(page.Todos);
```

A test that needs the real container builds the app and calls `Run` — but a headless process is
usually simpler: `dotnet cui tree` against the app project drives it with its own services.

## Related

- `docs/ARCHITECTURE.md` in the repository — decisions and the generator pipeline.
- [SKILL.md](SKILL.md) — how to write pages.
- [api/Options.md](api/Options.md) — every option struct.
