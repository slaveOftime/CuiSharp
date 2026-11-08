using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>
/// A terminal's scrollback and its line index, owned by managed code.
/// <para>
/// CUI keeps the text in a buffer the app supplies, so this type owns that storage and
/// keeps it valid: an unmanaged text block plus the line ring behind it. Write with
/// <see cref="Write(ReadOnlySpan{byte})"/>, <see cref="Write(string?)"/> or an
/// interpolated string, then draw it with <c>ui.Terminal(id, terminal, opts)</c>.
/// </para>
/// <para>
/// The ring recycles the oldest lines when it fills, so a long-running log never has
/// to grow; <see cref="Clear"/> empties it.
/// </para>
/// </summary>
/// <example>
/// <code>
/// var log = new Terminal();
/// log.WriteLine("$ dotnet build");
/// var result = ui.Terminal("log", log, new TerminalOpts { Height = 240 });
/// if (result.Command is { Length: > 0 } command) log.WriteLine("$ " + command);
/// </code>
/// </example>
public sealed unsafe class Terminal : IDisposable
{
    private NativeTerminalBuffer _buffer;
    private NativeTerminalLine* _lines;
    private byte* _text;

    /// <summary>Creates a terminal with a fixed scrollback size.</summary>
    /// <param name="maxLines">Lines kept before the oldest scroll away. Default 500.</param>
    /// <param name="maxChars">Bytes kept before the oldest lines make way. Default 64 KB.</param>
    public Terminal(int maxLines = 500, int maxChars = 64 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLines, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 64);
        _text = (byte*)NativeMemory.Alloc((nuint)maxChars);
        _lines = (NativeTerminalLine*)NativeMemory.Alloc((nuint)maxLines, (nuint)sizeof(NativeTerminalLine));
        Native.TerminalInit(ref _buffer, (nint)_text, (nuint)maxChars, (nint)_lines, maxLines);
    }

    /// <summary>Appends UTF-8 text, interpreting \n, \r, \t, \b and ANSI SGR colours.</summary>
    public void Write(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty) return;
        ObjectDisposedException.ThrowIf(_text == null, this);
        fixed (byte* p = utf8) Native.TerminalWrite(ref _buffer, (nint)p, utf8.Length);
    }

    /// <summary>Appends text, interpreting \n, \r, \t, \b and ANSI SGR colours.</summary>
    public void Write(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        Write(System.Text.Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Appends one line and starts a new one.</summary>
    public void WriteLine(string? text)
    {
        Write(text);
        Write([(byte)'\n']);
    }

    /// <summary>Empties the scrollback, leaving one empty line.</summary>
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_text == null, this);
        Native.TerminalClear(ref _buffer);
    }

    /// <summary>Lines currently in the scrollback, including the one being written.</summary>
    public int LineCount => _text == null ? 0 : _buffer.Count;

    /// <summary>Lines ever started, so a caller can tell an empty terminal from a scrolled one.</summary>
    public ulong TotalLines => _text == null ? 0 : _buffer.Total;

    /// <summary>Draws the terminal and reports what the user typed or interrupted.</summary>
    /// <remarks>
    /// The output is the line press Enter accepted, already echoed into the scrollback;
    /// <see langword="null"/> when nothing was entered this frame.
    /// </remarks>
    internal TerminalResult Draw(nint ui, nint id, in NativeTerminalOpts opts)
    {
        ObjectDisposedException.ThrowIf(_text == null, this);
        return TerminalResult.FromNative(Native.Terminal(ui, id, ref _buffer, in opts));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        ObjectDisposedException.ThrowIf(_text == null, this);
        NativeMemory.Free(_text);
        NativeMemory.Free(_lines);
        _text = null;
        _lines = null;
        _buffer = default;
    }
}
