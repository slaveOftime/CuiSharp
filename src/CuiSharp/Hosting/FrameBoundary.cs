using CuiSharp.Interop;

namespace CuiSharp.Hosting;

/// <summary>
/// What every host does at a frame boundary, in one place.
/// <para>
/// A result finished on a pool thread may only be applied on the UI thread, and a sleeping window
/// cannot be woken by that thread. Both are properties of the host, not of a component, so
/// <see cref="CuiApp.Run(CuiAppOptions, DrawFn)"/>, the manual <see cref="CuiApp.Frame"/> loop and
/// the headless host all enter and leave a frame here instead of each carrying their own copy.
/// </para>
/// </summary>
internal static class FrameBoundary
{
    private static readonly Action EnterAction = Enter;
    private static readonly Action<nint> LeaveAction = Leave;

    /// <summary>
    /// Makes <see cref="FrameHooks"/> call this boundary. A component, mutation or progressive
    /// operation does it on construction, so an app that uses none of them never roots this type.
    /// </summary>
    internal static void Install()
    {
        if (FrameHooks.Current is not null) return;
        FrameHooks.Current = new FrameHooks(EnterAction, LeaveAction);
    }

    /// <summary>
    /// Starts a frame: applies what finished since the last one, then resets the component flags
    /// the frame will set again.
    /// </summary>
    private static void Enter()
    {
        Completions.Drain();
        Component.BeginFrame();
    }

    /// <summary>
    /// Ends the frame that just drew, before the loop sleeps: asks for the next one while work is
    /// in flight or a component is live, then spends the frame a completion was owed.
    /// </summary>
    private static void Leave(nint context)
    {
        if (Component.AnyWantFrames || PendingWork.Due)
        {
            // A finished operation gets an immediate frame, so the value it published is drawn
            // before the window can sleep.
            Native.RequestRedraw(context);
        }
        else if (PendingWork.Any)
        {
            float seconds = PendingWork.NextFrameSeconds;
            if (seconds > 0f) Native.RequestWakeup(context, seconds);
            else Native.RequestRedraw(context);
        }

        PendingWork.MarkDrawn();
    }
}
