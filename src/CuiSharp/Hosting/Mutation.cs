namespace CuiSharp.Hosting;

/// <summary>
/// One background operation with a result. Start it from a click or <c>OnInitialized</c>;
/// draw its <see cref="Latest"/>, <see cref="IsRunning"/> and <see cref="Error"/> as needed.
/// The work is invoked on the pool and the host arranges a frame for its outcome.
/// </summary>
/// <remarks>
/// The first start binds the operation to the window or surface being drawn. Start and restart
/// during its drawing, and cancel or dispose on its thread. Dispose it when its app or page owner
/// goes away. The worker should return data, not edit UI-owned state; pass an <c>apply</c> callback
/// for a one-time update to that state before the owner next draws.
/// </remarks>
public sealed class Mutation<T> : IDisposable
{
    private readonly OperationState _state = new();
    private readonly Action<T>? _apply;
    private Func<CancellationToken, Task<T>>? _work;
    private T? _latest;

    /// <summary>Creates a mutation whose work is supplied at its first start.</summary>
    public Mutation() : this(apply: null) { }

    /// <summary>Creates a mutation with a frame-thread callback and work supplied at its first start.</summary>
    public Mutation(Action<T>? apply) => _apply = apply;

    /// <summary>Creates a mutation around the work it runs.</summary>
    public Mutation(Func<CancellationToken, Task<T>> work) : this(work, apply: null) { }

    /// <summary>Creates a mutation with a callback to apply a successful result before the owner draws.</summary>
    public Mutation(Func<CancellationToken, Task<T>> work, Action<T>? apply) : this(apply)
    {
        ArgumentNullException.ThrowIfNull(work);
        _work = work;
    }

    /// <summary>True while the current worker is in flight; its apply callback may still be queued afterwards.</summary>
    public bool IsRunning => _state.IsRunning;

    /// <summary>
    /// The last successful worker result, available before its apply callback runs. A new run
    /// keeps the previous value until it succeeds. Initially and after cancellation, this is default.
    /// </summary>
    public T? Latest
    {
        get { lock (_state.Gate) return _latest; }
    }

    /// <summary>The current run's worker or apply failure, or null. Cancellation is not a failure.</summary>
    public Exception? Error => _state.Error;

    /// <summary>Starts the configured work from the owning frame. Does nothing while running or after disposal.</summary>
    public void Run()
    {
        if (_state.CanStart(replace: false)) Start(Work);
    }

    /// <summary>Starts and remembers the supplied work if nothing is running.</summary>
    public void Run(Func<CancellationToken, Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (_state.CanStart(replace: false)) Start(work);
    }

    /// <summary>Supersedes the current run and starts the configured work again.</summary>
    public void Restart()
    {
        if (_state.CanStart(replace: true)) Start(Work);
    }

    /// <summary>Supersedes the current run and starts and remembers the supplied work.</summary>
    public void Restart(Func<CancellationToken, Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (_state.CanStart(replace: true)) Start(work);
    }

    /// <summary>Requests cancellation, drops pending apply and clears the result and error. Does not wait.</summary>
    public void Cancel() => _state.Stop(Clear);

    /// <summary>Cancels the operation and prevents later starts. Dispose on its owning thread.</summary>
    public void Dispose()
    {
        _state.Stop(Clear, dispose: true);
        _work = null;
    }

    private Func<CancellationToken, Task<T>> Work => _work ?? throw new InvalidOperationException(
        "This Mutation has no work yet; pass work to the constructor, Run or Restart.");

    private void Start(Func<CancellationToken, Task<T>> work)
    {
        _state.Start(work, Store, _apply);
        _work = work;
    }

    private void Store(T value) => _latest = value;

    private void Clear() => _latest = default;
}

/// <summary>A <see cref="Mutation{T}"/> whose work has no result. The same frame ownership rules apply.</summary>
public sealed class Mutation : IDisposable
{
    private readonly OperationState _state = new();
    private readonly Action? _onSuccess;
    private Func<CancellationToken, Task>? _work;

    /// <summary>Creates a mutation whose work is supplied at its first start.</summary>
    public Mutation() : this(onSuccess: null) { }

    /// <summary>Creates a mutation with a frame-thread success callback and work supplied later.</summary>
    public Mutation(Action? onSuccess) => _onSuccess = onSuccess;

    /// <summary>Creates a mutation around the work it runs.</summary>
    public Mutation(Func<CancellationToken, Task> work) : this(work, onSuccess: null) { }

    /// <summary>Creates a mutation with a callback to run before the owning frame draws after success.</summary>
    public Mutation(Func<CancellationToken, Task> work, Action? onSuccess) : this(onSuccess)
    {
        ArgumentNullException.ThrowIfNull(work);
        _work = work;
    }

    /// <summary>True while the worker is in flight; a success callback may still be queued.</summary>
    public bool IsRunning => _state.IsRunning;

    /// <summary>The worker or success callback failure, or null. Cancellation is not a failure.</summary>
    public Exception? Error => _state.Error;

    /// <summary>Starts the configured work from the owning frame. Does nothing while running or after disposal.</summary>
    public void Run()
    {
        if (_state.CanStart(replace: false)) Start(Work);
    }

    /// <summary>Starts and remembers the supplied work if nothing is running.</summary>
    public void Run(Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (_state.CanStart(replace: false)) Start(work);
    }

    /// <summary>Supersedes the current run and starts the configured work again.</summary>
    public void Restart()
    {
        if (_state.CanStart(replace: true)) Start(Work);
    }

    /// <summary>Supersedes the current run and starts and remembers the supplied work.</summary>
    public void Restart(Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (_state.CanStart(replace: true)) Start(work);
    }

    /// <summary>Requests cancellation and clears the error, without waiting for the worker.</summary>
    public void Cancel() => _state.Stop();

    /// <summary>Cancels the operation and prevents later starts. Dispose on its owning thread.</summary>
    public void Dispose()
    {
        _state.Stop(dispose: true);
        _work = null;
    }

    private Func<CancellationToken, Task> Work => _work ?? throw new InvalidOperationException(
        "This Mutation has no work yet; pass work to the constructor, Run or Restart.");

    private void Start(Func<CancellationToken, Task> work)
    {
        _state.Start(run => work(run.Token), onSuccess: _onSuccess);
        _work = work;
    }
}
