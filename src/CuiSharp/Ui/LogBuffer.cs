using System.Runtime.InteropServices;
using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>
/// A log's ring buffer and line index, owned by managed code.
/// <para>
/// CUI keeps the text in storage the app supplies, so this type owns it: an unmanaged
/// text block plus the line ring behind it. Append with <c>Append</c> or
/// <c>AppendLine</c> and draw it with <c>ui.LogView(id, log, opts)</c>, which adds the level
/// filters, search, follow and copy toolbar.
/// </para>
/// </summary>
/// <example>
/// <code>
/// var log = new LogBuffer();
/// log.AppendLine(LogLevel.Warn, "retrying in 2 s");
/// ui.LogView("log", log, new LogViewOpts { Height = 220, Timestamps = true });
/// </code>
/// </example>
public sealed unsafe class LogBuffer : IDisposable
{
    private NativeLogBuffer _buffer;
    private NativeLogLine* _lines;
    private byte* _text;

    /// <summary>Creates a log that keeps <paramref name="maxLines"/> lines of history.</summary>
    /// <param name="maxLines">Lines kept before the oldest scroll away. Default 500.</param>
    /// <param name="maxChars">Bytes kept before the oldest lines make way. Default 64 KB.</param>
    public LogBuffer(int maxLines = 500, int maxChars = 64 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLines, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 64);
        _text = (byte*)NativeMemory.Alloc((nuint)maxChars);
        _lines = (NativeLogLine*)NativeMemory.Alloc((nuint)maxLines, (nuint)sizeof(NativeLogLine));
        Native.LogInit(ref _buffer, (nint)_lines, maxLines, (nint)_text, (nuint)maxChars);
    }

    /// <summary>Lines currently in the buffer.</summary>
    public int LineCount => _text == null ? 0 : _buffer.Count;

    /// <summary>Lines ever appended, so the buffer can tell "empty" from "scrolled".</summary>
    public ulong TotalLines => _text == null ? 0 : _buffer.Total;

    /// <summary>The timestamp the next line is stamped with, in seconds since the app started.</summary>
    public double Now
    {
        get => _buffer.Now;
        set => _buffer.Now = value;
    }

    /// <summary>Appends one line at the buffer's own clock (<see cref="Now"/>).</summary>
    public void AppendLine(string? text, LogLevel level = LogLevel.Info) => Append(text, level, _buffer.Now);

    /// <summary>Appends one line stamped with <paramref name="time"/>, in seconds.</summary>
    public void Append(string? text, LogLevel level, double time)
    {
        ObjectDisposedException.ThrowIf(_text == null, this);
        if (string.IsNullOrEmpty(text)) return;
        int length = System.Text.Encoding.UTF8.GetByteCount(text);
        byte[] bytes = new byte[length];
        System.Text.Encoding.UTF8.GetBytes(text, bytes);
        fixed (byte* p = bytes) Native.LogAppendText(ref _buffer, level, time, (nint)p, length);
    }

    /// <summary>Everything from the oldest line on, as text, with <c>\n</c> between lines.</summary>
    /// <remarks>Allocates: meant for saving a log or copying it out, not for the frame loop.</remarks>
    public string Text()
    {
        if (_text == null || _buffer.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _buffer.Count; i++)
        {
            nint lineText = 0;
            if (Native.LogGet(in _buffer, i, (nint)(&lineText)) == 0) continue;
            if (i > 0) sb.Append('\n');
            sb.Append(Marshal.PtrToStringUTF8(lineText));
        }

        return sb.ToString();
    }

    /// <summary>Forgets every line.</summary>
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_text == null, this);
        Native.LogClear(ref _buffer);
    }

    /// <summary>Draws the log and its toolbar; <paramref name="o"/> styles it.</summary>
    internal void Draw(nint ui, nint id, in NativeLogViewOpts o)
    {
        ObjectDisposedException.ThrowIf(_text == null, this);
        Native.LogView(ui, id, ref _buffer, in o);
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
