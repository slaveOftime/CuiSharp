using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>The result of a custom-drawn canvas.</summary>
public readonly struct CanvasResult
{
    /// <summary>The rectangle the canvas was laid out into.</summary>
    public readonly RectF Rect;

    /// <summary>The pointer is over the canvas.</summary>
    public readonly bool Hovered;

    /// <summary>The pointer is pressed on the canvas.</summary>
    public readonly bool Pressed;

    /// <summary>The canvas was clicked this frame.</summary>
    public readonly bool Clicked;

    /// <summary>Pointer position relative to the canvas origin.</summary>
    public readonly float MouseX, MouseY;

    internal CanvasResult(NativeCanvasResult r)
    {
        Rect = new RectF(r.Rect.X, r.Rect.Y, r.Rect.W, r.Rect.H);
        Hovered = r.Hovered != 0;
        Pressed = r.Pressed != 0;
        Clicked = r.Clicked != 0;
        MouseX = r.Mouse.X;
        MouseY = r.Mouse.Y;
    }
}

/// <summary>
/// Immediate-mode drawing plus custom canvases.
/// <para>
/// Everything here draws in <b>points</b> with a top-left origin, using the same
/// anti-aliasing and theme tokens as the built-in components, so a custom chart
/// looks like it belongs. Draw calls made outside a canvas belong to the current
/// container; inside a canvas they are clipped to it.
/// </para>
/// <para>
/// The paint callback is a delegate kept alive by this type and invoked
/// synchronously during the frame, so it can capture state freely.
/// </para>
/// </summary>
public unsafe ref partial struct Ui
{
    // CUI stores the paint callback on the layout node and runs it in the
    // frame's paint phase, which happens on the next cui_app_frame call. So the
    // delegate has to outlive Canvas() itself: it is held in a per-frame list
    // and the slot index is passed through the void* user pointer.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void PaintThunk(nint ui, NativeRect rect, void* user)
    {
        var frame = UiFrameState.Painting;
        int slot = (int)(nint)user;
        if (frame is null || (uint)slot >= (uint)frame.Painters.Count) return;

        var live = Features.Live ? Hosting.LiveSession.ForContext(ui) : null;
        live?.EnterCallback();
        try
        {
            frame.Painters[slot](new Ui(ui, frame), new RectF(rect.X, rect.Y, rect.W, rect.H));
        }
        finally
        {
            live?.ExitCallback();
        }
    }

    /// <summary>
    /// Reserves a box of the given size and lets you paint it.
    /// </summary>
    /// <example>
    /// <code>
    /// var r = ui.Canvas("spark", Size.Percent(100), Size.Px(60), (ui, box) =&gt;
    /// {
    ///     for (int i = 1; i &lt; points.Length; i++)
    ///         ui.DrawLine(box.X + i - 1, box.Y + points[i - 1], box.X + i, box.Y + points[i], 1.5f, Color.Accent);
    /// });
    /// if (r.Hovered) ui.Tooltip($"x = {r.MouseX:0}");
    /// </code>
    /// </example>
    public CanvasResult Canvas(UiText id, Size width, Size height, PaintFn paint)
    {
        ArgumentNullException.ThrowIfNull(paint);

        int slot = _frame.Painters.Count;
        _frame.Painters.Add(paint);

        var result = Native.Canvas(Handle, Utf8.Get(id), new NativeSize { Kind = width.Kind, Value = width.Value }, new NativeSize { Kind = height.Kind, Value = height.Value }, (nint)(delegate* unmanaged[Cdecl]<nint, NativeRect, void*, void>)&PaintThunk, (nint)slot, 0);
        WidgetRecorder.Note(Handle, "Canvas", id);
        return new CanvasResult(result);
    }

    /// <summary>
    /// Persistent, zeroed storage for a widget id, for values that must survive
    /// between frames (animation phases, drag state, hit-test scratch).
    /// </summary>
    public Span<T> State<T>(UiText id) where T : unmanaged
    {
        var slot = (T*)Native.State(Handle, Native.MakeId(Handle, Utf8.Get(id)), (nuint)sizeof(T));
        return new Span<T>(slot, 1);
    }

    /// <summary>
    /// Persistent, zeroed storage for a widget id, with a callback CUI runs when it throws
    /// that id's state away (about five idle seconds, or when the window closes).
    /// </summary>
    /// <remarks>
    /// Use it when the state owns something that needs releasing: a texture, a lease, a
    /// pooled buffer. The block is a header (the registered callback) followed by
    /// <typeparamref name="T"/>, so do not mix it with <see cref="State{T}(UiText)"/> for the
    /// same id, and keep <typeparamref name="T"/> small.
    /// </remarks>
    /// <example><code>
    /// ref var image = ref MemoryMarshal.GetReference(ui.State&lt;int&gt;("wallpaper", () =&gt; Release()));
    /// </code></example>
    public Span<T> State<T>(UiText id, Action onFree) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(onFree);
        uint idHash = Native.MakeId(Handle, Utf8.Get(id));
        nuint size = (nuint)(sizeof(nint) + sizeof(T));
        var block = (byte*)Native.State(Handle, idHash, size);
        nint* header = (nint*)block;
        if (*header == 0)
        {
            // Registered once per id: CUI calls the disposer with this same block.
            *header = FrameCallbacks.RentPersistent("state-on-free:" + idHash.ToString(System.Globalization.CultureInfo.InvariantCulture), onFree);
            Native.StateOnFree(Handle, idHash, FrameCallbacks.StateDisposerPointer);
        }

        return new Span<T>(block + sizeof(nint), 1);
    }

    /// <summary>A stable <c>cui_id</c> for a string, for <c>Animate</c> and <c>State</c> callers.</summary>
    public uint MakeId(UiText id) => Native.MakeId(Handle, Utf8.Get(id));

    /// <summary>Moves a value toward <paramref name="target"/> with a spring, keyed by id.</summary>
    public float Animate(UiText id, float target) => Native.Animate(Handle, MakeId(id), target);

    /// <summary>Moves a value toward <paramref name="target"/> with a spring of the given duration and bounce.</summary>
    public float AnimateSpring(UiText id, float target, float duration, float bounce) =>
        Native.AnimateSpring(Handle, MakeId(id), target, duration, bounce);

    // ---- drawing primitives ---------------------------------------------
    private static NativeColor Nc(Color c) => new() { R = c.R, G = c.G, B = c.B, A = c.A };

    private static NativeCorners Corners(float radius) => new() { Tl = radius, Tr = radius, Br = radius, Bl = radius };

    /// <summary>Fills a rounded rectangle.</summary>
    public void DrawRect(RectF r, Color fill, float radius = 0) =>
        Native.DrawRect(Handle, Nr(r), Corners(radius), Nc(fill));

    /// <summary>Fills a rounded rectangle and strokes its border.</summary>
    public void DrawRect(RectF r, Color fill, Color border, float borderWidth, float radius = 0) =>
        Native.DrawRectBorder(Handle, Nr(r), Corners(radius), Nc(fill), Nc(border), borderWidth);

    /// <summary>Fills a rounded rectangle with a vertical gradient.</summary>
    public void DrawRectGradient(RectF r, Color top, Color bottom, float radius = 0) =>
        Native.DrawRectGradient(Handle, Nr(r), Corners(radius), Nc(top), Nc(bottom));

    /// <summary>A soft drop shadow behind a rounded rectangle.</summary>
    public void DrawShadow(RectF r, Color color, float blur = 12, float spread = 0, float dx = 0, float dy = 2, float radius = 0) =>
        Native.DrawShadow(Handle, Nr(r), Corners(radius), blur, spread, new NativeVec2 { X = dx, Y = dy }, Nc(color));

    /// <summary>A line between two points.</summary>
    public void DrawLine(float x0, float y0, float x1, float y1, float thickness, Color color) =>
        Native.DrawLine(Handle, new NativeVec2 { X = x0, Y = y0 }, new NativeVec2 { X = x1, Y = y1 }, thickness, Nc(color));

    /// <summary>A polyline through the given x/y pairs.</summary>
    public void DrawPolyline(ReadOnlySpan<(float X, float Y)> points, float thickness, Color color, bool closed = false)
    {
        if (points.Length < 2) return;

        var buffer = Utf8.AllocFrame<NativeVec2>(points.Length);
        for (int i = 0; i < points.Length; i++) buffer[i] = new NativeVec2 { X = points[i].X, Y = points[i].Y };
        Native.DrawPolyline(Handle, (nint)buffer, points.Length, thickness, Nc(color), (byte)(closed ? 1 : 0));
    }

    /// <summary>A convex polygon through the given x/y pairs.</summary>
    public void DrawPolygon(ReadOnlySpan<(float X, float Y)> points, Color color)
    {
        if (points.Length < 3) return;

        var buffer = Utf8.AllocFrame<NativeVec2>(points.Length);
        for (int i = 0; i < points.Length; i++) buffer[i] = new NativeVec2 { X = points[i].X, Y = points[i].Y };
        Native.DrawPolygon(Handle, (nint)buffer, points.Length, Nc(color));
    }

    /// <summary>A filled circle.</summary>
    public void DrawCircle(float cx, float cy, float radius, Color color) =>
        Native.DrawCircle(Handle, new NativeVec2 { X = cx, Y = cy }, radius, Nc(color));

    /// <summary>An arc from <paramref name="fromRadians"/> to <paramref name="toRadians"/>.</summary>
    public void DrawArc(float cx, float cy, float radius, float fromRadians, float toRadians, float thickness, Color color) =>
        Native.DrawArc(Handle, new NativeVec2 { X = cx, Y = cy }, radius, fromRadians, toRadians, thickness, Nc(color));

    /// <summary>Draws text outside layout, from a string, UTF-8 bytes, or interpolation.</summary>
    public void DrawText(UiText text, float x, float y, Color color, float size = 12, Font font = Font.Body)
    {
        nint utf8 = text.GetPointer(out int len);
        if (len == 0) return;
        Native.DrawText(Handle, font, size, new NativeVec2 { X = x, Y = y }, Nc(color), utf8, len);
    }

    /// <summary>Draws an icon glyph into a rectangle.</summary>
    public void DrawIcon(UiText icon, RectF r, Color color, float size = 16) =>
        Native.DrawIcon(Handle, Utf8.Get(icon), Nr(r), size, Nc(color));

    /// <summary>
    /// Draws a texture into <paramref name="destination"/>, sampling
    /// <paramref name="uv"/> in 0..1 texture coordinates.
    /// </summary>
    /// <param name="texture">A texture handle from <c>ui.Image</c> or <c>ui.LoadImage</c>.</param>
    /// <param name="destination">Where it lands, in points.</param>
    /// <param name="uv">The part of the texture to sample; the whole texture by default.</param>
    /// <param name="tint">Multiplied into the pixels, so a white texture can take any colour.</param>
    /// <param name="radius">Corner radius, for a rounded or circular crop.</param>
    public void DrawImage(ulong texture, RectF destination, RectF uv, Color tint, float radius = 0) =>
        Native.DrawImage(Handle, (nuint)texture, Nr(destination), Nr(uv), Corners(radius), Nc(tint));

    /// <summary>Draws the whole texture into <paramref name="destination"/>.</summary>
    public void DrawImage(ulong texture, RectF destination, Color tint, float radius = 0) =>
        DrawImage(texture, destination, new RectF(0, 0, 1, 1), tint, radius);

    /// <summary>Clips subsequent drawing to a rectangle. Pair with <see cref="PopClip"/>.</summary>
    public void PushClip(RectF r) => Native.PushClip(Handle, Nr(r));

    /// <summary>Clips subsequent drawing to a rectangle, closing the clip on dispose.</summary>
    /// <example><code>
    /// using (ui.Clip(box)) { ... }   // painted only inside box
    /// </code></example>
    public ClipScope Clip(RectF r)
    {
        Native.PushClip(Handle, Nr(r));
        return new ClipScope(Handle);
    }

    /// <summary>Ends the clip opened by <see cref="PushClip"/>.</summary>
    public void PopClip() => Native.PopClip(Handle);

    private static NativeRect Nr(RectF r) => new() { X = r.X, Y = r.Y, W = r.W, H = r.H };
}

/// <summary>Closes a clip opened by <see cref="Ui.Clip"/>.</summary>
public ref struct ClipScope : IDisposable
{
    private nint _ui;

    internal ClipScope(nint ui) { _ui = ui; }

    /// <summary>Pops the clip. Disposing twice does nothing.</summary>
    public void Dispose()
    {
        nint ui = _ui;
        if (ui == 0) return;
        _ui = 0;
        Native.PopClip(ui);
    }
}
