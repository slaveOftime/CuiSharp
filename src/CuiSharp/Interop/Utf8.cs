using System.Buffers.Text;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace CuiSharp;

/// <summary>
/// UTF-8 marshalling with no GC allocation on the steady-state path.
/// <para>
/// The backing store is a <b>frame arena</b>: a list of chunks, bump-allocated
/// and reset at the start of every frame by <see cref="BeginFrame"/>. It holds
/// argument strings and small struct arrays for one frame.
/// </para>
/// <para>
/// Chunks are <b>never moved or reallocated</b>, only added. CUI reads text
/// during the frame's paint phase, after your draw code returns, so a pointer
/// handed over early must still be valid at the end of the frame: a frame that
/// outgrows a chunk takes another, and the chunks are reused next frame.
/// </para>
/// <para>
/// Resetting is safe because CUI never keeps a <c>const char *</c> from its
/// options: every id is reduced to a <c>uint32</c> by <c>cui_make_id</c> during
/// the call, and labels are only read while the call runs.
/// </para>
/// </summary>
internal static unsafe class Utf8
{
    private const int ArenaChunk = 128 * 1024;

    [ThreadStatic] private static IntPtr[]? _chunks;
    [ThreadStatic] private static int[]? _capacities;
    [ThreadStatic] private static int _chunkCount;
    [ThreadStatic] private static int _chunk;
    [ThreadStatic] private static int _used;
    // Identify the top reservation, not just its end address. Handlers are
    // value types: an earlier copy must not reclaim a later allocation's tail.
    [ThreadStatic] private static byte* _lastAllocation;
    [ThreadStatic] private static int _lastAllocationSize;

    /// <summary>Bytes bumped so far this frame, across every chunk.</summary>
    internal static int FrameBytes
    {
        get
        {
            int total = 0;
            for (int i = 0; i < _chunk; i++) total += _capacities![i];
            return total + _used;
        }
    }

    /// <summary>Bytes the arena holds for reuse: the high-water mark of a frame.</summary>
    internal static int Capacity
    {
        get
        {
            int total = 0;
            for (int i = 0; i < _chunkCount; i++) total += _capacities![i];
            return total;
        }
    }

    /// <summary>Frees every chunk. For tests and shutdown.</summary>
    internal static void Reset()
    {
        if (_chunks != null)
        {
            for (int i = 0; i < _chunkCount; i++)
            {
                if (_chunks[i] != IntPtr.Zero) NativeMemory.Free((void*)_chunks[i]);
            }
        }

        _chunks = null;
        _capacities = null;
        _chunkCount = 0;
        _chunk = 0;
        _used = 0;
        _lastAllocation = null;
        _lastAllocationSize = 0;
    }

    /// <summary>Drops everything allocated last frame. Call once per frame, before drawing.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BeginFrame()
    {
        _chunk = 0;
        _used = 0;
        _lastAllocation = null;
        _lastAllocationSize = 0;
        // Delegates CUI called during the frame that just ended are dead now.
        FrameCallbacks.BeginFrame();
    }

    /// <summary>
    /// A block that gives its bytes back on the way out, for a helper that can be called
    /// without a frame: <c>using var scope = Utf8.Scope();</c>. Outside a frame nothing else
    /// resets the arena, so a helper that allocates must not leak into the next call.
    /// </summary>
    internal static FrameScope Scope() => new(_chunk, _used);

    internal readonly ref struct FrameScope(int chunk, int used)
    {
        public void Dispose()
        {
            _chunk = chunk;
            _used = used;
            _lastAllocation = null;
            _lastAllocationSize = 0;
        }
    }

    public static nint Get(UiText text) => text.GetPointer(out _);

    /// <summary>Copies <paramref name="s"/> into the frame arena. Null or empty gives a null pointer.</summary>
    public static nint Get(string? s) => Get(s, out _);

    /// <summary>
    /// Copies <paramref name="s"/> into the frame arena and reports how many
    /// UTF-8 bytes it took, for APIs that want the pointer and the length.
    /// </summary>
    public static nint Get(string? s, out int byteLength)
    {
        byteLength = 0;
        if (string.IsNullOrEmpty(s)) return 0;

        int max = Encoding.UTF8.GetMaxByteCount(s.Length);
        byte* dst = Reserve(max + 1);
        int written = Encoding.UTF8.GetBytes(s, new Span<byte>(dst, max));
        dst[written] = 0;
        // Hand back the worst-case slack so short strings stay cheap.
        _used -= max - written;
        _lastAllocationSize = written + 1;
        byteLength = written;
        return (nint)dst;
    }

