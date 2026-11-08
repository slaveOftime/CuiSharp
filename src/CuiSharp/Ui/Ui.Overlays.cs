// Transitional wrappers that stay hand-written until Phase 2, Step 3.
// Charts, texture handles, text-buffer inputs and reorder keep ownership or
// pooling concerns the mechanical mapping cannot express yet.
using CuiSharp.Interop;

namespace CuiSharp;

public unsafe ref partial struct Ui
{
    public (float X, float Y) ViewportSize()
    {
        var v = Native.ViewportSize(Handle);
        return (v.X, v.Y);
    }

    public bool WantsKeyboard => Native.WantsKeyboard(Handle) != 0;
    public bool Shortcut(Mods mods, Key key) => Native.Shortcut(Handle, (int)mods, key) != 0;

    /// <summary>Rectangle of the most recent component.</summary>
    public RectF LastRect
    {
        get
        {
            var r = Native.LastRect(Handle);
            return new RectF(r.X, r.Y, r.W, r.H);
        }
    }

    public void Icon(UiText icon, float size = 16f, Color color = default) =>
        Native.Icon(Handle, Utf8.Get(icon), size,
            UiMarshal.ToNative(color.IsToken || color.A != 0 ? color : Color.Foreground));

    public bool DateToday(out Date value)
    {
        var d = Native.DateToday();
        value = new Date(d.Year, d.Month, d.Day);
        return d.Year != 0;
    }

    /// <summary>Pushes a string id for the widgets drawn inside the scope.</summary>
    public IdScope Id(UiText id)
    {
        Native.PushId(Handle, Utf8.Get(id));
        return new IdScope(Handle);
    }

    /// <summary>Pushes an integer id, typically a row or item index.</summary>
    public IdScope Id(int key)
    {
        Native.PushIdInt(Handle, key);
        return new IdScope(Handle);
    }

    /// <summary>
    /// The id of the widget drawn last, which is what <see cref="TourStep.Target"/> and the
    /// other "point at this" APIs take. Read it immediately after the control.
    /// </summary>
    /// <example><code>
    /// ui.Button("Export");
    /// steps[1].Target = ui.LastId;
    /// </code></example>
    public uint LastId => Native.LastId(Handle);

    /// <summary>Enumerates items, each inside its own id scope derived from <paramref name="key"/>.</summary>
    public KeyedScope<T> Keyed<T>(T[] items, KeyFn<T> key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new KeyedScope<T>(Handle, items, key);
    }

    // cui_reorder_begin keeps the order pointer until reorder_end, which is
    // past the P/Invoke pin. The frame arena copy is stable for the frame and
    // the managed array is rewritten when the list closes.
    public void ReorderBegin(UiText id, int[] order)
    {
        int* storage = Utf8.AllocFrame<int>(Math.Max(1, order.Length));
        for (int i = 0; i < order.Length; i++) storage[i] = order[i];
        _frame.ReorderOrder = order;
        _frame.ReorderStorage = storage;
        Native.ReorderBegin(Handle, Utf8.Get(id), ref *storage, order.Length);
    }

    public LayoutScope ReorderItem(int position)
    {
        Native.ReorderItemBegin(Handle, position);
        return new LayoutScope(Handle, ScopeKind.ReorderItem, "ReorderItem");
    }

    public bool ReorderEnd()
    {
        bool changed = Native.ReorderEnd(Handle) != 0;
        int[]? order = _frame.ReorderOrder;
        int* storage = _frame.ReorderStorage;
        if (order is not null && storage is not null)
            for (int i = 0; i < order.Length; i++) order[i] = storage[i];
        _frame.ReorderOrder = null;
        _frame.ReorderStorage = null;
        return changed;
    }

    /// <summary>Comma-separated tags in one field; enter or comma adds, backspace removes.</summary>
    public bool TagInput(UiText id, ref string value, TagOpts o = default)
    {
        uint key = Native.MakeId(Handle, Utf8.Get(id));
        var slot = _frame.Text.Rent(key, value, 256);
        var n = o.ToNative();
        bool changed = Native.TagInput(Handle, Utf8.Get(id), (nint)slot.Buffer, (nuint)slot.Capacity, in n) != 0;
        if (changed) value = TextFieldPool.Decode(slot);
        return changed;
    }

    /// <summary>One-time code input: <paramref name="length"/> boxes, buffer length + 1.</summary>
    public bool OtpInput(UiText id, ref string value, int length)
    {
        uint key = Native.MakeId(Handle, Utf8.Get(id));
        var slot = _frame.Text.Rent(key, value, length + 1);
        bool changed = Native.OtpInput(Handle, Utf8.Get(id), (nint)slot.Buffer, length) != 0;
        if (changed) value = TextFieldPool.Decode(slot);
        return changed;
    }
}
