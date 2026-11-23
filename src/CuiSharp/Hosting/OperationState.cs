namespace CuiSharp.Hosting;

/// <summary>The shared run lifecycle of a mutation and a progressive operation.</summary>
internal sealed class OperationState
{
    internal readonly Lock Gate = new();
    private FrameDispatcher? _owner;
    private Thread? _ownerThread;
    private volatile Run? _current;
    private volatile Exception? _error;
    private bool _disposed;

    internal bool IsRunning => _current is { Running: true };
    internal Exception? Error => _error;

    internal bool CanStart(bool replace)
    {
        CheckThread();
        return !_disposed && (replace || !IsRunning);
    }

    internal void Start(Func<Run, Task> work, float seconds = 0f, Action? reset = null, Action? onSuccess = null)
    {
        Run run = Begin(seconds, reset);
        // Always enter Execute, even if the caller cancels before the pool gets here: it
        // owns disposal of the source. Calling the delegate here instead would also run its
        // synchronous prefix inside Draw.
        _ = Task.Run(() => Execute(run, work, onSuccess));
    }

    internal void Start<T>(Func<CancellationToken, Task<T>> work, Action<T> store, Action<T>? apply)
    {
        Run run = Begin(seconds: 0f, reset: null);
        _ = Task.Run(() => Execute(run, work, store, apply));
    }

    private Run Begin(float seconds, Action? reset)
    {
        FrameDispatcher owner = FrameDispatcher.ForCurrentFrame();
        if (_owner is not null && _owner != owner)
            throw new InvalidOperationException("A background operation belongs to the window or surface that first started it.");

        _owner = owner;
        _ownerThread = Thread.CurrentThread;
        var run = new Run(owner);
        lock (Gate)
        {
            Abandon();
            _error = null;
            reset?.Invoke();
            owner.Start(run, seconds);
            _current = run;
        }

        return run;
    }

    internal void Stop(Action? reset = null, bool dispose = false)
    {
        if (_disposed) return;
        CheckThread();
        lock (Gate)
        {
            Abandon();
            reset?.Invoke();
            _error = null;
            _disposed = dispose;
        }
    }

    internal void Publish<T>(Run run, T value, Action<T> store)
    {
        lock (Gate)
        {
            if (_current != run || !run.Running) return;
            store(value);
        }
    }

    private void Apply(Run run, Action apply)
    {
        // Run/Restart/Cancel/Dispose are confined to this same frame thread. The worker
        // cannot retire this run between that check and the callback.
        if (_current != run) return;
        try
        {
            apply();
        }
        catch (Exception error)
        {
            if (_current == run) _error = error;
            UiFrameState.Drawing?.Faults?.Add(error);
        }
    }

    // With Gate held. Drop the run immediately; invoking cancellation callbacks is not UI work.
    private void Abandon()
    {
        if (_current is { Running: true } run)
        {
            run.Running = false;
            run.Owner.Abandon(run);
            run.Cancellation = run.Source.CancelAsync();
        }

        _current = null;
    }

    private async Task Execute(Run run, Func<Run, Task> work, Action? onSuccess)
    {
        bool succeeded = false;
        Exception? error = null;
        try
        {
            run.Token.ThrowIfCancellationRequested();
            await work(run).ConfigureAwait(false);
            succeeded = true;
        }
        catch (OperationCanceledException)
        {
            // A cancelled or superseded run is not a failed run, regardless of who cancelled it.
        }
        catch (Exception failure)
        {
            error = failure;
        }

        await Finish(run, error, commit: null, succeeded ? onSuccess : null).ConfigureAwait(false);
    }

    private async Task Execute<T>(Run run, Func<CancellationToken, Task<T>> work, Action<T> store, Action<T>? apply)
    {
        T? value = default;
        bool succeeded = false;
        Exception? error = null;
        try
        {
            run.Token.ThrowIfCancellationRequested();
            value = await work(run.Token).ConfigureAwait(false);
            succeeded = true;
        }
        catch (OperationCanceledException)
        {
            // A cancelled or superseded run is not a failed run, regardless of who cancelled it.
        }
        catch (Exception failure)
        {
            error = failure;
        }

        Action? commit = succeeded ? () => store(value!) : null;
        Action? onComplete = succeeded && apply is not null ? () => apply(value!) : null;
        await Finish(run, error, commit, onComplete).ConfigureAwait(false);
    }

    private async Task Finish(Run run, Exception? error, Action? commit, Action? apply)
    {
        Task? cancellation;
        lock (Gate)
        {
            if (_current == run)
            {
                commit?.Invoke();
                _error = error;
                run.Owner.Complete(run, apply is null ? null : () => Apply(run, apply));
            }

            cancellation = run.Cancellation;
        }

        try
        {
            if (cancellation is not null) await cancellation.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Observe exceptions from user cancellation callbacks without reporting them as
            // the outcome of a run the caller has already abandoned.
        }
        finally
        {
            run.Source.Dispose();
        }
    }

    private void CheckThread()
    {
        if (_ownerThread is not null && _ownerThread != Thread.CurrentThread)
            throw new InvalidOperationException("Control a background operation on the thread that started it.");
    }

    internal sealed class Run(FrameDispatcher owner)
    {
        internal readonly CancellationTokenSource Source = new();
        internal readonly FrameDispatcher Owner = owner;
        internal CancellationToken Token => Source.Token;
        internal volatile bool Running = true;
        internal Task? Cancellation;
    }
}
