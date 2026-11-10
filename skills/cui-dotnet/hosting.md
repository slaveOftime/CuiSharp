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
| `CuiApplicationBuilder` | `Services`, `Configuration`, `Logging`, `Window`, `Args` |
| `Component` | `Draw(Ui)`, `OnInitialized`, `ComponentToken`, an inline error boundary, `WantsFrames` |
| `ui.Component(child)` | draws a child component inside the current container |
| `Mutation` / `Mutation<T>` | one async operation: `Run`, `IsRunning`, `Error`, `TryPublish` |

A component is the screen: fields for state, a `Draw(Ui)` method, and the mutations it owns. There
is no view model, no binding, no `INotifyPropertyChanged`, and no reflection in the frame loop.

## The frame contract

`CuiApp.Run` hands the loop to CUI, which drives every frame — including the frames the OS requests
while the user drags a window edge. Each frame:

1. work that finished on a pool thread is applied (`Completions.Drain`), on the UI thread;
2. the root component renders; a throw is contained to that component and drawn as an inline error;
3. if a mutation is still running, or a rendered component reported `WantsFrames`, `ui.RequestRedraw()`
   keeps the loop awake — a sleeping window cannot be woken by a pool thread;
4. the owed wake-up is spent, so the loop can sleep again once nothing is outstanding.

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
        if (ui.Button("Save", new() { Loading = _save.IsRunning })) _save.Run();
        if (_save.Error is { } error) ui.Badge(error.Message, new() { Tone = Tone.Danger });
    }
}
```

The work runs on the pool — `Run` hands it to `Task.Run`, so even the synchronous prefix of an
`async` method (a parse, a file read before the first `await`) is off the frame. It never touches
`Ui`, and it hands its result over at a frame boundary. There is no synchronization context: a
continuation does not run on the UI thread, it publishes a value the UI thread reads next frame. That
is the whole async story, and it is why nothing deadlocks.

A mutation owns one run at a time and remembers which one. If a result arrives after the component
was disposed — the user navigated away while the request was in flight — it is dropped rather than
applied into a page that no longer exists, and a run that has been superseded cannot clear the state
of the newer one. `IsRunning` and `Error` are always current, so `Draw` never has to poll.

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
