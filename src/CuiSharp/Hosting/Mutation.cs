using System.Diagnostics.CodeAnalysis;

namespace CuiSharp.Hosting;

/// <summary>
/// One background operation owned by a component: start it, keep drawing, and see
/// the result on a later frame.
/// <para>
/// The frame loop is synchronous, so nothing inside <c>Draw</c> may await. A
/// mutation is the shape that makes that safe: it runs the work on the pool,
/// holds the outcome, and never touches a <see cref="Ui"/> from a pool thread.
/// While the operation runs — and for the one frame its result still needs to be
/// drawn — the host keeps requesting frames, so the state appears immediately
/// instead of on the next mouse move.
/// </para>
/// <para>
/// <see cref="Error"/>, <see cref="IsRunning"/> and <see cref="Latest"/> are always
/// current and cost nothing to read, so a mutation needs no polling in <c>Draw</c>.
/// Pass an <c>apply</c> callback when the result also has to mutate model state on
/// the UI thread before the next frame draws.
/// </para>
/// </summary>
/// <example><code>
/// // fire and forget: the button reflects the operation it owns
/// private readonly Mutation _save = new(todos.SaveAsync);
/// ui.ActionButton("Save", _save, new() { Icon = Icons.Save });
///
/// // with a result: applied before the next frame, or read straight from Latest
/// private readonly Mutation&lt;MarketSnapshot&gt; _load = new(
///     ct => feed.LoadAsync(ct),
///     snapshot => history.Load(snapshot.History));
/// if (ui.Button("Load")) _load.Run();
/// </code></example>
public sealed class Mutation<T> : IDisposable
{
    private readonly Lock _gate = new();
    private Func<CancellationToken, Task<T>>? _work;
    private readonly Action<T>? _apply;
    private CancellationTokenSource? _run;
    private Task<T>? _pending;
    private T? _latest;
    private Exception? _error;

    // The run this mutation is currently owned by. Bumped by every start or cancel under _gate,
    // read by a completion that arrives later: an operation the app has moved on from may not publish.
    private long _generation;
    private long _counted;        // the PendingWork id being counted, 0 when idle
    private volatile bool _disposed;

    /// <summary>
    /// Creates a mutation whose work is supplied at <see cref="Run(Func{CancellationToken, Task{T}})"/>
    /// or <see cref="Restart(Func{CancellationToken, Task{T}})"/>.
    /// </summary>
    public Mutation() : this(apply: null) { }

    /// <summary>
    /// Creates a mutation that hands each completed result to <paramref name="apply"/> on the UI
    /// thread before the next frame draws, with work supplied when it starts.
    /// </summary>
    public Mutation(Action<T>? apply)
    {
        _apply = apply;
        // A component is usually built on the UI thread, but DI may construct it earlier or
        // elsewhere. Start() binds the completion to the thread that actually starts the work.
        Frames = FrameDispatcher.Current;
        FrameBoundary.Install();
    }

    /// <summary>Creates a mutation around the work it runs.</summary>
    public Mutation(Func<CancellationToken, Task<T>> work) : this(work, apply: null) { }

    /// <summary>
    /// Creates a mutation that hands its result to <paramref name="apply"/> on the UI
    /// thread, before the next frame draws.
    /// </summary>
    public Mutation(Func<CancellationToken, Task<T>> work, Action<T>? apply) : this(apply)
    {
        ArgumentNullException.ThrowIfNull(work);
        _work = work;
    }

    /// <summary>
    /// The frame thread that applies this mutation's result. Captured when the mutation is created
    /// and reset on every start, so a mutation built ahead of the UI it belongs to still lands on
    /// the thread that starts its work.
    /// </summary>
    internal FrameDispatcher Frames { get; private set; }

    /// <summary>True while the operation is in flight.</summary>
    public bool IsRunning => Volatile.Read(ref _counted) != 0;

    /// <summary>
    /// The result of the last completed run, or default before the first completion or after
    /// <see cref="Cancel"/> / <see cref="Dispose"/>.
    /// </summary>
    public T? Latest
    {
        get
        {
            lock (_gate) return _latest;
        }
    }

    /// <summary>
    /// The failure that ended the last run, or null. A cancellation caused by
    /// <see cref="Restart()"/>, <see cref="Cancel"/> or <see cref="Dispose"/> is not a failure.
    /// Cached when the operation completes, so reading it every frame allocates nothing.
    /// </summary>
    public Exception? Error => Volatile.Read(ref _error);

