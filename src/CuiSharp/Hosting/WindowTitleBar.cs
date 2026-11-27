using CuiSharp.Interop;

namespace CuiSharp.Hosting;

// The platform asks after layout, so keep ids rather than copying the previous frame's rectangles
// during draw. Query the layout CUI actually painted, including after a resize. The native
// title-bar hit test cannot see these IDs: only the not-yet-shipped native widget can register them.
internal sealed class WindowTitleBar(CuiWindow window, nint context, bool fixedSize)
{
    internal CuiWindow Window { get; } = window;
    internal bool FixedSize { get; } = fixedSize;
    private readonly List<uint> _dragRegions = [];
    internal uint MaximizeId;
    internal int DragRegionCount => _dragRegions.Count;

    internal void AddDragRegion(uint id) => _dragRegions.Add(id);

    internal void Reset()
    {
        _dragRegions.Clear();
        MaximizeId = 0;
    }

    internal TitleBarHit HitTest(float x, float y)
    {
        if (Contains(MaximizeId, x, y)) return TitleBarHit.Maximize;
        foreach (uint id in _dragRegions)
            if (Contains(id, x, y)) return TitleBarHit.Caption;
        return TitleBarHit.None;
    }

    private bool Contains(uint id, float x, float y)
    {
        if (id == 0) return false;
        var rect = Native.GetRect(context, id);
        return x >= rect.X && x < rect.X + rect.W && y >= rect.Y && y < rect.Y + rect.H;
    }
}
