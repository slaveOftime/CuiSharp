namespace CuiSharp.Hosting;

/// <summary>
/// Optional per-frame trace for numbers when there is no console to watch — a
/// WinExe has none, and a window that is drawing badly is the one case where a
/// trace is worth more than a footer.
/// <para>
/// Set <c>CUI_TRACE=trace.txt</c> before running: the host writes the first frame
/// and then every 60th, outside the measured draw. Each line is one frame's cost,
/// its allocation, and how much work is still in flight. With the variable unset
/// this costs a null check per frame.
/// </para>
/// </summary>
internal static class FrameTrace
{
    private const int Every = 60;

    private static readonly StreamWriter? Writer = Open();
    private static long _frames;

    internal static void Frame()
    {
        if (Writer is not { } writer) return;

        // The first frame included, so a page that then sleeps still reports once.
        if (++_frames is not 1 && _frames % Every != 0) return;

        writer.WriteLine(
            $"frame {FrameStats.FrameMs:0.0} ms  draw {FrameStats.DrawMs:0.00} ms  " +
            $"alloc {FrameStats.AllocatedBytes} B  inflight {Component.PendingOperations}");
    }

    internal static void Close() => Writer?.Dispose();

    private static StreamWriter? Open()
    {
        var path = Environment.GetEnvironmentVariable("CUI_TRACE");
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            return new StreamWriter(path, append: false) { AutoFlush = true };
        }
        catch (IOException)
        {
            // A trace must never be able to stop an app from starting.
            return null;
        }
    }
}
