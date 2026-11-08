using System.Runtime.InteropServices;
using System.Text;

namespace CuiSharp;

/// <summary>
/// Unmanaged, NUL-terminated UTF-8 storage that CUI can read and write: the
/// value of an editable field lives here, not in a managed string.
/// <para>
/// One allocation per buffer for the lifetime of the app. Reading costs one
/// decode; <see cref="Utf8"/> exposes it as a span without allocating, and
/// <see cref="Value"/> decodes only when the text actually changed — CuiSharp
/// invalidates the cached string when a widget reports an edit.
/// </para>
/// </summary>
public sealed unsafe class TextBuffer : IDisposable
{
    private byte* _buf;
    private readonly int _cap;
    private string? _value;

    /// <summary>Allocates a zeroed buffer of <paramref name="capacity"/> bytes.</summary>
    public TextBuffer(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        _cap = capacity;
        _buf = (byte*)NativeMemory.AllocZeroed((nuint)capacity);
    }

    internal byte* Ptr => _buf;

    internal nuint Capacity => (nuint)_cap;

    /// <summary>Length of the current value in bytes, excluding the terminator.</summary>
    public int ByteLength => _buf == null ? 0 : Utf8.Length;

    /// <summary>The current value as UTF-8 bytes, without copying.</summary>
    public ReadOnlySpan<byte> Utf8
    {
        get
        {
            if (_buf == null) return default;

            int len = 0;
            while (_buf[len] != 0) len++;
            return new ReadOnlySpan<byte>(_buf, len);
        }
    }

    /// <summary>
    /// The current value as a string. Cached until the buffer changes, so it is
    /// free to read every frame; the first read after an edit allocates.
    /// </summary>
    public string Value
    {
        get
        {
            ObjectDisposedException.ThrowIf(_buf == null, this);
            return _value ??= Decode();
        }
    }

    /// <summary>Writes <paramref name="s"/> into the buffer. Throws when it does not fit.</summary>
    public void Set(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        ObjectDisposedException.ThrowIf(_buf == null, this);

        int written = Encoding.UTF8.GetBytes(s, new Span<byte>(_buf, _cap - 1));
        _buf[written] = 0;
        _value = s;
    }

    /// <summary>Writes UTF-8 bytes into the buffer. Throws when they do not fit.</summary>
    public void Set(ReadOnlySpan<byte> utf8)
    {
        ObjectDisposedException.ThrowIf(_buf == null, this);

        if (utf8.Length > _cap - 1)
        {
            throw new ArgumentException($"value needs {utf8.Length + 1} bytes, buffer holds {_cap}", nameof(utf8));
        }

        utf8.CopyTo(new Span<byte>(_buf, utf8.Length));
        _buf[utf8.Length] = 0;
        _value = null;
    }

    /// <summary>True when the value equals <paramref name="utf8"/> without allocating.</summary>
    public bool ValueIs(ReadOnlySpan<byte> utf8) => Utf8.SequenceEqual(utf8);

    /// <summary>
    /// Drops the cached string. Called by CuiSharp when CUI edited the buffer,
    /// since a native edit cannot be seen from managed code.
    /// </summary>
    internal void Invalidate() => _value = null;

    private string Decode()
    {
        var span = Utf8;
        return span.IsEmpty ? string.Empty : Encoding.UTF8.GetString(span);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_buf == null) return;
        NativeMemory.Free(_buf);
        _buf = null;
        _value = null;
    }
}
