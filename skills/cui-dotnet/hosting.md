# Hosting

Start with `CuiApp.Run(draw)` or `CuiApp.Run(component)`. Use `CuiApplication` if you also want
Microsoft dependency injection. All three use the same frame and background-work rules.

```csharp
var builder = CuiApplication.CreateBuilder(args);
builder.Services.AddSingleton<TodoService>();
builder.Services.AddSingleton<TodoPage>();
builder.Window.Title = "Todos";

using var app = builder.Build();
return app.Run<TodoPage>();
```

A `Component` owns ordinary C# state and rebuilds its UI in synchronous `Draw(Ui)`. Compose it
with `ui.Component(child)`. `OnInitialized()` runs once, just before its first draw. Exceptions
in initialization and drawing appear inline. There is no data binding or synchronization context.

## The frame contract

1. At the start of a frame, the host applies completed callbacks queued for **that window or
   surface**, on its drawing thread.
2. Draw reads state and builds the UI. Never block or await in Draw. `Ui` and its scopes cannot
   leave the frame.
3. A window draws for input, native animation, or app requests. Use `WantsFrames` for a rendered
   component that continuously changes, and `ui.RequestRedraw()` for one more frame. Background
   operations request frames automatically; they don't need either of those calls.

`CuiApp.Run` also draws during a native window resize. In a manual `CuiApp.Create`/`Frame()` loop,
`Frame()` ends the previous draw and starts the next one before returning. A `CuiSurface` (including
`TestApp` and `HeadlessApp`) instead draws only when driven. `FrameStats` reports the cost of app
drawing, not native layout and paint; a surface exposes its sample as `LastFrame`.

## Async work

**Start on the frame thread. Work on the pool. Draw the result on the frame thread.**

| Need | Use |
|---|---|
| One save, load or computation | `Mutation` for `Task`; `Mutation<T>` for `Task<T>` |
| A scan, download, or other operation that reports as it runs | `Progressive<T>` — the newest snapshot wins |
| Every event matters or an external event source must wake an otherwise idle app | Neither is an event queue or a general-purpose UI dispatcher |

### One result

```csharp
using System.IO;
using CuiSharp;
using CuiSharp.Hosting;

var path = "notes.txt";
using var load = new Mutation<string>(ct => File.ReadAllTextAsync(path, ct));

return CuiApp.Run(ui =>
{
    if (ui.Button("Load", new() { Loading = load.IsRunning })) load.Run();
    if (load.Latest is { } text) ui.Label(text);
    if (load.Error is { } error) ui.Alert(error.Message, new() { Tone = Tone.Danger });
});
```

`Latest` is the last successful worker result (default until the first success or after `Cancel`).
A new run keeps the previous successful result until it succeeds. To distinguish a successful
null/default from no result, or to update UI state once, pass `apply` to `Mutation<T>` or
`onSuccess` to `Mutation`. The callback runs
before the next draw of the owner that takes the completion; `Latest` and `IsRunning` describe the
worker and may change before that callback runs. A throwing callback is contained and reported in
`Error` and, for a surface, in `Faults`. It does not roll back anything it already changed.
`ui.ActionButton` is optional; it calls the configured `Run()` and supplies loading and retry UI.

### Progress snapshots

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using CuiSharp;
using CuiSharp.Hosting;

using var page = new ImportPage();
return CuiApp.Run(page);

sealed class ImportPage : Component
{
    private readonly Progressive<Progress> _import = new(ImportAsync) { FrameRate = 5 };

    protected override void Draw(Ui ui)
    {
        if (ui.Button("Import", new() { Loading = _import.IsRunning })) _import.Run();
        if (_import.IsRunning && ui.Button("Cancel")) _import.Cancel();
        if (_import.Latest is { } progress) ui.Label($"Imported {progress.Done} / 10");
        if (_import.Error is { } error) ui.Alert(error.Message, new() { Tone = Tone.Danger });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _import.Dispose();
        base.Dispose(disposing);
    }

    private static async Task ImportAsync(CancellationToken ct, Action<Progress> publish)
    {
        for (int done = 1; done <= 10; done++)
        {
            await Task.Delay(200, ct); // stand-in for importing one item
            publish(new Progress(done));
        }
    }

    private sealed record Progress(int Done);
}
```

Publish immutable snapshots, or transfer ownership so the producer no longer mutates the data.
Finish reporting before the worker task ends. `Latest` is one reference read, not a queue; read it
once into a local if multiple controls need to agree. A new run starts with `null` unless you pass
`initial`. `FrameRate` is the cadence this operation requests while active, not an FPS cap. `0`
asks for every frame, `5` asks for a wake after about 200 ms; input, animation or other work can
draw sooner. A worker finishing during a Windows timer wait can post a wake immediately; on other
backends the already scheduled wake is the fallback.

### Ownership and cancellation

- **Construct** anywhere. The first `Run` or `Restart` binds the operation to the frame host
  drawing right now. Start from a click or `OnInitialized()` there, not a constructor, a worker,
  or an unconditional call on every draw. Capture any changing input before handing work to the
  pool. Even the synchronous prefix of the work runs off the frame thread.
- **`Run()`** starts only when idle. You can configure the delegate at construction or pass it
  to `Run(work)`; an accepted delegate is remembered for retry. **`Restart()`** or
  **`Restart(work)`** can replace work still in flight. Either way, starting a new run drops
  any callback from the previous run that has not been applied yet.
- **`Cancel()`** clears the result and error and drops pending apply; the same instance is reusable.
  **`Dispose()`** does the same and prevents later starts. Call them on the owning thread, including
  between frames and at shutdown. Cancellation is cooperative: these calls don't wait, and an
  abandoned worker may still run but cannot affect the operation's state. Cancellation is not a
  failure. Keep one operation per UI owner and dispose it when that owner goes away; disposing a
  `Component` does not automatically dispose its fields.
- **`Task` continuations are not UI events.** They have no frame-thread affinity. Do not touch
  `Ui` or mutate UI-owned state from a worker, with or without `ConfigureAwait`. If the frame
  completes a `TaskCompletionSource` that a worker awaits, use
  `TaskCreationOptions.RunContinuationsAsynchronously` so the worker cannot resume inline in Draw.
  Read `Latest` in Draw or use a mutation's `apply`.

## Testing a hosted app

`TestApp` drives a component directly, without DI or a window. Use a controllable worker (for
example, one that awaits a test-owned `TaskCompletionSource`) and assert what the frame actually
shows. Complete the worker, then use `WaitFor` to wait for a label/query, or `WaitReady` for a frame
that calls `ui.MarkReady()`. `Frame()` forces a draw. `Settle()` is for animation, **not** for waiting
for worker results. These tests don't prove a sleeping OS window wakes; check that separately in a
real window. `dotnet cui` can drive the real startup and services when needed.

## Related

- [SKILL.md](SKILL.md) — how to write pages.
- [api/Options.md](api/Options.md) — every option struct.
