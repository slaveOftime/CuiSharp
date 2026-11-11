using System.Collections.Concurrent;

namespace CuiSharp.Hosting;

/// <summary>
/// The thread-bound queue for mutation results waiting at a frame boundary.
/// <para>
/// Work that finished on a pool thread may only be applied at a frame boundary on the thread that
/// called <see cref="Mutation{T}.Run"/>. A process-wide queue instead lets a frame on another
/// thread apply that result, so the queue belongs to the dispatcher and a dispatcher belongs to one
/// thread.
/// </para>
/// <para>
/// It is found through a thread-static rather than a registry, so a dead thread's dispatcher is
/// never handed to its replacement, and draining an empty queue reads one field and returns: the
/// steady-state cost of the mechanism on a frame is nothing.
/// </para>
/// </summary>
internal sealed class FrameDispatcher
{
    private readonly ConcurrentQueue<Action> _queue = new();

    [ThreadStatic] private static FrameDispatcher? _current;

    /// <summary>The frame thread of the calling thread, created on first use.</summary>
    internal static FrameDispatcher Current => _current ??= new();

    /// <summary>Queues a completion for the next frame this thread draws.</summary>
    internal void Post(Action apply) => _queue.Enqueue(apply);

    /// <summary>Runs everything that finished since the last frame, on the calling thread.</summary>
    internal void Drain()
    {
        while (_queue.TryDequeue(out Action? apply)) apply();
    }
}
