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
/// <see cref="Error"/> and <see cref="IsRunning"/> are always current and cost
/// nothing to read, so a fire-and-forget mutation needs no bookkeeping in
/// <c>Draw</c>. Pass an <c>apply</c> callback when the result has to change state,
/// and <c>Draw</c> never polls: the callback runs on the UI thread, before the
/// next frame draws.
/// </para>
/// </summary>
/// <example><code>
/// // fire and forget: the button reflects the operation it owns
/// private readonly Mutation _save = new(todos.SaveAsync);
/// if (ui.Button("Save", new() { Loading = _save.IsRunning })) _save.Run();
/// if (_save.Error is { } e) ui.Badge(e.Message, new() { Tone = Tone.Danger });
///
/// // with a result: applied before the next frame, so Draw never polls
/// private readonly Mutation&lt;MarketSnapshot&gt; _load = new(
///     ct => feed.LoadAsync(ct),
///     snapshot => history.Load(snapshot.History));
/// if (ui.Button("Load")) _load.Run();
/// </code></example>
public sealed class Mutation<T> : IDisposable
{
    private readonly Func<CancellationToken, Task<T>> _work;
    private readonly Action<T>? _apply;
    private readonly CancellationTokenSource _lifetime = new();
    private Task<T>? _pending;
    private Exception? _error;

    // The run this mutation is currently owned by. Bumped by every Run, read by a completion
    // that arrives later: an operation the app has moved on from may not publish.
    private long _generation;
    private long _counted;        // the generation PendingWork is counting, 0 for none
    private volatile bool _disposed;

    /// <summary>Creates a mutation around the work it runs.</summary>
    public Mutation(Func<CancellationToken, Task<T>> work) : this(work, apply: null) { }

    /// <summary>
    /// Creates a mutation that hands its result to <paramref name="apply"/> on the UI
    /// thread, before the next frame draws.
    /// </summary>
    public Mutation(Func<CancellationToken, Task<T>> work, Action<T>? apply)
    {
        ArgumentNullException.ThrowIfNull(work);
        _work = work;
        _apply = apply;
        // A component is usually built on the UI thread, but DI may construct it earlier or
        // elsewhere. Run() binds the completion to the thread that actually starts the work.
        Frames = FrameDispatcher.Current;
    }

    /// <summary>
    /// The frame thread that applies this mutation's result. Captured when the mutation is created
    /// and reset on every <see cref="Run"/> call, so a mutation built ahead of the UI it belongs to
    /// still lands on the thread that starts its work.
    /// </summary>
    internal FrameDispatcher Frames { get; private set; }

    /// <summary>True while the operation is in flight.</summary>
    public bool IsRunning => _pending is { IsCompleted: false };

    /// <summary>
    /// The failure that ended the last run, or null. Cached when the operation
    /// completes, so reading it every frame allocates nothing.
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

        // Called from Draw, the one place a mutation is meant to start: the frame that is drawing
        // is the frame that will apply the result, whatever thread the mutation was built on.
        Frames = FrameDispatcher.Current;

        Volatile.Write(ref _error, null);
        _pending = null;
        long generation = Interlocked.Increment(ref _generation);

        CancellationToken token;
        try
        {
            token = _lifetime.Token;
        }
        catch (ObjectDisposedException error)
        {
            Volatile.Write(ref _error, error);
            return;
        }

        Task<T> task;
        try
        {
            // On the pool, because this is called from Draw: an `async` method runs its
            // synchronous prefix on whoever calls it, so invoking it here would make the frame
            // wait for work that was supposed to be off-frame. See skills/cui-dotnet/hosting.md.
            task = Task.Run(() => _work(token), token);
        }
        catch (Exception error)
        {
            // A faulted start must not escape into the frame: report it like any
            // other failure so the component can show it.
            Volatile.Write(ref _error, error.GetBaseException());
            return;
        }

        _pending = task;
        PendingWork.Add();
        Interlocked.Exchange(ref _counted, generation);

        // Settle on completion, not on TryPublish: a component that is no longer
        // drawn never polls, and a finished operation must not keep the window
        // awake forever. The one owed frame is what makes the result visible.
        task.ContinueWith(
            static (completed, state) =>
            {
                (Mutation<T> owner, long run) = ((Mutation<T>, long))state!;
                owner.Complete(completed, run);
            },
            (this, generation),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Takes the result if the operation finished. Needed only when the result has no
    /// <c>apply</c> callback; an operation that was given one has already applied it.
    /// </summary>
    public bool TryPublish([NotNullWhen(true)] out T? result)
    {
        result = default;
        if (_pending is not { IsCompleted: true } completed) return false;

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
        _lifetime.Cancel();
        _lifetime.Dispose();
        _pending = null;
        Settle(Volatile.Read(ref _counted));
    }

    private void Complete(Task<T> task, long generation)
    {
        // A result is published only by the run that still owns this mutation. A run that
        // finished after the app moved on — restarted, navigated, disposed — is dropped, and
        // dropping it must not touch the newer run's state either.
        bool current = IsCurrent(generation);

        if (task.IsCompletedSuccessfully)
        {
            // A result with somewhere to go is applied on the UI thread, before the
            // next frame, so Draw reads state instead of polling.
            if (_apply is not null && current) Completions.Post(this, task.Result, generation);
        }
        else if (current)
        {
            // Error is the completion signal callers observe. Publish it only after PendingWork
            // has been settled, so observing the error also observes the matching count update.
            Settle(generation);
            Volatile.Write(ref _error, task.Exception?.GetBaseException() ?? new TaskCanceledException(task));
            return;
        }

        Settle(generation);
    }

    private bool IsCurrent(long generation) => !_disposed && Volatile.Read(ref _generation) == generation;

    // Called on the UI thread, once per completed frame.
    internal void Apply(T value, long generation)
    {
        if (!IsCurrent(generation)) return;
        _pending = null;
        _apply?.Invoke(value);
    }

    // Each run counts pending work under its own generation, so a stale completion settles its
    // own count and never the newer operation's.
    private void Settle(long generation)
    {
        if (generation == 0) return;
        if (Interlocked.CompareExchange(ref _counted, 0, generation) != generation) return;
        PendingWork.Remove();
    }
}

/// <summary>A <see cref="Mutation{T}"/> whose work has no result.</summary>
public sealed class Mutation : IDisposable
{
    private readonly Mutation<bool> _inner;

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
            async token =>
            {
                await work(token).ConfigureAwait(false);
                return true;
            },
            onSuccess is null ? null : _ => onSuccess());
    }

    /// <summary>True while the operation is in flight.</summary>
    public bool IsRunning => _inner.IsRunning;

    /// <summary>The failure that ended the last run, or null. Read it every frame; it costs nothing.</summary>
    public Exception? Error => _inner.Error;

    /// <summary>Starts the operation. Re-entry is ignored, so it is safe to call every frame.</summary>
    public void Run() => _inner.Run();

    /// <summary>Cancels a running operation. Optional.</summary>
    public void Dispose() => _inner.Dispose();
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
