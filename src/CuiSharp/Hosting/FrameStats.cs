using System.Diagnostics;

namespace CuiSharp.Hosting;

/// <summary>What one frame cost. <see cref="FrameStats"/> holds the last one in the process.</summary>
/// <param name="Frames">Frames rendered since the process started.</param>
/// <param name="FrameMs">Wall-clock time between this frame and the previous one.</param>
/// <param name="DrawMs">Time spent drawing this frame's components.</param>
/// <param name="AllocatedBytes">Managed bytes allocated while drawing it.</param>
/// <param name="Fps">Smoothed frame rate of the run this frame belongs to.</param>
/// <param name="IsLive">Whether anything asked for this frame.</param>
public readonly record struct FrameSample(
    long Frames,
    double FrameMs,
    double DrawMs,
    long AllocatedBytes,
    double Fps,
    bool IsLive);

/// <summary>
/// Per-frame numbers for the frame that just rendered, so a footer or a trace can
/// show them without every app measuring the clock itself.
/// <para>
/// The host fills these in around each component render; nothing here allocates.
/// </para>
/// </summary>
/// <example><code>
/// ui.Secondary($"draw {FrameStats.DrawMs:0.00} ms");
/// ui.Secondary($"alloc {FrameStats.AllocatedBytes} B");
/// ui.Secondary(FrameStats.IsLive ? $"{FrameStats.Fps:0} fps" : "idle");
/// </code></example>
/// <remarks>
/// <see cref="FrameMs"/> is wall-clock time, so it says how often the app is really drawing.
/// CUI sleeps when nothing asks for a frame, which is why an idle app uses no CPU — and why one
/// mouse move after two minutes of silence would otherwise report a 120 000 ms "frame".
/// <see cref="Fps"/> therefore only averages the frames that were part of a run of frames, and
/// <see cref="IsLive"/> says whether the app is in such a run at all.
/// </remarks>
public static class FrameStats
{
    /// <summary>
    /// A gap longer than this means the loop was asleep, not slow: at 4 frames per second a
    /// window is still being redrawn, and below that it was idle and woke up.
    /// </summary>
    private const double IdleMs = 250;

    private static long _lastFrame;
    private static long _lastDrawnFrame;

    /// <summary>Number of frames rendered since the process started.</summary>
    public static long Frames { get; private set; }

    /// <summary>Wall-clock time between the start of this frame and the previous one.</summary>
    public static double FrameMs { get; private set; }

    /// <summary>Time spent drawing this frame's components.</summary>
    public static double DrawMs { get; private set; }

    /// <summary>
    /// Managed bytes allocated while drawing this frame. It should read 0: the
    /// frame's own numbers are the cheapest way to notice a hidden allocation.
    /// </summary>
    public static long AllocatedBytes { get; private set; }

    /// <summary>
    /// Frames per second, smoothed over the frames of the current run. Zero until there is
    /// something to average, and unchanged while the app is asleep — see the remarks on this type.
    /// </summary>
    public static double Fps { get; private set; }

    /// <summary>
    /// True when something asked for this frame: a component that changes on its own
    /// (<c>WantsFrames</c>) or work that is still in flight. An app that says false is asleep
    /// between input, so a frame rate would be a number about nothing.
    /// </summary>
    public static bool IsLive { get; private set; }

    /// <summary>Opens a frame. Called by the host before the root component renders.</summary>
    internal static long Begin()
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastDrawnFrame is not 0) FrameMs = Elapsed(_lastDrawnFrame, now);

        Frames++;
        _lastFrame = now;
        return GC.GetAllocatedBytesForCurrentThread();
    }

    /// <summary>Closes a frame and returns its numbers. Called by the host after the component rendered.</summary>
    internal static FrameSample End(long allocatedAtStart)
    {
        AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart;
        DrawMs = Elapsed(_lastFrame, Stopwatch.GetTimestamp());
        IsLive = Component.AnyWantFrames || PendingWork.Any;

        // The first frame of a run has no previous frame to measure against, and the frame after
        // a sleep measures the sleep. Both leave the average alone rather than dragging it to 0.
        if (FrameMs > 0 && FrameMs <= IdleMs)
        {
            double instant = 1000.0 / FrameMs;
            Fps = Fps == 0 ? instant : (Fps * 0.9) + (instant * 0.1);
        }

        _lastDrawnFrame = _lastFrame;
        return new FrameSample(Frames, FrameMs, DrawMs, AllocatedBytes, Fps, IsLive);
    }

    private static double Elapsed(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;
}
