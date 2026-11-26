using System.Runtime.CompilerServices;
using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>
/// One node of a <c>ui.TreeTable</c> model: a flat array linked by
/// index, so a hundred thousand nodes cost what one screenful does.
/// </summary>
/// <remarks>
/// The tree table reads and writes this array in place: it toggles
/// <see cref="TreeNodeFlags.Open"/> and <see cref="TreeNodeFlags.Selected"/>, sets
/// <see cref="TreeNodeFlags.Loading"/> when it wants a node's children, and
/// <see cref="CuiTree.Sort"/> relinks the indices. Keep it in a field, not in a per-frame array.
/// </remarks>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct TreeNode
{
    /// <summary>The parent's index, or -1 for a top level node.</summary>
    public int Parent;

    /// <summary>The first child's index, or -1 when it has none or none are loaded yet.</summary>
    public int FirstChild;

    /// <summary>The next sibling's index, or -1 for the last one.</summary>
    public int NextSibling;

    /// <summary>Open, selected and loading state, plus whether children are pending.</summary>
    public TreeNodeFlags Flags;

    /// <summary>A detached node: no parent, no children, no state.</summary>
    public static TreeNode Detached => new() { Parent = -1, FirstChild = -1, NextSibling = -1 };

    /// <summary>The node is expanded.</summary>
    public readonly bool IsOpen => (Flags & TreeNodeFlags.Open) != 0;

    /// <summary>The node is selected.</summary>
    public readonly bool IsSelected => (Flags & TreeNodeFlags.Selected) != 0;

    /// <summary>The table asked for this node's children and they have not arrived yet.</summary>
    public readonly bool IsLoading => (Flags & TreeNodeFlags.Loading) != 0;

    /// <inheritdoc />
    public override readonly string ToString() => $"parent {Parent}, child {FirstChild}, next {NextSibling}, {Flags}";

    internal readonly NativeTreeNode ToNative() => new()
    {
        Parent = Parent,
        FirstChild = FirstChild,
        NextSibling = NextSibling,
        Flags = (uint)Flags,
    };

    internal static TreeNode FromNative(NativeTreeNode n) => new()
    {
        Parent = n.Parent,
        FirstChild = n.FirstChild,
        NextSibling = n.NextSibling,
        Flags = (TreeNodeFlags)n.Flags,
    };
}

/// <summary>cui_tree_node flags: what the tree table toggles as you use it.</summary>
[Flags]
public enum TreeNodeFlags : uint
{
    /// <summary>Show a chevron before the children are loaded.</summary>
    HasChildren = 1,

    /// <summary>Expanded. The table toggles this when the chevron is clicked.</summary>
    Open = 2,

    /// <summary>Selected. The table sets and clears this.</summary>
    Selected = 4,

    /// <summary>Children are arriving: an open node shows a spinner row until this is cleared.</summary>
    Loading = 8,
}

/// <summary>What the tree table reported after its rows were drawn.</summary>
public readonly struct TreeTableEvents
{
    /// <summary>The node chosen with Enter or a double click, or -1.</summary>
    public readonly int Activated;

    /// <summary>The node the user opened this frame, or -1.</summary>
    public readonly int Expanded;

    /// <summary>The node the user closed this frame, or -1.</summary>
    public readonly int Collapsed;

    /// <summary>True when the selection changed.</summary>
    public readonly bool SelectionChanged;

    /// <summary>True when a sortable header was clicked: sort the model and pass the new root back in.</summary>
    public readonly bool SortChanged;

    private readonly int[] _load;
    private readonly int[] _dropped;

    internal TreeTableEvents(int activated, int expanded, int collapsed, bool selectionChanged, bool sortChanged,
        int[] load, int[] dropped, int dropTarget, TreeDrop dropWhere)
    {
        Activated = activated;
        Expanded = expanded;
        Collapsed = collapsed;
        SelectionChanged = selectionChanged;
        SortChanged = sortChanged;
        _load = load;
        _dropped = dropped;
        DropTarget = dropTarget;
        DropWhere = dropWhere;
    }

    /// <summary>
    /// Nodes whose children the table wants: each now has <see cref="TreeNodeFlags.Loading"/>.
    /// Link them in, then clear Loading.
    /// </summary>
    /// <remarks>The ids are copied out of frame memory, so the array stays valid.</remarks>
    public ReadOnlySpan<int> Load => _load;

    /// <summary>
    /// The nodes a drag dropped, in the order the table reports them. Move them (each with its
    /// subtree) with <see cref="CuiTree.Move"/>; until then the model is unchanged.
    /// </summary>
    /// <remarks>The ids are copied out of frame memory, so the array stays valid.</remarks>
    public ReadOnlySpan<int> Dropped => _dropped;

    /// <summary>The node the drop landed at, or -1.</summary>
    public readonly int DropTarget;

    /// <summary>Where the dropped nodes go next to <see cref="DropTarget"/>.</summary>
    public readonly TreeDrop DropWhere;

    internal static unsafe TreeTableEvents FromNative(NativeTreeTableEvents e)
    {
        int[] load = e.LoadCount > 0 && e.Load != 0
            ? new ReadOnlySpan<int>((void*)e.Load, e.LoadCount).ToArray()
            : [];
        int[] dropped = e.DropCount > 0 && e.Dropped != 0
            ? new ReadOnlySpan<int>((void*)e.Dropped, e.DropCount).ToArray()
            : [];
        return new TreeTableEvents(e.Activated, e.Expanded, e.Collapsed,
            e.SelectionChanged != 0, e.SortChanged != 0, load, dropped, e.DropTarget, (TreeDrop)e.DropWhere);
    }
}

