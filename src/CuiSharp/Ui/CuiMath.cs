using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>
/// The pure helpers behind the editors: evaluating a curve or a gradient the same way
/// the widgets do, and diffing two texts. They take no <see cref="Ui"/>, so they are
/// usable from a component's own code or a unit test.
/// </summary>
public static unsafe class CuiMath
{
    /// <summary>
    /// The value of a bezier curve at <paramref name="x"/>, exactly as the curve editor
    /// draws it: keys sorted by <c>X</c>, with their handles.
    /// </summary>
    public static float EvaluateCurve(ReadOnlySpan<CurveKey> keys, float x)
    {
        if (keys.IsEmpty) return 0;
        var native = stackalloc NativeCurveKey[keys.Length];
        for (int i = 0; i < keys.Length; i++) native[i] = keys[i].ToNative();
        return Native.CurveEval((nint)native, keys.Length, x);
    }

    /// <summary>The colour of a gradient at <paramref name="t"/> (0..1), interpolated in CUI's own space.</summary>
    public static Color EvaluateGradient(ReadOnlySpan<GradientStop> stops, float t)
    {
        if (stops.IsEmpty) return Color.Transparent;
        var native = stackalloc NativeGradientStop[stops.Length];
        for (int i = 0; i < stops.Length; i++) native[i] = stops[i].ToNative();
        return UiMarshal.FromNative(Native.GradientEval((nint)native, stops.Length, t));
    }

    /// <summary>
    /// Marks the byte ranges of one line that changed between two versions of it, as
    /// <see cref="DiffRange"/> lists.
    /// </summary>
    /// <returns>
    /// The changed ranges in the old line and in the new one, or <see langword="null"/>
    /// when the lines have too little in common for marks to help (or one is over 2000 bytes).
    /// </returns>
    public static (DiffRange[] Old, DiffRange[] New)? DiffWords(string? oldLine, string? newLine, int capacity = 64)
    {
        var oldBytes = System.Text.Encoding.UTF8.GetBytes(oldLine ?? "");
        var newBytes = System.Text.Encoding.UTF8.GetBytes(newLine ?? "");
        var oldRanges = stackalloc NativeDiffRange[capacity];
        var newRanges = stackalloc NativeDiffRange[capacity];
        int oldCount = 0, newCount = 0;
        fixed (byte* oldPtr = oldBytes)
        fixed (byte* newPtr = newBytes)
        {
            bool useful = Native.DiffWords((nint)oldPtr, oldBytes.Length, (nint)newPtr, newBytes.Length,
                (nint)oldRanges, ref oldCount, (nint)newRanges, ref newCount, capacity) != 0;
            if (!useful) return null;
        }

        return (Copy(oldRanges, oldCount), Copy(newRanges, newCount));

        static DiffRange[] Copy(NativeDiffRange* ranges, int count)
        {
            var result = new DiffRange[count];
            for (int i = 0; i < count; i++) result[i] = new DiffRange(ranges[i].Start, ranges[i].Length);
            return result;
        }
    }

