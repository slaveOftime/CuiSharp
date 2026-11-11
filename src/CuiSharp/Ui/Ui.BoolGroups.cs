// The widgets that edit one value per item: a toggle group, a multi-select
// and a filter bar. Native writes `count` entries through the pointer, so the
// single-value mapping the generator applies everywhere else cannot be used;
// the array crosses as a frame-arena block and is written back whole.
using CuiSharp.Interop;

namespace CuiSharp;

public unsafe ref partial struct Ui
{
    /// <summary>
    /// A row of toggle buttons sharing one bool per label. Independent by default;
    /// <see cref="ToggleGroupOpts.Exclusive"/> makes them behave like radio buttons.
    /// Returns true on the frame an entry flips.
    /// </summary>
    public bool ToggleGroup(UiText id, ReadOnlySpan<string> labels, Span<bool> values, ToggleGroupOpts o = default)
    {
        if (values.Length < labels.Length)
            throw new ArgumentException("values must hold one bool per label.", nameof(values));
        if (o.Icons is { } icons && icons.Length < labels.Length)
            throw new ArgumentException("Icons must contain at least one entry per item.", nameof(o));

        byte* flags = Utf8.AllocFrame<byte>(labels.Length);
        for (int i = 0; i < labels.Length; i++) flags[i] = (byte)(values[i] ? 1 : 0);
        var n = o.ToNative();
        bool changed = Native.ToggleGroup(Handle, Utf8.Get(id), UiMarshal.Strings(labels), labels.Length, (nint)flags, in n) != 0;
        for (int i = 0; i < labels.Length; i++) values[i] = flags[i] != 0;
        // The count travels with the note: cui_toggle_group_ex returns before building anything
        // when it is given none, and nothing on record can tell that call from a built one.
        WidgetRecorder.Note(Handle, "ToggleGroup", id, labels.Length);
        return changed;
    }

    /// <summary>
    /// A searchable list with one checkbox per item and chips for the chosen ones.
    /// Returns true on the frame an entry flips.
    /// </summary>
    public bool MultiSelect(UiText id, ReadOnlySpan<string> items, Span<bool> selected, MultiSelectOpts o = default)
    {
        if (selected.Length < items.Length)
            throw new ArgumentException("selected must hold one bool per item.", nameof(selected));
        if (o.Icons is { } icons && icons.Length < items.Length)
            throw new ArgumentException("Icons must contain at least one entry per item.", nameof(o));

        byte* flags = Utf8.AllocFrame<byte>(items.Length);
        for (int i = 0; i < items.Length; i++) flags[i] = (byte)(selected[i] ? 1 : 0);
        var n = o.ToNative();
        bool changed = Native.MultiSelect(Handle, Utf8.Get(id), UiMarshal.Strings(items), items.Length, (nint)flags, in n) != 0;
        for (int i = 0; i < items.Length; i++) selected[i] = flags[i] != 0;
        WidgetRecorder.Note(Handle, "MultiSelect", id);
        return changed;
    }

    /// <summary>
    /// Filter chips, one dropdown per field plus a search. <paramref name="selected"/>
    /// holds one option bitmask per field; returns true on any change.
    /// </summary>
    public bool FilterBar(UiText id, ReadOnlySpan<FilterField> fields, Span<uint> selected, ref string search, int searchCap = 256)
    {
        if (selected.Length < fields.Length)
            throw new ArgumentException("selected must hold one bitmask per field.", nameof(selected));

        var items = Utf8.AllocFrame<NativeFilterField>(fields.Length);
        for (int i = 0; i < fields.Length; i++) items[i] = fields[i].ToNative();
        uint* masks = Utf8.AllocFrame<uint>(fields.Length);
        for (int i = 0; i < fields.Length; i++) masks[i] = selected[i];
        var slot = RentText(Native.MakeId(Handle, Utf8.Get(id)) ^ 0xa0d7bcf1u, ref search, searchCap);
        bool changed = Native.FilterBar(Handle, Utf8.Get(id), (nint)items, fields.Length, ref *masks,
            (nint)slot.Buffer, (nuint)slot.Capacity) != 0;
        for (int i = 0; i < fields.Length; i++) selected[i] = masks[i];
        if (TextChanged(slot)) search = DecodeText(slot);
        WidgetRecorder.Note(Handle, "FilterBar", id);
        return changed;
    }
}