/// <summary>Draws one tree row: the caller writes the cells, the widget the indentation and chevron.</summary>
public delegate void TreeTableRow(Ui ui, int node, int depth);

/// <summary>
/// The tree table: an expandable tree with data-grid columns, for file explorers, scene
/// graphs and debugger variables. The widget flattens the open part of the tree and builds
/// only the rows in view.
/// </summary>
public static unsafe class TreeTableUiExtensions
{
    extension(Ui ui)
    {
        /// <summary>
        /// Draws <paramref name="nodes"/> as a tree with <paramref name="columns"/>, calling
        /// <paramref name="row"/> once per visible node to fill its cells.
        /// </summary>
        /// <param name="id">The widget id, also the key the label delegate is cached under.</param>
        /// <param name="columns">The columns, as for <c>ui.Table</c>.</param>
        /// <param name="nodes">The model, read and written in place (open, selected, loading, relinked).</param>
        /// <param name="row">Draws one row: start with <c>ui.TreeTableName(...)</c>, then the other cells.</param>
        /// <param name="sort">The header sort state; the table writes it back.</param>
        /// <param name="cursor">The keyboard's node index, read and written.</param>
        /// <param name="o">Size, row height, root node, guides.</param>
        /// <param name="label">Optional names for type-ahead; hoist it, since a closure allocates.</param>
        /// <example><code>
        /// var events = ui.TreeTable("files", Columns, _nodes, DrawRow, ref _sort, ref _cursor,
        ///     new TreeTableOpts { Height = 320, Guides = true });
        ///
        /// foreach (int node in events.Load) LoadChildren(node);
        /// if (events.SortChanged) _root = CuiTree.Sort(_nodes, _root, Compare);
        /// </code></example>
        public TreeTableEvents TreeTable(UiText id, ReadOnlySpan<GridColumn> columns, TreeNode[] nodes,
            TreeTableRow row, ref TableSort sort, ref int cursor, TreeTableOpts o = default, Func<int, string?>? label = null) =>
            ui.TreeTable(id, columns, nodes, row, ref sort, ref cursor, canDrop: null, o: o, label: label);

        /// <summary>Draws the tree and lets <paramref name="canDrop"/> refuse candidate destinations during a drag.</summary>
        public TreeTableEvents TreeTable(UiText id, ReadOnlySpan<GridColumn> columns, TreeNode[] nodes,
            TreeTableRow row, ref TableSort sort, ref int cursor, TreeTableOpts o, Func<int, string?>? label,
            Func<int, TreeDrop, bool>? canDrop)
        {
            ArgumentNullException.ThrowIfNull(nodes);
            ArgumentNullException.ThrowIfNull(row);

            var nativeSort = sort.ToNative();
            int keyboard = cursor;
            fixed (TreeNode* model = nodes)
            {
                var events = Run(ui, id, Columns(ui, columns), columns.Length, model, nodes.Length, row, ref nativeSort,
                    ref keyboard, o, label, canDrop);
                sort = TableSort.FromNative(nativeSort);
                cursor = keyboard;
                return events;
            }
        }

        /// <summary>
        /// Draws a tree without sort or keyboard state: the table keeps no per-id interaction
        /// state, which is all a static tree needs.
        /// </summary>
        public TreeTableEvents TreeTable(UiText id, ReadOnlySpan<GridColumn> columns, TreeNode[] nodes,
            TreeTableRow row, TreeTableOpts o = default, Func<int, string?>? label = null) =>
            ui.TreeTable(id, columns, nodes, row, canDrop: null, o: o, label: label);

