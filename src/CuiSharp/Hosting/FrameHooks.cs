namespace CuiSharp.Hosting;

/// <summary>
/// The frame boundary a host calls, installed by the first piece of hosting state that needs it.
/// <para>
/// The drain/wake pair is only meaningful when something can finish on a pool thread. Calling into
/// it unconditionally from <see cref="CuiApp.Run(CuiAppOptions, DrawFn)"/> would root the whole
/// hosting layer — components, mutations, the dispatcher and its queue — in an app that draws a
/// button and nothing else, which is the one promise the size profile makes. A constructor that
/// needs the boundary installs this object; an app that never constructs one finds it null and
/// pays nothing for the code behind it.
/// </para>
/// </summary>
internal sealed class FrameHooks
{
    /// <summary>Installed once by the first component, mutation or progressive run; else null.</summary>
    internal static FrameHooks? Current;

    internal readonly Action Enter;
    internal readonly Action<nint> Leave;

    internal FrameHooks(Action enter, Action<nint> leave)
    {
        Enter = enter;
        Leave = leave;
    }
}