    /// <summary>
    /// The hunks that turn <paramref name="oldText"/> into <paramref name="newText"/>,
    /// computed the way the diff view does.
    /// </summary>
    /// <param name="oldText">The previous text.</param>
    /// <param name="newText">The new text.</param>
    /// <param name="ignoreLineEndings">Treat CRLF and LF as the same line ending.</param>
    /// <param name="maxHunks">The most hunks to return; the count is reported in the stats.</param>
    /// <returns>The hunks and the statistics; <c>Hunks</c> may be shorter than <c>Stats.Hunks</c> when it did not fit.</returns>
    public static (DiffHunk[] Hunks, DiffStats Stats) DiffLines(string? oldText, string? newText, bool ignoreLineEndings = false, int maxHunks = 512)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxHunks, 1);
        var oldBytes = System.Text.Encoding.UTF8.GetBytes(oldText ?? "");
        var newBytes = System.Text.Encoding.UTF8.GetBytes(newText ?? "");
        var hunks = stackalloc NativeDiffHunk[maxHunks];
        NativeDiffStats stats = default;
        int count;
        fixed (byte* oldPtr = oldBytes)
        fixed (byte* newPtr = newBytes)
        {
            count = Native.DiffCompute((nint)oldPtr, (nuint)oldBytes.Length, (nint)newPtr, (nuint)newBytes.Length,
                ignoreLineEndings ? 1u : 0u, (nint)hunks, maxHunks, (nint)(&stats));
        }

        var result = new DiffHunk[Math.Min(count, maxHunks)];
        for (int i = 0; i < result.Length; i++)
            result[i] = new DiffHunk(hunks[i].OldStart, hunks[i].OldCount, hunks[i].NewStart, hunks[i].NewCount);
        return (result, new DiffStats(stats.Added, stats.Removed, stats.Hunks, stats.OldLines, stats.NewLines, stats.Approximate != 0));
    }

    /// <summary>
    /// True when <paramref name="path"/> matches a filter such as <c>"*.png;*.jpg"</c>, using
    /// CUI's own matching rule (the one the file picker applies).
    /// </summary>
    public static bool PathMatches(string? path, string? filter)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(filter)) return true;
        return Native.PathHasExtension(Utf8.Get(path), Utf8.Get(filter)) != 0;
    }
}

/// <summary>
/// Applies an interactive checkbox toggle to markdown text: flips the <c>[ ]</c>/<c>[x]</c>
/// at the offset a <see cref="MarkdownResult"/> reports.
/// </summary>
public static unsafe class MarkdownText
{
    /// <summary>
    /// Rewrites <paramref name="text"/> with the task box at <paramref name="taskOffset"/>
    /// flipped, keeping every other byte.
    /// </summary>
    /// <returns>False when the offset does not point at a task box.</returns>
    /// <remarks>
    /// <paramref name="taskOffset"/> is what <see cref="MarkdownResult.TaskOffset"/> reports:
    /// the byte offset of the character inside the brackets. Pair with
    /// <c>ui.Markdown(id, text, ref result)</c>.
    /// </remarks>
    public static bool ToggleTask(ref string text, int taskOffset, MarkdownResult result)
    {
        if (string.IsNullOrEmpty(text) || taskOffset < 0) return false;
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var native = result.ToNative();
        fixed (byte* p = bytes)
        {
            if (Native.MarkdownApplyTask((nint)p, in native) == 0) return false;
        }

        text = System.Text.Encoding.UTF8.GetString(bytes);
        return true;
    }
}

/// <summary>One hunk of a line diff: a run of lines replaced between the old and new text.</summary>
public readonly struct DiffHunk
{
    public readonly int OldStart, OldCount, NewStart, NewCount;

    public DiffHunk(int oldStart, int oldCount, int newStart, int newCount)
    {
        OldStart = oldStart;
        OldCount = oldCount;
        NewStart = newStart;
        NewCount = newCount;
    }

    /// <inheritdoc />
    public override string ToString() => $"-{OldStart},{OldCount} +{NewStart},{NewCount}";
}

/// <summary>What a line diff found, including whether it had to approximate.</summary>
public readonly struct DiffStats
{
    public readonly int Added, Removed, Hunks, OldLines, NewLines;

    /// <summary>True when the inputs were too large for an exact diff, so the hunks are approximate.</summary>
    public readonly bool Approximate;

    internal DiffStats(int added, int removed, int hunks, int oldLines, int newLines, bool approximate)
    {
        Added = added;
        Removed = removed;
        Hunks = hunks;
        OldLines = oldLines;
        NewLines = newLines;
        Approximate = approximate;
    }
}

/// <summary>A byte range in a line, for the changed part a diff marks.</summary>
public readonly struct DiffRange
{
    /// <summary>Byte offset in the UTF-8 line.</summary>
    public readonly int Start;

    /// <summary>Length in bytes.</summary>
    public readonly int Length;

    public DiffRange(int start, int length) { Start = start; Length = length; }

    /// <inheritdoc />
    public override string ToString() => $"{Start}+{Length}";
}
