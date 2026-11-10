namespace CuiSharp;

// Shared, allocation-free conversion primitives for generated options/wrappers.
// All arrays are copied into the frame arena: native CUI may read them during
// paint, after the originating span or pin has gone out of scope.
internal static unsafe class UiMarshal
{
    internal static nint Strings(ReadOnlySpan<string> values)
    {
        if (values.IsEmpty) return 0;
        var pointers = Utf8.AllocFrame<nint>(values.Length);
        for (int i = 0; i < values.Length; i++) pointers[i] = Utf8.Get(values[i]);
        return (nint)pointers;
    }

    internal static nint Values<T>(ReadOnlySpan<T> values) where T : unmanaged
    {
        if (values.IsEmpty) return 0;
        T* target = Utf8.AllocFrame<T>(values.Length);
        values.CopyTo(new Span<T>(target, values.Length));
        return (nint)target;
    }

    internal static Interop.NativeSize ToNative(Size value) => new() { Kind = value.Kind, Value = value.Value };
    internal static Interop.NativeColor ToNative(Color value) => new() { R = value.R, G = value.G, B = value.B, A = value.A };
    internal static Interop.NativeDate ToNative(Date value) => new() { Year = value.Year, Month = value.Month, Day = value.Day };
    internal static Interop.NativeRect ToNative(RectF value) => new() { X = value.X, Y = value.Y, W = value.W, H = value.H };
    internal static Date FromNative(Interop.NativeDate value) => new(value.Year, value.Month, value.Day);
    internal static Color FromNative(Interop.NativeColor value) => new(value.R, value.G, value.B, value.A);
    internal static Size FromNative(Interop.NativeSize value) => Size.FromParts(value.Kind, value.Value);
    internal static RectF FromNative(Interop.NativeRect value) => new(value.X, value.Y, value.W, value.H);

    /// <summary>
    /// Writes up to <paramref name="capacity"/> values into a fixed C array, zeroing
    /// the rest: the record owns that storage, so nothing points into the frame arena.
    /// </summary>
    internal static void WriteValues<T>(ReadOnlySpan<T> values, T* destination, int capacity) where T : unmanaged
    {
        new Span<T>(destination, capacity).Clear();
        int written = Math.Min(values.Length, capacity);
        values[..written].CopyTo(new Span<T>(destination, written));
    }

    internal static T[] ReadValues<T>(T* source, int capacity) where T : unmanaged =>
        new Span<T>(source, capacity).ToArray();

    /// <summary>A C <c>bool[N]</c> is a byte per element, so booleans convert one at a time.</summary>
    internal static void WriteBooleans(ReadOnlySpan<bool> values, byte* destination, int capacity)
    {
        new Span<byte>(destination, capacity).Clear();
        for (int i = 0; i < Math.Min(values.Length, capacity); i++) destination[i] = values[i] ? (byte)1 : (byte)0;
    }

    internal static bool[] ReadBooleans(byte* source, int capacity)
    {
        var result = new bool[capacity];
        for (int i = 0; i < capacity; i++) result[i] = source[i] != 0;
        return result;
    }
}
