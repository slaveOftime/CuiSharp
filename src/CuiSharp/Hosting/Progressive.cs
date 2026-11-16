namespace CuiSharp.Hosting;

/// <summary>
/// A background operation that reports more than once: a scan filling in its counts, a download
/// moving bytes, a spectrum table arriving row by row, a live feed.
/// <para>
/// It is the sibling of <see cref="Mutation{T}"/> for work with no single result. The work runs on
/// the pool and calls the <c>publish</c> callback whenever it has a new value; the frame reads
/// <see cref="Latest"/>. Nothing crosses threads on the frame path except a reference load, so a
/// frame never waits, allocates or locks, and a report that arrives between two frames is
/// deliberately dropped: the frame wants the newest state, not a queue of every step.
/// </para>
/// <para>
/// While a run is active the host keeps asking for frames, because a pool thread cannot wake a
/// sleeping window. <see cref="FrameRate"/> is how often that happens: the default draws as fast as
/// the window does, which is what a visualizer or an animation wants; 10 or 5 is enough for a
/// progress bar and leaves the CPU for the work.
/// </para>
/// </summary>
/// <example><code>
/// private readonly Progressive&lt;ScanResult&gt; _scan = new(scanner.ScanAsync) { FrameRate = 5 };
///
/// if (ui.Button("Scan", new() { Loading = _scan.IsRunning })) _scan.Run();
/// if (_scan.Latest is { } result) DrawScan(result);
/// if (_scan.Error is { } error) ui.Badge(error.Message, new() { Tone = Tone.Danger });
/// </code></example>
public sealed class Progressive<T> : IDisposable where T : class
{
    private readonly Lock _gate = new();
    private Func<CancellationToken, Action<T>, Task>? _work;
    private CancellationTokenSource? _run;
    private T? _latest;
    private Exception? _error;
    private readonly float _frameRate;

    // The run this operation is currently owned by. Bumped by every start or cancel under _gate
    // and checked by Publish under the same lock, so a superseded or cancelled producer can never
    // overwrite Latest or Error after the owner has moved on.
    private long _generation;
    private long _counted;        // the PendingWork id being counted, 0 when idle
    private volatile bool _disposed;

    /// <summary>
    /// Creates an operation. Constructing one installs the host's frame boundary, so a plain
    /// <see cref="CuiApp.Run(CuiAppOptions, DrawFn)"/> app that uses a progressive run wakes and
    /// applies results like a hosted one, without charging an app that uses neither.
    /// </summary>
    public Progressive() => FrameBoundary.Install();

    /// <summary>Creates an operation around the work it runs.</summary>
    public Progressive(Func<CancellationToken, Action<T>, Task> work) : this()
    {
        ArgumentNullException.ThrowIfNull(work);
        _work = work;
    }