    /// <summary>
    /// Starts the operation — on the pool, never on the frame thread. Re-entry is ignored, so it is
    /// safe to call every frame, and a run started after <see cref="Dispose"/> is ignored too: the
    /// component that owned it is gone.
    /// </summary>
    public void Run()
    {
        if (IsRunning || _disposed) return;
        if (_work is null)
            throw new InvalidOperationException("This Mutation has no work yet; pass work to the constructor or to Run(work).");
        Start(_work);
    }

    /// <summary>Starts <paramref name="work"/> if nothing is running.</summary>
    public void Run(Func<CancellationToken, Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (IsRunning || _disposed) return;
        Start(work);
    }

    /// <summary>Starts the last work again, cancelling the run it replaces.</summary>
    public void Restart()
    {
        if (_disposed) return;
        if (_work is null)
            throw new InvalidOperationException("This Mutation has no work yet; pass work to the constructor or to Restart(work).");
        Start(_work);
    }

    /// <summary>Replaces the work and starts it, cancelling whatever was running.</summary>
    public void Restart(Func<CancellationToken, Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (_disposed) return;
        Start(work);
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
            _pending = null;
            _latest = default;
            Volatile.Write(ref _error, null);
        }

        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }

        PendingWork.Settle(Interlocked.Exchange(ref _counted, 0), completed: false);
    }

    /// <summary>
    /// Takes the result if the operation finished. Needed only when the caller wants a one-shot
    /// consumption; <see cref="Latest"/> and the <c>apply</c> callback are the non-polling shapes.
    /// </summary>
    public bool TryPublish([NotNullWhen(true)] out T? result)
    {
        result = default;
        if (IsRunning || _pending is not { IsCompleted: true } completed) return false;

        _pending = null;
        if (!completed.IsCompletedSuccessfully) return false;

        result = completed.Result!;
        return true;
    }

    /// <summary>
    /// Cancels a running operation and drops its result. Optional: a mutation that is never disposed
    /// is cancelled when the process exits, which is what an app usually wants.
    /// <para>
    /// After this, a completion that arrives late — the work finished just before the page was
    /// navigated away from — is dropped instead of applying into a component that no longer exists.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _work = null;
        Cancel();
    }

    private void Start(Func<CancellationToken, Task<T>> work)
    {
        _work = work;

        // Called from Draw, the one place a mutation is meant to start: the frame that is drawing
        // is the frame that will apply the result, whatever thread the mutation was built on.
        Frames = FrameDispatcher.Current;

        var run = new CancellationTokenSource();
        CancellationToken token = run.Token;
        CancellationTokenSource? previous;
        long generation;

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
            _pending = null;
            Volatile.Write(ref _error, null);
        }

        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }

        long id = PendingWork.Add();
        long oldCounted = Interlocked.Exchange(ref _counted, id);
        PendingWork.Settle(oldCounted, completed: false);

        Task<T> task;
        try
        {
            // On the pool, because this is called from Draw: an `async` method runs its
            // synchronous prefix on whoever calls it, so invoking it here would make the frame
            // wait for work that was supposed to be off-frame. See skills/cui-dotnet/hosting.md.
            task = Task.Run(() => work(token), token);
        }
        catch (Exception error)
        {
            // A faulted start must not escape into the frame: report it like any
            // other failure so the component can show it.
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

        _pending = task;

        // Settle on completion, not on TryPublish: a component that is no longer
        // drawn never polls, and a finished operation must not keep the window
        // awake forever. The one owed frame is what makes the result visible.
        task.ContinueWith(
            static (completed, state) =>
            {
                (Mutation<T> owner, long run, long counted, CancellationTokenSource source) =
                    ((Mutation<T>, long, long, CancellationTokenSource))state!;
                owner.Complete(completed, run, counted, source);
            },
            (this, generation, id, run),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void Complete(Task<T> task, long generation, long counted, CancellationTokenSource run)
    {
        // A result is published only by the run that still owns this mutation. A run that
        // finished after the app moved on — restarted, navigated, disposed — is dropped, and
        // dropping it must not touch the newer run's state either.
        bool current;
        bool postApply = false;
        T? value = default;

        lock (_gate)
        {
            current = !_disposed && _generation == generation;
            if (current)
            {
                _run = null;

                if (task.IsCompletedSuccessfully)
                {
                    value = task.Result;
                    _latest = value;
                    postApply = _apply is not null;
                }
                else if (!task.IsCanceled || !run.IsCancellationRequested)
                {
                    if (!run.IsCancellationRequested)
                    {
                        Volatile.Write(
                            ref _error,
                            task.Exception?.GetBaseException() ?? new TaskCanceledException(task));
                    }
                }
            }
        }

        if (postApply) Completions.Post(this, value!, generation);

        run.Dispose();

        // Settle PendingWork and publish Error/Completions before clearing _counted: a caller that
        // observes IsRunning == false also observes the settled count and the final outcome.
        PendingWork.Settle(counted, completed: current);
        Interlocked.CompareExchange(ref _counted, 0, counted);
    }

    // Called on the UI thread, once per completed frame.
    internal void Apply(T value, long generation)
    {
        lock (_gate)
        {
            if (_disposed || _generation != generation) return;
            _pending = null;
        }

        _apply?.Invoke(value);
    }
}

/// <summary>A <see cref="Mutation{T}"/> whose work has no result.</summary>
public sealed class Mutation : IDisposable
{
    private readonly Mutation<bool> _inner;

    /// <summary>
    /// Creates a mutation whose work is supplied at <see cref="Run(Func{CancellationToken, Task})"/>
    /// or <see cref="Restart(Func{CancellationToken, Task})"/>.
    /// </summary>
    public Mutation() : this(onSuccess: null) { }

    /// <summary>
    /// Creates a mutation that calls <paramref name="onSuccess"/> on the UI thread before the next
    /// frame draws once the work completes, with work supplied when it starts.
    /// </summary>
    public Mutation(Action? onSuccess)
    {
        _inner = new Mutation<bool>(onSuccess is null ? null : _ => onSuccess());
    }

    /// <summary>Creates a mutation around the work it runs.</summary>
    public Mutation(Func<CancellationToken, Task> work) : this(work, onSuccess: null) { }

    /// <summary>
    /// Creates a mutation that calls <paramref name="onSuccess"/> on the UI thread,
    /// before the next frame draws, once the work completes.
    /// </summary>
    public Mutation(Func<CancellationToken, Task> work, Action? onSuccess)
    {
        ArgumentNullException.ThrowIfNull(work);
        _inner = new Mutation<bool>(
            Wrap(work),
            onSuccess is null ? null : _ => onSuccess());
    }

    /// <summary>True while the operation is in flight.</summary>
    public bool IsRunning => _inner.IsRunning;

    /// <summary>The failure that ended the last run, or null. Read it every frame; it costs nothing.</summary>
    public Exception? Error => _inner.Error;

    /// <summary>Starts the operation. Re-entry is ignored, so it is safe to call every frame.</summary>
    public void Run() => _inner.Run();

    /// <summary>Starts <paramref name="work"/> if nothing is running.</summary>
    public void Run(Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        _inner.Run(Wrap(work));
    }

    /// <summary>Starts the last work again, cancelling the run it replaces.</summary>
    public void Restart() => _inner.Restart();

    /// <summary>Replaces the work and starts it, cancelling whatever was running.</summary>
    public void Restart(Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        _inner.Restart(Wrap(work));
    }

    /// <summary>Cancels a running operation and clears <see cref="Error"/>, keeping this instance reusable.</summary>
    public void Cancel() => _inner.Cancel();

    /// <summary>Cancels a running operation. Optional.</summary>
    public void Dispose() => _inner.Dispose();

    private static Func<CancellationToken, Task<bool>> Wrap(Func<CancellationToken, Task> work) =>
        async token =>
        {
            await work(token).ConfigureAwait(false);
            return true;
        };
}

/// <summary>
/// Work that finished on a pool thread and still has to reach the UI thread.
/// <para>
/// The frame boundary is the only place it may run, so a frame never observes a
/// half-applied result: everything queued is applied at the start of a frame, and
/// that frame draws the new state. Only a frame on the thread which started the work drains its
/// completions — see <see cref="FrameDispatcher"/>.
/// </para>
/// </summary>
internal static class Completions
{
    internal static void Post<T>(Mutation<T> owner, T value, long generation) =>
        owner.Frames.Post(() => owner.Apply(value, generation));

    /// <summary>Runs everything that finished since the last frame, on the calling thread.</summary>
    internal static void Drain() => FrameDispatcher.Current.Drain();
}
