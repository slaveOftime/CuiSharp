using CuiSharp.Interop;

namespace CuiSharp.Hosting;

/// <summary>Completion delivery and redraw demand for one window or surface.</summary>
internal sealed class FrameDispatcher(nint wakeWindow) : IFrameWork
{
    private readonly Lock _gate = new();
    private readonly Dictionary<OperationState.Run, float> _running = [];
    private List<Action> _queued = [];
    private List<Action> _delivery = [];
    private bool _due;
    private bool _closed;
    private float _nextFrameSeconds = -1f;
    private static int _pendingOperations;

    internal static int PendingOperations => Volatile.Read(ref _pendingOperations);
    public float NextFrameSeconds => Volatile.Read(ref _nextFrameSeconds);

    // There is no thread-wide fallback: the same thread can draw several independent surfaces.
    internal static FrameDispatcher ForCurrentFrame()
    {
        UiFrameState frame = UiFrameState.Drawing ?? throw new InvalidOperationException(
            "Start a background operation while drawing its window or surface, for example from a click or OnInitialized.");
        return (FrameDispatcher)(frame.Work ??= new FrameDispatcher(frame.WakeWindow));
    }

    internal void Start(OperationState.Run run, float seconds)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _running.Add(run, seconds);
            Refresh();
            Interlocked.Increment(ref _pendingOperations);
        }
    }

    internal void Abandon(OperationState.Run run)
    {
        lock (_gate)
        {
            if (!_running.Remove(run)) return;
            Refresh();
            Interlocked.Decrement(ref _pendingOperations);
        }
    }

    internal void Complete(OperationState.Run run, Action? apply)
    {
        lock (_gate)
        {
            bool open = _running.Remove(run);
            if (open)
            {
                if (apply is not null) _queued.Add(apply);
                Volatile.Write(ref _due, true);
                Refresh();
                Interlocked.Decrement(ref _pendingOperations);
            }

            // Publish the settled worker before Drain can take this completion. A draw that
            // handles it must not still show loading, and a callback can safely start again.
            run.Running = false;

            // Off Windows the last active frame already armed the next wake. On Windows
            // interrupt that wait without touching a CUI context from the worker.
            if (open && wakeWindow != 0) Win32.PostMessage(wakeWindow, Win32.WmNull, 0, 0);
        }
    }

    public void Drain()
    {
        if (!Volatile.Read(ref _due)) return;
        lock (_gate)
        {
            Volatile.Write(ref _due, false);
            (_queued, _delivery) = (_delivery, _queued);
            Refresh();
        }

        // Take one batch before Draw. New arrivals belong to the next frame, including ones
        // started by a completion callback. No user code runs under the dispatcher's lock.
        try
        {
            foreach (Action apply in _delivery) apply();
        }
        finally
        {
            _delivery.Clear();
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            Interlocked.Add(ref _pendingOperations, -_running.Count);
            _running.Clear();
            _queued.Clear();
            Volatile.Write(ref _due, false);
            Refresh();
        }
    }

    // One published number is all a frame needs: -1 idle, 0 immediate, positive a timer.
    private void Refresh()
    {
        float shortest = _due ? 0f : -1f;
        if (!_due)
        {
            foreach (float seconds in _running.Values)
            {
                if (shortest < 0f || seconds < shortest) shortest = seconds;
                if (shortest == 0f) break;
            }
        }

        Volatile.Write(ref _nextFrameSeconds, shortest);
    }
}