    /// <summary>
    /// How many frames per second the host draws while a run is active. 0 (the default) draws at
    /// the window's own pace; a positive value throttles the wake-up, which is what a scan or a
    /// progress bar wants. Read when a run starts.
    /// </summary>
    public float FrameRate
    {
        get => _frameRate;
        init
        {
            if (!float.IsFinite(value) || value < 0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    "FrameRate must be zero or a finite positive number.");
            }

            _frameRate = value;
        }
    }

    /// <summary>
    /// The value the current or last run published most recently, or null before the first
    /// report. A reference load, so reading it every frame allocates nothing.
    /// </summary>
    public T? Latest => Volatile.Read(ref _latest);

    /// <summary>True while the operation is in flight.</summary>
    public bool IsRunning => Volatile.Read(ref _counted) != 0;

    /// <summary>
    /// The failure that ended the last run, or null. A cancellation caused by
    /// <see cref="Restart(Func{CancellationToken, Action{T}, Task}, T?)"/>, <see cref="Cancel"/>
    /// or <see cref="Dispose"/> is not a failure. Cached when the run completes, so reading it
    /// every frame allocates nothing.
    /// </summary>
    public Exception? Error => Volatile.Read(ref _error);

    /// <summary>
    /// Starts the operation if nothing is running. Safe to call from an event: a run that is
    /// already in flight is left alone, so a second click cannot restart it by accident.
    /// </summary>
    public void Run()
    {
        if (_disposed || IsRunning) return;
        if (_work is null)
            throw new InvalidOperationException("This Progressive has no work yet; pass work to the constructor or to Run(work).");
        Start(_work, initial: null);
    }

    /// <summary>
    /// Starts <paramref name="work"/> if nothing is running. Pass <paramref name="initial"/> when
    /// the first frame should already see an empty snapshot before the pool thread reports.
    /// </summary>
    public void Run(Func<CancellationToken, Action<T>, Task> work, T? initial = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (_disposed || IsRunning) return;
        Start(work, initial);
    }

    /// <summary>
    /// Starts the last work again, cancelling the run it replaces. Use it when the input changed —
    /// another track, another folder — so the old producer cannot publish into the new run.
    /// </summary>
    public void Restart()
    {
        if (_disposed) return;
        if (_work is null)
            throw new InvalidOperationException("This Progressive has no work yet; pass work to the constructor or to Restart(work).");
        Start(_work, initial: null);
    }

    /// <summary>
    /// Replaces the work and starts it, cancelling whatever was running. Pass
    /// <paramref name="initial"/> when the frame that starts the run should already see a seed
    /// snapshot instead of null while the pool thread starts up.
    /// </summary>
    public void Restart(Func<CancellationToken, Action<T>, Task> work, T? initial = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (_disposed) return;
        Start(work, initial);
    }

    /// <summary>
    /// Cancels a running operation and clears <see cref="Latest"/> and <see cref="Error"/>, while
    /// keeping this instance ready for a later <see cref="Run()"/> or <see cref="Restart()"/>.
    /// </summary>
    public void Cancel()
    {
        CancellationTokenSource? previous;
        lock (_gate)
        {
            _generation++;
            previous = _run;
            _run = null;
            Volatile.Write(ref _error, null);
            Volatile.Write(ref _latest, null);
        }

        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }

        PendingWork.Settle(Interlocked.Exchange(ref _counted, 0), completed: false);
    }

    /// <summary>
    /// Cancels a running operation and drops its value. Optional: an operation that is never
    /// disposed is cancelled when the process exits, which is what an app usually wants. A
    /// completion that arrives late — the work finished just before the page was navigated away
    /// from — is dropped instead of publishing into a component that no longer exists.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _work = null;
        Cancel();
    }

    private void Start(Func<CancellationToken, Action<T>, Task> work, T? initial)
    {
        _work = work;

        var run = new CancellationTokenSource();
        CancellationToken token = run.Token;
        CancellationTokenSource? previous;
        long generation;

        // The frame that starts this run is also the frame that will read it, so this is the one
        // place a newer run takes ownership. Anything the old one publishes from here is stale.
        lock (_gate)
        {
            if (_disposed)
            {
                run.Dispose();
                return;
            }

            generation = ++_generation;
            previous = _run;
            _run = run;
            Volatile.Write(ref _error, null);
            Volatile.Write(ref _latest, initial);
        }

        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }

        // A superseded run stops owing frames the moment it is replaced, even if its work ignores
        // cancellation; its late completion then finds no id of its own and changes nothing.
        long id = PendingWork.Add(_frameRate > 0f ? 1f / _frameRate : 0f);
        long oldCounted = Interlocked.Exchange(ref _counted, id);
        PendingWork.Settle(oldCounted, completed: false);

        var publish = new Action<T>(value => Publish(value, generation));

        Task task;
        try
        {
            // On the pool, because this is called from Draw: an `async` method runs its
            // synchronous prefix on whoever calls it, so invoking it here would make the frame
            // wait for work that was supposed to be off-frame. See skills/cui-dotnet/hosting.md.
            // The token is captured before Task.Run so a Restart that disposes the source cannot
            // make the work's first line throw while reading `run.Token`.
            task = Task.Run(() => work(token, publish), token);
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                if (!_disposed && _generation == generation)
                {
                    _run = null;
                    Volatile.Write(ref _error, error.GetBaseException());
                }
            }

            run.Dispose();
            PendingWork.Settle(id, completed: false);
            Interlocked.CompareExchange(ref _counted, 0, id);
            return;
        }

        task.ContinueWith(
            static (completed, state) =>
            {
                (Progressive<T> owner, long run, long counted, CancellationTokenSource source) =
                    ((Progressive<T>, long, long, CancellationTokenSource))state!;
                owner.Complete(completed, run, counted, source);
            },
            (this, generation, id, run),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void Publish(T value, long generation)
    {
        ArgumentNullException.ThrowIfNull(value);

        // The old run may still be winding down after a Restart or Cancel, and a disposed
        // component must never be published into. Checking and writing under _gate guarantees
        // that a stale producer cannot land between a generation bump and the reset of _latest.
        lock (_gate)
        {
            if (_disposed || _generation != generation) return;
            Volatile.Write(ref _latest, value);
        }
    }

    private void Complete(Task task, long generation, long counted, CancellationTokenSource run)
    {
        bool current;
        lock (_gate)
        {
            current = !_disposed && _generation == generation;
            if (current)
            {
                _run = null;

                // A cancellation the operation asked for is not a failure: Restart, Cancel and
                // Dispose are how a producer is stopped, and a page should not draw an error for leaving.
                if (!task.IsCompletedSuccessfully && !task.IsCanceled && !run.IsCancellationRequested)
                {
                    Volatile.Write(
                        ref _error,
                        task.Exception?.GetBaseException() ?? new TaskCanceledException(task));
                }
            }
        }

        run.Dispose();

        // Settle PendingWork before clearing _counted: a reader that observes IsRunning == false
        // also observes the settled count, the owed completion frame and the final Error/Latest.
        PendingWork.Settle(counted, completed: current);
        Interlocked.CompareExchange(ref _counted, 0, counted);
    }
}