        /// <summary>Draws the tree with a drop predicate and without exposing its sort or cursor.</summary>
        public TreeTableEvents TreeTable(UiText id, ReadOnlySpan<GridColumn> columns, TreeNode[] nodes,
            TreeTableRow row, TreeTableOpts o, Func<int, string?>? label, Func<int, TreeDrop, bool>? canDrop)
        {
            ArgumentNullException.ThrowIfNull(nodes);
            ArgumentNullException.ThrowIfNull(row);

            var sort = new NativeTableSort();
            int cursor = 0;
            fixed (TreeNode* model = nodes)
            {
                return Run(ui, id, Columns(ui, columns), columns.Length, model, nodes.Length, row, ref sort, ref cursor, o, label, canDrop);
            }
        }
    }

    private static NativeGridColumn* Columns(Ui target, ReadOnlySpan<GridColumn> columns)
    {
        var block = Utf8.AllocFrame<NativeGridColumn>(columns.Length);
        for (int i = 0; i < columns.Length; i++) block[i] = columns[i].ToNative();
        return block;
    }

    private static TreeTableEvents Run(Ui target, UiText id, NativeGridColumn* cols, int columnCount, TreeNode* model,
        int nodeCount, TreeTableRow row, ref NativeTableSort sort, ref int cursor, TreeTableOpts o, Func<int, string?>? label,
        Func<int, TreeDrop, bool>? canDrop)
    {
        var opts = o.ToNative((nint)Unsafe.AsPointer(ref sort), (nint)Unsafe.AsPointer(ref cursor));
        if (label is not null || canDrop is not null)
        {
            opts.User = FrameCallbacks.RentTreeHooks(target.FrameState, label, canDrop);
            if (label is not null) opts.Label = FrameCallbacks.TreeLabelPointer;
            if (canDrop is not null) opts.CanDrop = FrameCallbacks.CanDropPointer;
        }

        Native.TreeTableBegin(target.Handle, Utf8.Get(id), (nint)cols, columnCount, (nint)model, nodeCount, in opts);
        NativeTreeTableEvents nativeEvents;
        try
        {
            int node;
            while ((node = Native.TreeTableRow(target.Handle)) >= 0)
                row(target, node, Native.TreeTableDepth(target.Handle));
        }
        finally
        {
            nativeEvents = Native.TreeTableEnd(target.Handle);
        }

        var events = TreeTableEvents.FromNative(nativeEvents);
        WidgetRecorder.Note(target.Handle, "TreeTable", id);
        return events;
    }
}

/// <summary>Sorts a tree model the way the tree table expects: every sibling list, relinked.</summary>
public static unsafe class CuiTree
{
    /// <summary>
    /// Sorts each sibling list of <paramref name="nodes"/> with <paramref name="compare"/>,
    /// relinking the indices, and returns the new first top level node.
    /// </summary>
    /// <param name="nodes">The model, rewritten in place.</param>
    /// <param name="root">The current first top level node.</param>
    /// <param name="compare">Compares two node indices; keep it hoisted, since a closure allocates.</param>
    /// <param name="key">Cache key for the delegate, so a steady frame does not re-allocate its handle.</param>
    public static int Sort(TreeNode[] nodes, int root, Comparison<int> compare, string key = "tree-sort")
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(compare);
        if (nodes.Length == 0) return root;

        nint user = FrameCallbacks.Rent(key, compare);
        fixed (TreeNode* model = nodes)
        {
            return Native.TreeSort((nint)model, nodes.Length, root,
                FrameCallbacks.TreeComparePointer, user);
        }
    }

    /// <summary>
    /// Moves nodes — each with its subtree — before, into (after the last child) or after
    /// <paramref name="target"/>, keeping their order, and relinks <paramref name="nodes"/>.
    /// Returns the new first top level node. A target that is one of the moved nodes or inside
    /// one, or a node listed twice, moves nothing. Flags stay as they are: open the target
    /// yourself if you want the moved nodes in view.
    /// </summary>
    /// <param name="nodes">The model, rewritten in place.</param>
    /// <param name="root">The current first top level node.</param>
    /// <param name="move">The nodes to move, as <see cref="TreeTableEvents.Dropped"/> reports them.</param>
    /// <param name="target">The node to move them next to, or a negative one to move nothing.</param>
    /// <param name="where">Where they go next to <paramref name="target"/>.</param>
    public static int Move(TreeNode[] nodes, int root, ReadOnlySpan<int> move, int target, TreeDrop where)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (nodes.Length == 0 || move.IsEmpty || target < 0) return root;

        fixed (TreeNode* model = nodes)
        fixed (int* movePtr = move)
            return Native.TreeMove((nint)model, nodes.Length, root, (nint)movePtr, move.Length, target, (int)where);
    }
}
