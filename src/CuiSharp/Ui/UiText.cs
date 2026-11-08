using System.Runtime.CompilerServices;

namespace CuiSharp;

/// <summary>
/// Frame-local text: a string, UTF-8 bytes, or interpolation. All UI text
/// parameters accept this type; interpolated values format directly into the
/// frame arena without creating a managed string.
/// </summary>
/// <remarks>Use only in the frame where it is created. UTF-8 input is copied, not retained.</remarks>
[InterpolatedStringHandler]
public unsafe ref struct UiText
{
    private Utf8Handler _handler;

    /// <summary>Called by the compiler for interpolated text.</summary>
    public UiText(int literalLength, int formattedCount) =>
        _handler = new Utf8Handler(literalLength, formattedCount);

    private UiText(nint pointer, int byteLength) =>
        _handler = new Utf8Handler((byte*)pointer, byteLength);

    /// <summary>Copies a managed string into the frame arena; null means empty.</summary>
    public static implicit operator UiText(string? text)
    {
        nint pointer = Utf8.Get(text, out int length);
        return new UiText(pointer, length);
    }

    /// <summary>Copies pre-encoded UTF-8 into the frame arena.</summary>
    public static implicit operator UiText(ReadOnlySpan<byte> text) => new(Utf8.Get(text), text.Length);

    public void AppendLiteral(string value) => _handler.AppendLiteral(value);
    public void AppendFormatted(string? value) => _handler.AppendFormatted(value);
    public void AppendFormatted(scoped ReadOnlySpan<char> value) => _handler.AppendFormatted(value);
    public void AppendFormatted(scoped ReadOnlySpan<byte> value) => _handler.AppendFormatted(value);
    public void AppendFormatted(string? value, int alignment) => _handler.AppendFormatted(value, alignment);
    public void AppendFormatted(scoped ReadOnlySpan<char> value, int alignment) => _handler.AppendFormatted(value, alignment);
    public void AppendFormatted(UiText value)
    {
        nint pointer = value.GetPointer(out int length);
        _handler.AppendFormatted(new ReadOnlySpan<byte>((void*)pointer, length));
    }
    public void AppendFormatted(bool value) => _handler.AppendFormatted(value);
    public void AppendFormatted(char value) => _handler.AppendFormatted(value);
    public void AppendFormatted<T>(T value) where T : IUtf8SpanFormattable => _handler.AppendFormatted(value);
    public void AppendFormatted<T>(T value, string? format) where T : IUtf8SpanFormattable => _handler.AppendFormatted(value, format);
    public void AppendFormatted<T>(T value, int alignment) where T : IUtf8SpanFormattable => _handler.AppendFormatted(value, alignment);
    public void AppendFormatted<T>(T value, int alignment, string? format) where T : IUtf8SpanFormattable => _handler.AppendFormatted(value, alignment, format);

    /// <summary>Fallback for other values; their <c>ToString()</c> may allocate.</summary>
    public void AppendFormatted(object? value) => _handler.AppendFormatted(value);
    public void AppendFormatted(object? value, string? format) => _handler.AppendFormatted(value, format);
    public void AppendFormatted(object? value, int alignment, string? format = null) => _handler.AppendFormatted(value, alignment, format);

    internal nint GetPointer(out int byteLength)
    {
        byteLength = _handler.Length;
        return _handler.IsEmpty ? 0 : (nint)_handler.Ptr;
    }
}