    /// <summary>
    /// Copies a compile-time UTF-8 literal (<c>"Save"u8</c>) into the frame
    /// arena: one <c>memcpy</c>, no encoding pass, no allocation.
    /// </summary>
    public static nint Get(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty) return 0;

        byte* dst = Reserve(utf8.Length + 1);
        utf8.CopyTo(new Span<byte>(dst, utf8.Length));
        dst[utf8.Length] = 0;
        return (nint)dst;
    }

    /// <summary>Copies UTF-8 bytes into the arena and returns them as a pointer.</summary>
    public static byte* GetPointer(ReadOnlySpan<byte> utf8) => (byte*)Get(utf8);

    /// <summary>
    /// Bump-allocates a block from the frame arena, for the pointer, struct and
    /// float arrays CUI reads during the call.
    /// </summary>
    public static byte* AllocFrameBytes(int bytes) => Reserve(bytes);

    /// <summary>Bump-allocates room for <paramref name="count"/> items of <typeparamref name="T"/>.</summary>
    public static T* AllocFrame<T>(int count) where T : unmanaged =>
        (T*)Reserve(count * sizeof(T));

    /// <summary>
    /// Bump-allocates a zeroed block, for the rare caller that fills only some
    /// fields of a struct array.
    /// </summary>
    public static byte* BeginFramePointer(int bytes)
    {
        byte* p = Reserve(bytes);
        new Span<byte>(p, bytes).Clear();
        return p;
    }

    /// <summary>
    /// Writes text into a fixed C buffer owned by the caller's record, NUL-terminated and
    /// cut on a character boundary. Unlike <see cref="Get(string?)"/> it targets the
    /// struct's own storage, so nothing points into the frame arena.
    /// </summary>
    public static void WriteFixed(string? text, byte* destination, int capacity)
    {
        if (capacity <= 0) return;
        int limit = capacity - 1;
        if (string.IsNullOrEmpty(text)) { destination[0] = 0; return; }

        // Shrink until the exact byte count fits: a conservative upper bound would
        // throw away most of the buffer for text that is not all three-byte characters.
        ReadOnlySpan<char> chars = text.AsSpan();
        int keep = Math.Min(chars.Length, limit);
        while (keep > 0 && Encoding.UTF8.GetByteCount(chars[..keep]) > limit) keep--;
        // Never cut a surrogate pair in half: the encoder rejects a lone half.
        if (keep < chars.Length && keep > 0 && char.IsHighSurrogate(chars[keep - 1])) keep--;

        int written = keep == 0 ? 0 : Encoding.UTF8.GetBytes(chars[..keep], new Span<byte>(destination, limit));
        destination[written] = 0;
    }

    /// <summary>Reads a NUL-terminated fixed C buffer; null when it is empty.</summary>
    public static string? ReadFixed(byte* source, int capacity)
    {
        int length = 0;
        while (length < capacity && source[length] != 0) length++;
        return length == 0 ? null : Encoding.UTF8.GetString(source, length);
    }

    /// <summary>
    /// Hands unused capacity back to the arena when <paramref name="block"/> is
    /// still the top-most allocation, which is what lets a formatter reserve a
    /// pessimistic upper bound and keep only what it used.
    /// </summary>
    internal static void ReturnTail(byte* block, int capacity, int used)
    {
        if (block != _lastAllocation || capacity != _lastAllocationSize) return;

        int slack = capacity - used - 1;   // keep the NUL terminator
        if (slack > 0)
        {
            _used -= slack;
            _lastAllocationSize = used + 1;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte* Reserve(int bytes)
    {
        while (_chunk < _chunkCount)
        {
            if (_capacities![_chunk] - _used >= bytes)
            {
                byte* at = (byte*)_chunks![_chunk] + _used;
                _used += bytes;
                _lastAllocation = at;
                _lastAllocationSize = bytes;
                return at;
            }

            _chunk++;
            _used = 0;
        }

        AddChunk(bytes);
        byte* fresh = (byte*)_chunks![_chunk] + _used;
        _used += bytes;
        _lastAllocation = fresh;
        _lastAllocationSize = bytes;
        return fresh;
    }

    private static void AddChunk(int bytes)
    {
        int cap = Math.Max(ArenaChunk, bytes);
        byte* mem = (byte*)NativeMemory.Alloc((nuint)cap);

        if (_chunks == null)
        {
            _chunks = new IntPtr[4];
            _capacities = new int[4];
        }
        else if (_chunkCount == _chunks.Length)
        {
            Array.Resize(ref _chunks, _chunkCount * 2);
            Array.Resize(ref _capacities, _chunkCount * 2);
        }

        _chunks[_chunkCount] = (IntPtr)mem;
        _capacities![_chunkCount] = cap;
        _chunkCount++;
        _chunk = _chunkCount - 1;
        _used = 0;
    }

    /// <summary>
    /// Reserves a block for a string built incrementally. Unlike a scratch
    /// buffer this is not reused, because CUI reads text during the frame's
    /// paint phase: two strings sharing a buffer would end up identical.
    /// </summary>
    internal static byte* ReserveString(int bytes) => Reserve(bytes);
}

/// <summary>
/// Builds a NUL-terminated UTF-8 string for a single call with no allocation.
/// <para>
/// <c>ui.Text($"Clicked {clicks} times")</c> formats straight into the frame
/// arena: no <see cref="string"/> is created and numbers never pass through
/// <see cref="object"/>.
/// </para>
/// <para>
/// The initial block is sized from the literal length plus a guess per
/// formatted value. When a value does not fit, the block grows and the partial
/// text is copied, so output is <b>never truncated</b>. Whatever tail is left
/// unused goes back to the arena when <see cref="Ptr"/> is read.
/// </para>
/// </summary>
[InterpolatedStringHandler]
internal unsafe ref struct Utf8Handler
{
    private byte* _start;
    private int _cap;
    private int _len;

    /// <summary>Called by the compiler when the interpolated string is built.</summary>
    public Utf8Handler(int literalLength, int formattedCount)
    {
        // A guess, not a limit: anything that does not fit grows the block.
        _cap = Math.Max(32, literalLength + formattedCount * 24 + 16);
        _start = Utf8.ReserveString(_cap);
        _len = 0;
    }

    internal Utf8Handler(byte* pointer, int length)
    {
        _start = pointer;
        _cap = length + 1;
        _len = length;
    }

    /// <summary>The bytes written so far, without the terminator.</summary>
    public readonly int Length => _len;

    /// <summary>Whether anything was written.</summary>
    public readonly bool IsEmpty => _len == 0;

    // ---- literals and text ----------------------------------------------

    /// <summary>Appends a literal chunk of the interpolated string.</summary>
    public void AppendLiteral(string value) => AppendText(value);

    /// <summary>Appends a string value; null is skipped, matching CUI.</summary>
    public void AppendFormatted(string? value) => AppendText(value);

    /// <summary>Appends characters, utf8-encoding only the non-ASCII ones.</summary>
    public void AppendFormatted(scoped ReadOnlySpan<char> value) => AppendText(value);

    public void AppendFormatted(string? value, int alignment) => AppendAlignedText(value, alignment);

    public void AppendFormatted(scoped ReadOnlySpan<char> value, int alignment) => AppendAlignedText(value, alignment);

    /// <summary>Appends pre-encoded UTF-8.</summary>
    public void AppendFormatted(scoped ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty) return;
        EnsureRoom(value.Length);
        value.CopyTo(new Span<byte>(_start + _len, value.Length));
        _len += value.Length;
    }

    public void AppendFormatted(bool value) => AppendLiteral(value ? "true" : "false");

    public void AppendFormatted(char value)
    {
        if (value <= 0x7F)
        {
            EnsureRoom(1);
            _start[_len++] = (byte)value;
            return;
        }

        Span<char> one = stackalloc char[1];
        one[0] = value;
        AppendText(one);
    }

    // ---- anything UTF-8 formattable, with no boxing ----------------------

    public void AppendFormatted<T>(T value) where T : IUtf8SpanFormattable =>
        AppendFormattable(value, default, 0);

    public void AppendFormatted<T>(T value, string? format) where T : IUtf8SpanFormattable =>
        AppendFormattable(value, format, 0);

    public void AppendFormatted<T>(T value, int alignment) where T : IUtf8SpanFormattable =>
        AppendFormattable(value, default, alignment);

    public void AppendFormatted<T>(T value, int alignment, string? format) where T : IUtf8SpanFormattable =>
        AppendFormattable(value, format, alignment);

    // ---- last resort -----------------------------------------------------

    /// <summary>Fallback for any other type; may allocate through <c>ToString</c>.</summary>
    public void AppendFormatted(object? value) => AppendFormatted(value, 0, null);

    public void AppendFormatted(object? value, string? format) => AppendFormatted(value, 0, format);

    public void AppendFormatted(object? value, int alignment, string? format = null)
    {
        if (value is null)
        {
            AppendAlignedText(default, alignment);
            return;
        }
        AppendAlignedText(value is IFormattable formatted
            ? formatted.ToString(format, CultureInfo.InvariantCulture)
            : value.ToString(), alignment);
    }

    // ---- the result ------------------------------------------------------

    /// <summary>The NUL-terminated result, valid until the arena is reset.</summary>
    public byte* Ptr
    {
        get
        {
            EnsureRoom(0);
            _start[_len] = 0;
            Utf8.ReturnTail(_start, _cap, _len);
            // Appending after reading Ptr must grow rather than overwrite the
            // returned tail. This also makes repeated reads safe.
            _cap = _len + 1;
            return _start;
        }
    }

    // ---- internals -------------------------------------------------------

    private void AppendAlignedText(scoped ReadOnlySpan<char> chars, int alignment)
    {
        // Alignment measures UTF-16 characters, as ordinary C# interpolation
        // does, not encoded bytes (e.g. é occupies one column, two bytes).
        int padding = Math.Max(0, Math.Abs(alignment) - chars.Length);
        if (alignment > 0) AppendSpaces(padding);
        AppendText(chars);
        if (alignment < 0) AppendSpaces(padding);
    }

    private void AppendSpaces(int count)
    {
        EnsureRoom(count);
        new Span<byte>(_start + _len, count).Fill((byte)' ');
        _len += count;
    }

    private void AppendText(scoped ReadOnlySpan<char> chars)
    {
        if (chars.IsEmpty) return;

        // UI labels are overwhelmingly ASCII, where UTF-8 is one byte per char.
        int ascii = 0;
        while (ascii < chars.Length && chars[ascii] <= 0x7F) ascii++;

        if (ascii == chars.Length)
        {
            EnsureRoom(chars.Length);
            var dst = new Span<byte>(_start + _len, chars.Length);
            for (int i = 0; i < chars.Length; i++) dst[i] = (byte)chars[i];
            _len += chars.Length;
            return;
        }

        int max = Encoding.UTF8.GetMaxByteCount(chars.Length);
        EnsureRoom(max);
        _len += Encoding.UTF8.GetBytes(chars, new Span<byte>(_start + _len, max));
    }

    private void AppendFormattable<T>(T value, string? format, int alignment)
        where T : IUtf8SpanFormattable
    {
        int pad = Math.Abs(alignment);

        // Start small and grow: a custom format such as "0.00" is rejected at
        // every size, so the loop stops at 8 KiB and takes the allocating path
        // instead of growing forever.
        for (int size = 32; ; size *= 4)
        {
            EnsureRoom(pad + size);
            var dst = new Span<byte>(_start + _len + pad, size);
            if (value.TryFormat(dst, out int written, format, CultureInfo.InvariantCulture))
            {
                AppendAt(written, alignment);
                return;
            }

            if (size >= 8 * 1024)
            {
                // A custom pattern such as "0.00" is not supported by Utf8Formatter.
                AppendText(value is IFormattable formattable
                    ? formattable.ToString(format, CultureInfo.InvariantCulture)
                    : value.ToString());
                return;
            }
        }
    }

    // The formatted bytes sit at _len + |alignment|; move and pad them into place.
    private void AppendAt(int written, int alignment)
    {
        int pad = Math.Abs(alignment);
        if (pad == 0)
        {
            // Already where it belongs: the common case does no copying at all.
            _len += written;
            return;
        }

        byte* from = _start + _len + pad;

        if (alignment > 0)
        {
            int lead = alignment - written;
            if (lead > 0)
            {
                Buffer.MemoryCopy(from, _start + _len + lead, written, written);
                new Span<byte>(_start + _len, lead).Fill((byte)' ');
                _len += alignment;
                return;
            }

            Buffer.MemoryCopy(from, _start + _len, written, written);
            _len += written;
            return;
        }

        Buffer.MemoryCopy(from, _start + _len, written, written);
        int width = Math.Max(written, pad);
        if (width > written) new Span<byte>(_start + _len + written, width - written).Fill((byte)' ');
        _len += width;
    }

    /// <summary>Makes room for <paramref name="extra"/> more bytes plus the terminator.</summary>
    private void EnsureRoom(int extra)
    {
        int needed = _len + extra + 1;
        if (needed <= _cap) return;

        int cap = Math.Max(32, _cap);
        while (cap < needed) cap = checked(cap * 2);

        byte* grown = Utf8.ReserveString(cap);
        Buffer.MemoryCopy(_start, grown, cap, _len);
        _start = grown;
        _cap = cap;
    }
}
