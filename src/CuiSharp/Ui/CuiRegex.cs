using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>How a pattern matches. CUI's engine ignores case, <c>^</c>/<c>$</c> per line, or both.</summary>
[Flags]
public enum RegexFlags
{
    None = 0,
    IgnoreCase = 1,
    Multiline = 2,
    Singleline = 4,
}

/// <summary>One capture group of a match: byte offsets into the text, and the group's name when it has one.</summary>
/// <param name="Start">The first byte of the group, or -1 when the group took no part in the match.</param>
/// <param name="End">One past the last byte, or -1 when it took no part.</param>
/// <param name="Name">The name in <c>(?&lt;name&gt;…)</c>, or null for a positional group.</param>
public readonly record struct RegexGroup(int Start, int End, string? Name);

/// <summary>One match: its byte range, and one <see cref="RegexGroup"/> per group, index 0 being the whole match.</summary>
public readonly record struct RegexMatch(int Start, int End, RegexGroup[] Groups);

/// <summary>
/// CUI's regular expression engine, usable without a frame: the same backtracking matcher the
/// regex tester draws its highlights with. It supports the common syntax (classes, anchors, groups
/// and the usual quantifiers) and no lookaround or backreferences.
/// </summary>
public static unsafe class CuiRegex
{
    private static readonly int RowBytes = Unsafe.SizeOf<NativeRegexMatchGroupNameArray2>();

    /// <summary>
    /// Finds the first match of <paramref name="pattern"/> in <paramref name="text"/> at or after
    /// <paramref name="from"/> (a byte offset).
    /// </summary>
    /// <param name="pattern">The pattern, in CUI's own syntax.</param>
    /// <param name="text">The text to search.</param>
    /// <param name="match">The match, with its groups.</param>
    /// <param name="error">Why the pattern did not run: it does not compile, or it backtracks too much.</param>
    /// <param name="from">The UTF-8 byte offset to start at; matches never begin inside a character.</param>
    /// <param name="flags">Case and line-break behaviour.</param>
    /// <returns>True when something matched. A false return with a null <paramref name="error"/> is simply no match.</returns>
    public static bool TrySearch(string pattern, string text, out RegexMatch match, out string? error,
        int from = 0, RegexFlags flags = RegexFlags.None)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(text);

        NativeRegexMatch native = default;
        nint errorText = 0;
        int textBytes;
        int status;
        // No frame is implied: give the arena's bytes back before returning.
        using (Utf8.Scope())
        {
            status = Native.RegexSearch(
                Utf8.Get(pattern), (int)flags, Utf8.Get(text, out textBytes), textBytes, from, ref native, (nint)(&errorText));
        }

        error = errorText == 0 ? null : Marshal.PtrToStringUTF8(errorText);
        if (status != 1)
        {
            match = default;
            return false;
        }

        var groups = new RegexGroup[native.Groups + 1];
        byte* names = (byte*)Unsafe.AsPointer(ref native.GroupName);
        for (int g = 0; g <= native.Groups; g++)
        {
            groups[g] = new RegexGroup(native.GroupStart[g], native.GroupEnd[g],
                g == 0 ? null : Utf8.ReadFixed(names + (g * RowBytes), RowBytes));
        }

        match = new RegexMatch(native.Start, native.End, groups);
        return true;
    }

    /// <summary>The whole match's text.</summary>
    /// <param name="text">The text that was searched.</param>
    /// <param name="match">A match of it.</param>
    public static string Slice(string text, RegexMatch match) => Slice(text, match.Start, match.End);

    /// <summary>
    /// One group's text, which is what a caller marking the groups wants. Nothing is encoded again,
    /// so calling it per group over one match costs one substring each.
    /// </summary>
    /// <param name="text">The text that was searched.</param>
    /// <param name="group">A group of a match of it.</param>
    public static string Slice(string text, RegexGroup group) => Slice(text, group.Start, group.End);

    /// <summary>
    /// The text between two UTF-8 byte offsets. The engine reports bytes, so a caller holding
    /// bytes uses them directly; a caller holding a string gets the characters they cover.
    /// </summary>
    /// <param name="text">The text the offsets are into.</param>
    /// <param name="start">The first byte, or -1 for a group that took no part.</param>
    /// <param name="end">One past the last byte.</param>
    public static string Slice(string text, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (start < 0 || end <= start) return "";

        // ASCII is one byte and one char per element, so the offsets are already the indices and
        // there is nothing to walk. This is the common case and the one a loop over groups hits.
        if (Ascii.IsValid(text)) return text[start..Math.Min(end, text.Length)];

        (int first, int last) = CharRange(text, start, end);
        return first < last ? text[first..last] : "";
    }

    /// <summary>The character range two UTF-8 byte offsets cover, never splitting a rune.</summary>
    private static (int First, int Last) CharRange(string text, int start, int end)
    {
        ReadOnlySpan<char> chars = text.AsSpan();
        int bytes = 0, at = 0, first = -1, last = -1;
        while (at < chars.Length)
        {
            if (bytes == start && first < 0) first = at;
            if (bytes >= end) { last = at; break; }
            if (Rune.DecodeFromUtf16(chars[at..], out Rune rune, out int consumed) != OperationStatus.Done)
            {
                bytes += 1;
                at += 1;
                continue;
            }

            bytes += rune.Utf8SequenceLength;
            at += consumed;
        }

        if (first < 0) first = at;
        if (last < 0) last = at;
        return (first, last);
    }
}
