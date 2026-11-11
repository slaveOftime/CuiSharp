using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>Persistent selection, sort and keyboard state for one data grid.</summary>
public sealed class GridState
{
    /// <summary>First visible row (inclusive), updated when the grid opens.</summary>
    public int First;
    /// <summary>Last visible row (exclusive), updated when the grid opens.</summary>
    public int Last;
    /// <summary>One selection flag per row. Resize when the model changes, not while drawing.</summary>
    public bool[] Selected;
    /// <summary>The column and direction chosen in the header.</summary>
    public TableSort Sort;
    /// <summary>The keyboard row, or -1.</summary>
    public int Cursor = -1;
    /// <summary>The start of a Shift selection range, or -1.</summary>
    public int Anchor = -1;

    /// <summary>Creates state with selection storage for the given row count.</summary>
    public GridState(int rowCount = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        Selected = new bool[rowCount];
    }
}

/// <summary>A grid scope that publishes row interactions back to its state when closed.</summary>
public unsafe ref struct GridScope : IDisposable
{
    private OpenScope _scope;
    private GridState? _state;
    private readonly bool[] _selected;
    private readonly byte* _selection;
    private readonly NativeTableSort* _sort;
    private readonly int* _cursor;
    private readonly int* _anchor;
    private readonly int _rows;

    internal GridScope(OpenScope scope, GridState state, byte* selection, NativeTableSort* sort,
        int* cursor, int* anchor, int rows)
    {
        _scope = scope;
        _state = state;
        _selected = state.Selected;
        _selection = selection;
        _sort = sort;
        _cursor = cursor;
        _anchor = anchor;
        _rows = rows;
    }

    /// <summary>Whether the grid body should draw.</summary>
    public readonly bool Open => _scope.Open;
    /// <summary>Whether the grid body should draw.</summary>
    public static implicit operator bool(GridScope scope) => scope.Open;

    /// <summary>Closes the grid and writes selection, sort and keyboard state back.</summary>
    public void Dispose()
    {
        if (_state is not { } state) return;
        _state = null;
        _scope.Dispose();
        CopyBack(state, _selected, _selection, _sort, _cursor, _anchor, _rows);
    }

    internal static void CopyBack(GridState state, bool[] selected, byte* selection,
        NativeTableSort* sort, int* cursor, int* anchor, int rows)
    {
        for (int i = 0; i < rows; i++) selected[i] = selection[i] != 0;
        state.Sort = TableSort.FromNative(*sort);
        state.Cursor = *cursor;
        state.Anchor = *anchor;
    }
}

public static unsafe class StatefulDataGridUiExtensions
{
    extension(Ui ui)
    {
        /// <summary>Opens a virtualized grid; draw rows in [state.First, state.Last).</summary>
        public GridScope DataGrid(UiText id, ReadOnlySpan<GridColumn> columns, int rowCount,
            GridState state, GridOpts o = default)
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
            if (state.Selected is null || state.Selected.Length < rowCount)
                throw new ArgumentException("Selection must contain at least one entry per row.", nameof(state));
            var nativeColumns = Utf8.AllocFrame<NativeGridColumn>(columns.Length);
            for (int i = 0; i < columns.Length; i++) nativeColumns[i] = columns[i].ToNative();
            // CUI keeps these pointers until end, so neither stack locals nor movable managed fields work.
            var selection = Utf8.AllocFrame<byte>(Math.Max(1, rowCount));
            for (int i = 0; i < rowCount; i++) selection[i] = (byte)(state.Selected[i] ? 1 : 0);
            var sort = Utf8.AllocFrame<NativeTableSort>(1);
            *sort = state.Sort.ToNative();
            var cursor = Utf8.AllocFrame<int>(1);
            *cursor = state.Cursor;
            var anchor = Utf8.AllocFrame<int>(1);
            *anchor = state.Anchor;
            var opts = o.ToNative((nint)selection, (nint)sort, (nint)cursor, (nint)anchor);
            nint utf8Id = Utf8.Get(id);
            uint nativeId = Native.MakeId(ui.Handle, utf8Id);
            bool open = Native.DataGridBegin(ui.Handle, utf8Id, (nint)nativeColumns, columns.Length,
                rowCount, ref state.First, ref state.Last, in opts) != 0;
            // Header sort, select-all and keyboard navigation run in DataGridBegin, before rows draw.
            GridScope.CopyBack(state, state.Selected, selection, sort, cursor, anchor, rowCount);
            return new GridScope(new OpenScope(ui.Handle, &Native.DataGridEnd, "DataGrid", id, open, nativeId),
                state, selection, sort, cursor, anchor, rowCount);
        }

        /// <summary>Draws the body only when open; closes and publishes state even on exceptions.</summary>
        public void DataGrid(UiText id, ReadOnlySpan<GridColumn> columns, int rowCount,
            GridState state, DrawFn body, GridOpts o = default)
        {
            ArgumentNullException.ThrowIfNull(body);
            using var scope = ui.DataGrid(id, columns, rowCount, state, o);
            if (scope) body(ui);
        }
    }
}
