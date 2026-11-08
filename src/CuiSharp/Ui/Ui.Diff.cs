using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>
/// Reading a diff view's rows: the same rows the widget draws, for a custom renderer or a
/// test that asserts on the diff.
/// </summary>
public static unsafe class DiffUiExtensions
{
    extension(Ui ui)
    {
        /// <summary>
        /// Copies up to <paramref name="rows"/>.Length of the diff view's rows into
        /// <paramref name="rows"/>, starting at row <paramref name="first"/>.
        /// </summary>
        /// <returns>The total number of rows the view has, which may exceed the span.</returns>
        /// <remarks>
        /// Call it after <c>ui.DiffView(id, ...)</c> in the same frame: the rows belong to the
        /// view, so they are only valid while the frame is being built. Nothing is allocated.
        /// </remarks>
        public int DiffRows(UiText id, Span<DiffRow> rows, int first = 0)
        {
            if (rows.IsEmpty) return 0;
            var native = stackalloc NativeDiffRow[rows.Length];
            int total = Native.DiffViewRows(ui.Handle, Utf8.Get(id), (nint)native, first, rows.Length);
            int copy = Math.Min(total, rows.Length);
            for (int i = 0; i < copy; i++) rows[i] = DiffRow.FromNative(native[i]);
            return total;
        }
    }
}
