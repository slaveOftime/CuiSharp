using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>
/// A toggle on a canvas controls rail: an icon, the tooltip that names it, and whether it is on.
/// The rail flips <see cref="On"/> on a click and leaves the state to the caller.
/// </summary>
public struct CanvasToggle
{
    /// <summary>A toggle showing <paramref name="icon"/>, named by <paramref name="label"/> in its tooltip.</summary>
    public CanvasToggle(string icon, string label)
    {
        Icon = icon;
        Label = label;
        On = false;
    }

    /// <summary>The glyph.</summary>
    public string Icon { get; init; }

    /// <summary>The tooltip, and the name a test finds the toggle by.</summary>
    public string Label { get; init; }

    /// <summary>Shown pressed while true, and flipped when the toggle is clicked.</summary>
    public bool On { get; set; }
}

public unsafe ref partial struct Ui
{
    /// <summary>
    /// A rail of zoom presets, a fit button and the caller's own toggles, over the previous
    /// component. Returns the CUI_CANVAS_* flags for what was asked for.
    /// </summary>
    /// <param name="id">The widget id, which is also the identity of its hover and keyboard state.</param>
    /// <param name="view">The canvas pan and zoom, written back when the rail moves it.</param>
    /// <param name="toggles">The rail's own toggles, in order; the flipped state comes back in the span.</param>
    /// <param name="o">The zoom presets, the content's bounds and which side the rail sits on.</param>
    /// <remarks>
    /// Hand-written because each toggle carries a <c>bool *</c> of its own: a value record cannot own
    /// a pointer per item, so the flags live in the frame arena for the call and are copied back into
    /// the span afterwards.
    /// </remarks>
    public CanvasEvents CanvasControls(UiText id, ref NodeView view, scoped Span<CanvasToggle> toggles, CanvasControlsOpts o = default)
    {
        int count = toggles.Length;
        byte* flags = count == 0 ? null : Utf8.AllocFrameBytes(count);
        NativeCanvasToggle* native = count == 0 ? null : Utf8.AllocFrame<NativeCanvasToggle>(count);
        for (int i = 0; i < count; i++)
        {
            CanvasToggle toggle = toggles[i];
            flags[i] = (byte)(toggle.On ? 1 : 0);
            native[i] = new NativeCanvasToggle
            {
                Icon = Utf8.Get(toggle.Icon),
                Label = Utf8.Get(toggle.Label),
                On = (nint)(flags + i),
            };
        }

        var opts = o.ToNative();
        opts.Toggles = (nint)native;
        opts.ToggleCount = count;

        var handle = view.ToNative();
        int result = Native.CanvasControls(Handle, Utf8.Get(id), ref handle, in opts);
        view = NodeView.FromNative(handle);

        for (int i = 0; i < count; i++)
        {
            CanvasToggle toggle = toggles[i];
            toggle.On = flags[i] != 0;
            toggles[i] = toggle;
        }

        WidgetRecorder.Note(Handle, "CanvasControls", id);
        return (CanvasEvents)result;
    }

    /// <summary>
    /// A joined row of color swatches that can be locked, with a shuffle button that recolors the
    /// unlocked ones. Returns the CUI_WELL_* flags for what changed.
    /// </summary>
    /// <param name="label">The row's label; a label starting with "##" shows no label row.</param>
    /// <param name="seed">The shuffle state: shuffling moves it on, so the same seed gives the same colors.</param>
    /// <param name="colors">The swatches; a shuffle writes the colors it picked back into the span.</param>
    /// <param name="locked">One flag per swatch, flipped by a click and copied back.</param>
    /// <param name="o">The row's height, value text and whether it has a shuffle button.</param>
    /// <remarks>
    /// Both buffers describe the same swatches and must have equal lengths. CUI displays up to 16.
    /// Hand-written because both arrays are edited in place and share one native count.
    /// </remarks>
    public WellEvents PaletteStrip(UiText label, ref uint seed, scoped Span<Color> colors, scoped Span<bool> locked, PaletteStripOpts o = default)
    {
        uint nativeSeed = seed;
        WellEvents result = PaletteStripCore(label, &nativeSeed, colors, locked, o);
        seed = nativeSeed;
        return result;
    }

    /// <summary>
    /// A palette strip without shuffle state: the shuffle button only reports that it was pressed.
    /// </summary>
    /// <param name="label">The row's label; a label starting with "##" shows no label row.</param>
    /// <param name="colors">The swatches. Without a seed, shuffle leaves them unchanged.</param>
    /// <param name="locked">One flag per swatch, flipped by a click and copied back.</param>
    /// <param name="o">The row's height, value text and whether it has a shuffle button.</param>
    public WellEvents PaletteStrip(UiText label, scoped Span<Color> colors, scoped Span<bool> locked, PaletteStripOpts o = default) =>
        PaletteStripCore(label, null, colors, locked, o);

    private WellEvents PaletteStripCore(UiText label, uint* seed, scoped Span<Color> colors, scoped Span<bool> locked, PaletteStripOpts o)
    {
        if (locked.Length != colors.Length)
            throw new ArgumentException("One lock flag is required per color.", nameof(locked));

        int count = colors.Length;
        NativeColor* nativeColors = (NativeColor*)UiMarshal.Colors(colors);
        byte* flags = count == 0 ? null : Utf8.AllocFrameBytes(count);
        for (int i = 0; i < count; i++) flags[i] = (byte)(locked[i] ? 1 : 0);

        var opts = o.ToNative((nint)seed);
        int result = Native.PaletteStrip(Handle, Utf8.Get(label), (nint)nativeColors, (nint)flags, count, in opts);
        UiMarshal.ReadColors(nativeColors, colors);
        for (int i = 0; i < count; i++) locked[i] = flags[i] != 0;

        WidgetRecorder.Note(Handle, "PaletteStrip", label);
        return (WellEvents)result;
    }

    /// <summary>
    /// A bar that says how many items are selected, with the actions that apply to them, over the
    /// component it belongs to. Returns the action index, -1 when nothing happened, or -2 when
    /// the clear button or Esc cleared the selection.
    /// </summary>
    /// <param name="id">The widget id, which is also the identity of its hover and keyboard state.</param>
    /// <param name="selected">One flag per item; the clear button and Esc clear them, and the loop copies them back.</param>
    /// <param name="actions">The actions to offer; CUI_TONE_DANGER marks a destructive one.</param>
    /// <param name="o">The count format, the component it sits under and whether Esc clears.</param>
    /// <remarks>
    /// Hand-written because <c>selected</c> is a <c>bool *</c> per item that native clears itself: the
    /// span is the caller's storage, so the wrapper passes it and copies the cleared flags back.
    /// </remarks>
    public int SelectionBar(UiText id, scoped Span<bool> selected, scoped ReadOnlySpan<SelectionAction> actions, SelectionBarOpts o = default)
    {
        int itemCount = selected.Length;
        byte* flags = itemCount == 0 ? null : Utf8.AllocFrameBytes(itemCount);
        for (int i = 0; i < itemCount; i++) flags[i] = (byte)(selected[i] ? 1 : 0);

        NativeSelectionAction* nativeActions = actions.IsEmpty
            ? null
            : Utf8.AllocFrame<NativeSelectionAction>(actions.Length);
        for (int i = 0; i < actions.Length; i++) nativeActions[i] = actions[i].ToNative();

        var opts = o.ToNative();
        opts.Selected = (nint)flags;
        opts.ItemCount = itemCount;

        int result = Native.SelectionBar(Handle, Utf8.Get(id), itemCount, (nint)nativeActions, actions.Length, in opts);

        for (int i = 0; i < itemCount; i++) selected[i] = flags[i] != 0;

        WidgetRecorder.Note(Handle, "SelectionBar", id);
        return result;
    }
}
