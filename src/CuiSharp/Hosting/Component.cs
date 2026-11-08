using System.Runtime.CompilerServices;

namespace CuiSharp.Hosting;

/// <summary>
/// A screen or a piece of one. A component owns its state as fields and rebuilds
/// its UI every frame; there is no view model, no binding and nothing to notify.
/// <para>
/// Create one per screen and resolve it from the container, or inject it into a
/// parent and draw it with <c>ui.Component(child)</c>.
/// </para>
/// </summary>
/// <example><code>
/// internal sealed class Counter(TodoService todos) : Component
/// {
///     private int _clicks;
///     protected override void Draw(Ui ui) => ui.Text($"Clicks {_clicks}");
/// }
/// </code></example>
public abstract class Component : IDisposable
{
    [ThreadStatic] private static bool _wantsFrames;

    private CancellationTokenSource? _token;
    private bool _initialized;
    private bool _disposed;

    /// <summary>
    /// True when the component's content changes on its own: a live feed, a clock,
    /// an animation. The host keeps requesting frames while a rendered component
    /// says so, because CUI sleeps until input otherwise.
    /// </summary>
    protected virtual bool WantsFrames => false;

    internal bool WantsFramesNow => WantsFrames;

    /// <summary>Operations in flight across every component. For diagnostics.</summary>
    public static int PendingOperations => PendingWork.Count;

    /// <summary>
    /// Label for this component, used by a shell's page switch. Defaults to the type
    /// name; override it to change the wording without renaming the class.
    /// </summary>
    public virtual string Title => GetType().Name;

    /// <summary>Cancelled when the component is disposed; pass it to background work.</summary>
    protected CancellationToken ComponentToken => (_token ??= new()).Token;

    /// <summary>Called once before the first frame. Keep it synchronous and cheap.</summary>
    protected virtual void OnInitialized() { }

    /// <summary>Draws the component into the current container. Called once per frame.</summary>
    protected abstract void Draw(Ui ui);

    /// <summary>
    /// Draws this component, containing a failure to this component instead of the
    /// whole window. A throw inside <see cref="Draw"/> is reported inline and the
    /// frame keeps going.
    /// </summary>
    public void Render(Ui ui)
    {
        // A child component is initialized the first time a parent draws it, so a
        // component never has to be created "ready".
        Initialize();

        try
        {
            Draw(ui);
        }
        catch (Exception error)
        {
            ErrorBoundary(ui, error);
        }

        // Sampled after drawing on purpose: a page becomes live *during* a frame
        // (a load finishing, a feed starting), and the frame that changes it is
        // exactly the frame that must ask for the next one.
        if (WantsFrames) _wantsFrames = true;
    }

    /// <summary>Runs <see cref="OnInitialized"/> once, on the first frame that draws it.</summary>
    internal void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        OnInitialized();
    }

    /// <summary>Clears the per-frame "wants frames" flag. Called by the host.</summary>
    internal static void BeginFrame() => _wantsFrames = false;

    /// <summary>Whether any component rendered this frame asked for more frames.</summary>
    internal static bool AnyWantFrames => _wantsFrames;

    private static void ErrorBoundary(Ui ui, Exception error)
    {
        // Report first, draw second: a test that renders into a CuiSurface gets the
        // exception even if the alert itself cannot be drawn.
        CuiSurface.ReportFault(error);

        // Deliberately dependency-free: a failing component must not be able to
        // fail while reporting that it failed.
        ui.Alert("This part of the window failed", new AlertOpts
        {
            Description = error.Message,
            Tone = Tone.Danger,
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Idempotent on purpose: the container disposes the root component, and an
        // app that resolves components itself may dispose them too.
        if (_disposed) return;
        _disposed = true;

        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the component's state and cancels its background work.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing) return;
        if (_token is null) return;
        _token.Cancel();
        _token.Dispose();
        _token = null;
    }
}

/// <summary>Draws one component inside the current container.</summary>
public static class ComponentExtensions
{
    extension(Ui ui)
    {
        /// <summary>Draws <paramref name="component"/>, with its own error boundary.</summary>
        public void Component(Component component) => component.Render(ui);
    }
}

/// <summary>
/// Tracks background work so the host can keep the loop awake.
/// <para>
/// Two things count: an operation still running, and an operation that finished
/// since the last frame. The second one matters because the result has to be drawn
/// once — but only once. Counting a *finished* operation forever would pin the
/// window at the frame rate, which is exactly what a component that is no longer
/// drawn would do.
/// </para>
/// </summary>
internal static class PendingWork
{
    private static int _running;
    private static int _finished;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Add() => Interlocked.Increment(ref _running);

    internal static void Remove()
    {
        Interlocked.Decrement(ref _running);
        Interlocked.Increment(ref _finished);
    }

    internal static bool Any => Volatile.Read(ref _running) > 0 || Volatile.Read(ref _finished) > 0;

    internal static int Count => Volatile.Read(ref _running);

    /// <summary>Clears the "a frame is owed" flags once that frame has been drawn.</summary>
    internal static void MarkDrawn() => Interlocked.Exchange(ref _finished, 0);
}
