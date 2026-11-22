namespace CuiSharp.Hosting;

/// <summary>
/// One background operation that reports snapshots as it runs, such as a scan or a download.
/// The work starts on the pool and calls its publish delegate; the owning frame reads
/// <see cref="Latest"/>. Only the newest snapshot is kept, not a queue of reports.
/// </summary>
/// <remarks>
/// Start from a click or <c>OnInitialized</c>. The first start binds the operation to its window
/// or surface; start and restart during its drawing, and cancel or dispose on its thread. Publish
/// immutable snapshots (or transfer ownership) and finish all publishing before returning from
/// the work delegate. Dispose the operation when its app or page owner goes away.
/// </remarks>
public sealed class Progressive<T> : IDisposable where T : class
{
    private readonly OperationState _state = new();
    private Func<CancellationToken, Action<T>, Task>? _work;
    private T? _latest;
    private readonly float _frameRate;

    /// <summary>Creates an operation whose work is supplied at its first start.</summary>
    public Progressive() { }

    /// <summary>Creates an operation around the work it runs.</summary>
    public Progressive(Func<CancellationToken, Action<T>, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        _work = work;
    }

    /// <summary>
    /// How often this run asks for a window frame while active. Zero uses the window's own pace.
    /// A positive value asks for a timer (up to one day), but other activity can draw sooner.
    /// </summary>
    public float FrameRate
    {
        get => _frameRate;
        init
        {
            if (!float.IsFinite(value) || value < 0f)
                throw new ArgumentOutOfRangeException(nameof(value), value, "FrameRate must be zero or a finite positive number.");
            _frameRate = value;
        }
    }

    /// <summary>The most recent snapshot, read without locking. Reset at each start or cancellation.</summary>
    public T? Latest => Volatile.Read(ref _latest);

    /// <summary>True while the current worker is in flight.</summary>
    public bool IsRunning => _state.IsRunning;

    /// <summary>The current run's failure, or null. Cancellation is not a failure.</summary>
    public Exception? Error => _state.Error;

    /// <summary>Starts the configured work from the owning frame. Does nothing while running or after disposal.</summary>
    public void Run()
    {
        if (_state.CanStart(replace: false)) Start(Work, initial: null);
    }

    /// <summary>Starts and remembers the supplied work if idle. Optionally seeds <see cref="Latest"/>.</summary>
    public void Run(Func<CancellationToken, Action<T>, Task> work, T? initial = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (_state.CanStart(replace: false)) Start(work, initial);
    }

    /// <summary>Supersedes the current run and starts the configured work again.</summary>
    public void Restart()
    {
        if (_state.CanStart(replace: true)) Start(Work, initial: null);
    }

    /// <summary>Supersedes the current run and starts and remembers new work, with an optional seed.</summary>
    public void Restart(Func<CancellationToken, Action<T>, Task> work, T? initial = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (_state.CanStart(replace: true)) Start(work, initial);
    }

    /// <summary>Requests cancellation and clears the snapshot and error. Does not wait.</summary>
    public void Cancel() => _state.Stop(Clear);

    /// <summary>Cancels the operation and prevents later starts. Dispose on its owning thread.</summary>
    public void Dispose()
    {
        _state.Stop(Clear, dispose: true);
        _work = null;
    }

    private Func<CancellationToken, Action<T>, Task> Work => _work ?? throw new InvalidOperationException(
        "This Progressive has no work yet; pass work to the constructor, Run or Restart.");

    private void Start(Func<CancellationToken, Action<T>, Task> work, T? initial)
    {
        // Keep extreme intervals in range for platform timers that take milliseconds.
        float seconds = _frameRate > 0f ? Math.Min(1f / _frameRate, 24 * 60 * 60) : 0f;
        _state.Start(run =>
        {
            Action<T> store = Store;
            return work(run.Token, value =>
            {
                ArgumentNullException.ThrowIfNull(value);
                _state.Publish(run, value, store);
            });
        }, seconds, () => Store(initial));
        _work = work;
    }

    private void Store(T? value) => Volatile.Write(ref _latest, value);

    private void Clear() => Store(null);
}
