using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>
/// The filesystem helpers CUI keeps behind the file browser: a directory listing with the
/// same rule the browser uses, and the in-place sorts the flame graph and the tree need.
/// </summary>
public static unsafe class CuiFiles
{
    private const int MaxNameBytes = 64 * 1024;

    /// <summary>
    /// Lists a directory the way the file browser does: no <c>.</c>/<c>..</c>, and hidden
    /// entries only when asked for.
    /// </summary>
    /// <param name="path">The directory; the current one when null or empty.</param>
    /// <param name="includeHidden">Include dot files and entries the OS marks hidden.</param>
    /// <param name="maxEntries">The most entries to return. Default 20 000, the browser's own cap.</param>
    /// <returns>The entries (a generated <see cref="FileEntry"/> each), directories first, then by name.</returns>
    /// <remarks>Returns an empty list when the directory cannot be read.</remarks>
    public static FileEntry[] ListDirectory(string? path, bool includeHidden = false, int maxEntries = 20_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);

        var native = new NativeFileEntry[maxEntries];
        var names = new byte[MaxNameBytes];
        nint pathUtf8 = Utf8.Get(path);
        int count;
        fixed (byte* nameBuffer = names)
        {
            count = Native.FsList(0, pathUtf8, ref native[0], maxEntries, (nint)nameBuffer, (nuint)names.Length, (byte)(includeHidden ? 1 : 0));
        }

        if (count <= 0) return [];

        var result = new FileEntry[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = FileEntry.FromNative(native[i]);
        }

        Array.Sort(result, static (a, b) => a.Dir != b.Dir
            ? (a.Dir ? -1 : 1)
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>Sorts flame graph spans by weight in place, the order the graph expects.</summary>
    public static void SortFlameSpans(Span<FlameSpan> spans)
    {
        if (spans.IsEmpty) return;
        var native = Utf8.AllocFrame<NativeFlameSpan>(spans.Length);
        for (int i = 0; i < spans.Length; i++) native[i] = spans[i].ToNative();
        Native.FlameSort((nint)native, spans.Length);
        for (int i = 0; i < spans.Length; i++) spans[i] = FlameSpan.FromNative(native[i]);
    }
}
