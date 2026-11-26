using CuiSharp.Interop;

namespace CuiSharp.Hosting;

// The platform asks after layout, so keep ids rather than copying the previous frame's rectangles
// during draw. CUI can then answer with the same geometry it painted, including after a resize.
internal sealed class WindowTitleBar(CuiWindow window, nint context, bool fixedSize)
{
    internal CuiWindow Window { get; } = window;
    internal bool FixedSize { get; } = fixedSize;
    internal uint CaptionId;
    internal uint MaximizeId;

    internal TitleBarHit HitTest(float x, float y)
    {
        if (Contains(MaximizeId, x, y)) return TitleBarHit.Maximize;
        return Contains(CaptionId, x, y) ? TitleBarHit.Caption : TitleBarHit.None;
    }

    private bool Contains(uint id, float x, float y)
    {
        if (id == 0) return false;
        var rect = Native.GetRect(context, id);
        return x >= rect.X && x < rect.X + rect.W && y >= rect.Y && y < rect.Y + rect.H;
    }
}
