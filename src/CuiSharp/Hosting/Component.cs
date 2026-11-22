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
    private CancellationTokenSource? _token;
    private bool _initialized;
    private bool _disposed;

    /// <summary>
    /// True when the component's content changes on its own: a live feed, a clock,
    /// an animation. The host keeps requesting frames while a rendered component
    /// says so, because CUI sleeps until input otherwise.
    /// </summary>
    protected virtual bool WantsFrames => false;

    /// <summary>Current background runs across open frame hosts. For diagnostics only.</summary>
    public static int PendingOperations => FrameDispatcher.PendingOperations;

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
        try
        {
            Initialize();
            Draw(ui);
            // A draw can make the page live, so sample this after it runs.
            if (WantsFrames) ui.FrameState.WantsFrames = true;
        }
        catch (Exception error)
        {
            ErrorBoundary(ui, error);
        }
    }

    /// <summary>Runs <see cref="OnInitialized"/> once, on the first frame that draws it.</summary>
    internal void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        OnInitialized();
    }

    private static void ErrorBoundary(Ui ui, Exception error)
    {
        // Report first, draw second: a test that renders into a CuiSurface gets the
        // exception even if the alert itself cannot be drawn.
        ui.FrameState.Faults?.Add(error);

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

/// <summary>Draws a child component or a background-operation button inside the current container.</summary>
public static class ComponentExtensions
{
    extension(Ui ui)
    {
        /// <summary>Draws <paramref name="component"/>, with its own error boundary.</summary>
        public void Component(Component component) => component.Render(ui);

        /// <summary>
        /// Draws a button bound to <paramref name="mutation"/>: shows a spinner while running,
        /// surfaces <see cref="Mutation.Error"/> as a danger badge with a retry button when it
        /// fails, and calls <see cref="Mutation.Run()"/> when clicked.
        /// </summary>
        public bool ActionButton(
            UiText label,
            Mutation mutation,
            ButtonOpts opts = default,
            UiText loadingLabel = default,
            UiText retryLabel = default)
        {
            ArgumentNullException.ThrowIfNull(mutation);
            if (!DrawActionButton(ui, label, mutation.IsRunning, mutation.Error, opts, loadingLabel, retryLabel))
                return false;

            mutation.Run();
            return true;
        }

        /// <summary>
        /// Draws a button bound to <paramref name="mutation"/>: shows a spinner while running,
        /// surfaces <see cref="Mutation{T}.Error"/> as a danger badge with a retry button when it
        /// fails, and calls <see cref="Mutation{T}.Run()"/> when clicked.
        /// </summary>
        public bool ActionButton<T>(
            UiText label,
            Mutation<T> mutation,
            ButtonOpts opts = default,
            UiText loadingLabel = default,
            UiText retryLabel = default)
        {
            ArgumentNullException.ThrowIfNull(mutation);
            if (!DrawActionButton(ui, label, mutation.IsRunning, mutation.Error, opts, loadingLabel, retryLabel))
                return false;

            mutation.Run();
            return true;
        }

        /// <summary>
        /// Draws a button bound to <paramref name="progressive"/>: shows a spinner while running,
        /// surfaces <see cref="Progressive{T}.Error"/> as a danger badge with a retry button when
        /// it fails, and calls <see cref="Progressive{T}.Run()"/> when clicked.
        /// </summary>
        public bool ActionButton<T>(
            UiText label,
            Progressive<T> progressive,
            ButtonOpts opts = default,
            UiText loadingLabel = default,
            UiText retryLabel = default)
            where T : class
        {
            ArgumentNullException.ThrowIfNull(progressive);
            if (!DrawActionButton(ui, label, progressive.IsRunning, progressive.Error, opts, loadingLabel, retryLabel))
                return false;

            progressive.Run();
            return true;
        }
    }

    private static bool DrawActionButton(
        Ui ui,
        UiText label,
        bool isRunning,
        Exception? error,
        ButtonOpts opts,
        UiText loadingLabel,
        UiText retryLabel)
    {
        if (error is not null)
        {
            ui.Badge(error.Message, new() { Tone = Tone.Danger });
            return ui.Button(retryLabel.IsEmpty ? label : retryLabel, opts with { Variant = ButtonVariant.Danger });
        }

        if (isRunning)
        {
            ui.Button(loadingLabel.IsEmpty ? label : loadingLabel, opts with { Loading = true });
            return false;
        }

        return ui.Button(label, opts);
    }
}
