using System.Reflection.Metadata;
using CuiSharp.Interop;

// The runtime finds this handler by name, on the thread that applied the delta. Trimming is not a
// worry for it: a delta can only be applied to a JIT build, and the one trimmed build this library
// shapes — a self-contained AOT publish — has MetadataUpdaterSupport off, so the handler is
// unreachable there by construction.
[assembly: MetadataUpdateHandler(typeof(CuiSharp.Hosting.HotReloadWake))]

namespace CuiSharp.Hosting;

/// <summary>
/// Draws the frame a hot reload invalidates, so an edit reaches the screen without the user
/// touching the window.
/// </summary>
/// <remarks>
/// A CUI window waits inside the OS message loop when nothing is animating, and a delta applied by
/// <c>dotnet watch</c> only rewrites method bodies: nothing about the sleeping process says "draw
/// again", so the reloaded UI stays invisible until the user moves the mouse. The runtime does
/// report the reload — a type named by <c>[assembly: MetadataUpdateHandler]</c> gets
/// <see cref="ClearCache"/> before the delta and <see cref="UpdateApplication"/> once it is live.
/// <para>
/// <see cref="UpdateApplication"/> runs on the thread that applied the delta, so it may run
/// concurrently with a frame and must not touch a <c>cui_ctx</c>. It wakes each window the process
/// owns the way the rest of this framework does, by posting a message: the win32 backend waits in
/// <c>MsgWaitForMultipleObjectsEx</c> on <c>QS_POSTMESSAGE</c>, so the wait returns, the queue
/// drains and the next frame draws the reloaded code. No polling, and an idle window still costs
/// nothing.
/// </para>
/// <para>
/// A backend that cannot be woken from another thread gets the fallback instead — see
/// <see cref="HeartbeatArmed"/>, and <c>docs/PLAN.md</c> for the upstream wake API that would
/// retire it.
/// </para>
/// </remarks>
internal static class HotReloadWake
{
    /// <summary>Longest a window without a wake mechanism sleeps between frames, in seconds.</summary>
    internal const float HeartbeatSeconds = 0.5f;

    /// <summary>Guards <see cref="s_windows"/>: the delta thread writes nothing else here.</summary>
    private static readonly object Gate = new();

    /// <summary>
    /// Every window this process owns. An app has one (<c>docs/PLAN.md</c>: multi-window is
    /// upstream's question), but a test process opens several at once, and a reload has to reach
    /// all of them rather than whichever attached last.
    /// </summary>
    private static readonly List<nint> s_windows = [];

    private static long s_reloads;
    private static bool? s_watchSession;

    /// <summary>How many times a reload has been applied to this process.</summary>
    internal static long Reloads => Interlocked.Read(ref s_reloads);

    /// <summary>Whether this process was started by <c>dotnet watch</c>, which marks its children.</summary>
    internal static bool WatchSession =>
        s_watchSession ??= Environment.GetEnvironmentVariable("DOTNET_WATCH") == "1";

    /// <summary>
    /// Whether the frame loop has to poll to notice a reload, because this platform offers no way
    /// to wake it: only under <c>dotnet watch</c>, and only where the wake is missing.
    /// </summary>
    internal static bool HeartbeatArmed => HeartbeatArmedFor(WatchSession, OperatingSystem.IsWindows());

    /// <summary>
    /// The decision behind <see cref="HeartbeatArmed"/>, written so the four answers it can give
    /// are checkable on any machine rather than only on the platform that needs the fallback.
    /// </summary>
    internal static bool HeartbeatArmedFor(bool watchSession, bool windows) => watchSession && !windows;

    /// <summary>
    /// The runtime calls this before the delta is applied. An immediate-mode UI has nothing to
    /// invalidate: the next frame rebuilds the whole tree in call order, from the app's own fields.
    /// </summary>
    public static void ClearCache(Type[]? updatedTypes)
    {
    }

    /// <summary>
    /// The runtime calls this once the delta is live: the next frame draws the reloaded code.
    /// </summary>
    public static void UpdateApplication(Type[]? updatedTypes) => Notify();

    /// <summary>
    /// Records the reload and asks every window for a frame. Called from the delta applier's thread,
    /// so it only posts messages and never blocks; returns whether a wake was actually posted,
    /// which is false when no window is attached or none of them is left to post to.
    /// </summary>
    internal static bool Notify()
    {
        Interlocked.Increment(ref s_reloads);

        // Posting under the lock keeps a reload landing at the same moment as a detach from
        // waking a handle the loop no longer owns. The message is WM_NULL: the smallest thing
        // that sets QS_POSTMESSAGE, dispatched to DefWindowProc and dropped without effect.
        lock (Gate)
        {
            bool posted = false;
            foreach (nint window in s_windows)
                posted |= Win32.PostMessage(window, Win32.WmNull, 0, 0) != 0;

            return posted;
        }
    }

    /// <summary>Remembers a window to wake. Called on the UI thread once the window exists.</summary>
    internal static void Attach(nint hwnd)
    {
        if (hwnd == 0 || !OperatingSystem.IsWindows()) return;   // nowhere to post, so nothing to remember
        lock (Gate)
        {
            if (!s_windows.Contains(hwnd)) s_windows.Add(hwnd);
        }
    }

    /// <summary>Forgets a window that is going away, so a late reload cannot post to it.</summary>
    internal static void Detach(nint hwnd)
    {
        lock (Gate) s_windows.Remove(hwnd);
    }

    /// <summary>
    /// End-of-frame housekeeping for the window loop: on a platform that cannot be woken, promise
    /// the loop a next frame while a developer is watching, so a reloaded body still gets drawn.
    /// Asked from the UI thread only — the same thread that owns the <c>cui_ctx</c>.
    /// </summary>
    internal static void ForFrame(nint ui)
    {
        if (HeartbeatArmed) Native.RequestWakeup(ui, HeartbeatSeconds);
    }

    /// <summary>Re-reads the environment, the seam the tests simulate a watch session through.</summary>
    internal static void Reset() => s_watchSession = null;
}
