using System.Runtime.InteropServices;
using System.Text;
using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>
/// A filter built by the query builder, usable without a frame: the sentence it reads as, the
/// operators a field type offers, and whether a record matches. The builder edits
/// <see cref="QueryNode"/> values the app owns, so these take the same two arrays it draws from.
/// </summary>
public static unsafe class CuiQuery
{
    /// <summary>The name of one operator of a field type, e.g. "contains"; null for none.</summary>
    public static string? OperatorName(QueryType type, int op) =>
        Marshal.PtrToStringUTF8(Native.QueryOpName(type, op));

    /// <summary>How many operators a field type offers, which is what the picker lists.</summary>
    public static int OperatorCount(QueryType type)
    {
        int count = 0;
        while (Native.QueryOpName(type, count) != 0) count++;
        return count;
    }

    /// <summary>
    /// The nodes as one sentence, the way the builder reads them back: "Status is open and
    /// priority is high".
    /// </summary>
    /// <param name="fields">The fields the rules refer to by index.</param>
    /// <param name="nodes">The nodes in tree order, each with its depth.</param>
    /// <param name="capacity">The longest sentence to produce; a longer one is cut.</param>
    public static string Text(ReadOnlySpan<QueryField> fields, ReadOnlySpan<QueryNode> nodes, int capacity = 1024)
    {
        if (nodes.IsEmpty) return "";
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        // Called outside a frame, so the bytes it borrows are given back before it returns.
        using var scope = Utf8.Scope();
        var __fields = Utf8.AllocFrame<NativeQueryField>(fields.Length);
        for (int i = 0; i < fields.Length; i++) __fields[i] = fields[i].ToNative();
        var __nodes = Utf8.AllocFrame<NativeQueryNode>(nodes.Length);
        for (int i = 0; i < nodes.Length; i++) __nodes[i] = nodes[i].ToNative();
        byte* text = Utf8.AllocFrameBytes(capacity);

        ulong written = Native.QueryText((nint)__fields, fields.Length, (nint)__nodes, nodes.Length, (nint)text, (ulong)capacity);
        return Encoding.UTF8.GetString(text, (int)Math.Min(written, (ulong)capacity - 1));
    }

    /// <summary>
    /// Whether a record satisfies the nodes; <paramref name="values"/> is its value for each field,
    /// as text ("1", "true", "open"), so the caller decides how a record becomes text.
    /// </summary>
    public static bool Match(ReadOnlySpan<QueryField> fields, ReadOnlySpan<QueryNode> nodes, ReadOnlySpan<string> values)
    {
        if (nodes.IsEmpty) return true;

        using var scope = Utf8.Scope();
        var __fields = Utf8.AllocFrame<NativeQueryField>(fields.Length);
        for (int i = 0; i < fields.Length; i++) __fields[i] = fields[i].ToNative();
        var __nodes = Utf8.AllocFrame<NativeQueryNode>(nodes.Length);
        for (int i = 0; i < nodes.Length; i++) __nodes[i] = nodes[i].ToNative();

        return Native.QueryMatch((nint)__fields, fields.Length, (nint)__nodes, nodes.Length, UiMarshal.Strings(values)) != 0;
    }
}
