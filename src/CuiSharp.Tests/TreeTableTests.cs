namespace CuiSharp.Tests;

/// <summary>
/// The tree table and its sorter: a real node model, rows built from it, the lazy-load list,
/// and the sort that relinks siblings. These paths go through two native callbacks (the label
/// for type-ahead and the comparator), which is the part that cannot be checked by compiling.
/// </summary>
public class TreeTableTests
{
    private static readonly GridColumn[] Columns =
    [
        new("Name", Width: 200, TextAlign.Left, Sortable: true),
    ];

    [Fact]
    public void RowsComeFromTheModelAndOpenNodesShowTheirChildren()
    {
        if (!OperatingSystem.IsWindows()) return;

        //  0 root
        //   1 alpha (open)     2 beta
        //    3 alpha-1
        var nodes = new TreeNode[4];
        nodes[0] = new TreeNode { Parent = -1, FirstChild = 1, NextSibling = -1, Flags = TreeNodeFlags.Open };
        nodes[1] = new TreeNode { Parent = 0, FirstChild = 3, NextSibling = 2, Flags = TreeNodeFlags.Open };
        nodes[2] = new TreeNode { Parent = 0, FirstChild = -1, NextSibling = -1 };
        nodes[3] = new TreeNode { Parent = 1, FirstChild = -1, NextSibling = -1 };
        string[] names = ["root", "alpha", "beta", "alpha-1"];

        using var surface = CuiSurface.Create(600, 400);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        var visited = new List<(int Node, int Depth)>();
        TreeTableEvents events = default;

        surface.Draw(ui =>
        {
            events = ui.TreeTable("tree", Columns, nodes, (ui, node, depth) =>
            {
                visited.Add((node, depth));
                ui.TreeTableName(names[node], "");
            }, new TreeTableOpts { Height = 200, Guides = true }, node => names[node]);
        }, pixels);

        Assert.Empty(surface.Faults);
        Assert.Contains(pixels, b => b != 0);
        Assert.Equal([(0, 0), (1, 1), (3, 2), (2, 1)], visited);
        Assert.Equal(-1, events.Activated);
        Assert.True(events.Load.IsEmpty);

        // Closing alpha hides its child on the next frame, and only that child.
        nodes[1].Flags &= ~TreeNodeFlags.Open;
        visited.Clear();
        surface.Draw(ui =>
        {
            events = ui.TreeTable("tree", Columns, nodes, (ui, node, depth) =>
            {
                visited.Add((node, depth));
                ui.TreeTableName(names[node], "");
            }, new TreeTableOpts { Height = 200 }, node => names[node]);
        }, pixels);

        Assert.Equal([(0, 0), (1, 1), (2, 1)], visited);
    }

    [Fact]
    public void ExpandingANodeWithNoChildrenAsksForThem()
    {
        if (!OperatingSystem.IsWindows()) return;

        // A node with HasChildren and nothing loaded: opening it is what asks for the children.
        var nodes = new TreeNode[2];
        nodes[0] = new TreeNode { Parent = -1, FirstChild = 1, NextSibling = -1, Flags = TreeNodeFlags.Open };
        nodes[1] = new TreeNode
        {
            Parent = 0,
            FirstChild = -1,
            NextSibling = -1,
            Flags = TreeNodeFlags.HasChildren,
        };

        using var surface = CuiSurface.Create(600, 300);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        var sort = new TableSort();
        int cursor = 0;
        TreeTableEvents events = default;

        void Draw()
        {
            surface.Draw(ui =>
            {
                events = ui.TreeTable("tree", Columns, nodes,
                    (ui, node, depth) => ui.TreeTableName("node" + node, ""),
                    ref sort, ref cursor, new TreeTableOpts { Height = 160 });
            }, pixels);
        }

        Draw();
        Assert.Empty(surface.Faults);
        Assert.True(events.Load.IsEmpty);

        // Focus the table, move to the child, then open it with Right.
        surface.Focus(true);
        surface.MouseMove(120, 48);   // the first data row, below the header
        surface.MouseDown();
        Draw();
        surface.MouseUp();
        Draw();

        surface.KeyDown(Key.Down);
        Draw();
        surface.KeyUp(Key.Down);
        surface.KeyDown(Key.Right);
        Draw();
        surface.KeyUp(Key.Right);

        Assert.Empty(surface.Faults);
        Assert.Contains(1, events.Load.ToArray());
        Assert.True((nodes[1].Flags & TreeNodeFlags.Loading) != 0);
        Assert.True(nodes[1].IsOpen);
    }

    [Fact]
    public void SortRelinksSiblingsAndKeepsTheRoot()
    {
        if (!OperatingSystem.IsWindows()) return;

        //  0 root -> children 1, 2, 3 (beta, gamma, alpha)
        var nodes = new TreeNode[4];
        nodes[0] = new TreeNode { Parent = -1, FirstChild = 1, NextSibling = -1 };
        nodes[1] = new TreeNode { Parent = 0, FirstChild = -1, NextSibling = 2 };
        nodes[2] = new TreeNode { Parent = 0, FirstChild = -1, NextSibling = 3 };
        nodes[3] = new TreeNode { Parent = 0, FirstChild = -1, NextSibling = -1 };
        string[] names = ["root", "beta", "gamma", "alpha"];

        int root = CuiTree.Sort(nodes, 0, (a, b) => string.CompareOrdinal(names[a], names[b]));
        Assert.Equal(0, root);

        // Walk the relinked list: alpha, beta, gamma.
        var order = new List<int>();
        for (int n = nodes[root].FirstChild; n >= 0; n = nodes[n].NextSibling) order.Add(n);
        Assert.Equal([3, 1, 2], order);
        Assert.All(order, n => Assert.Equal(0, nodes[n].Parent));

        // And the table then draws that order.
        using var surface = CuiSurface.Create(600, 300);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        var drawn = new List<int>();
        surface.Draw(ui =>
        {
            nodes[0].Flags |= TreeNodeFlags.Open;
            ui.TreeTable("tree", Columns, nodes, (ui, node, depth) =>
            {
                if (depth > 0) drawn.Add(node);
                ui.TreeTableName(names[node], "");
            }, new TreeTableOpts { Height = 160, Root = root });
        }, pixels);

        Assert.Empty(surface.Faults);
        Assert.Equal([3, 1, 2], drawn);
    }

    [Fact]
    public void TypingJumpsToTheNodeWhoseNameMatches()
    {
        if (!OperatingSystem.IsWindows()) return;

        var nodes = new TreeNode[3];
        nodes[0] = new TreeNode { Parent = -1, FirstChild = 1, NextSibling = -1, Flags = TreeNodeFlags.Open };
        nodes[1] = new TreeNode { Parent = 0, FirstChild = -1, NextSibling = 2 };
        nodes[2] = new TreeNode { Parent = 0, FirstChild = -1, NextSibling = -1 };
        string[] names = ["root", "alpha", "beta"];

        using var surface = CuiSurface.Create(600, 300);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        var sort = new TableSort();
        int cursor = 0;

        // The label callback is what type-ahead reads; the first frame registers it.
        surface.Draw(ui =>
        {
            ui.TreeTable("tree", Columns, nodes, (ui, node, depth) => ui.TreeTableName(names[node], ""),
                ref sort, ref cursor, new TreeTableOpts { Height = 160, Guides = false }, node => names[node]);
        }, pixels);

        // The table takes the keyboard when a row is clicked; then typing "b" moves the
        // cursor to beta, which only the label hook can resolve.
        surface.Focus(true);
        surface.MouseMove(120, 48);   // the first data row, below the header
        surface.MouseDown();
        surface.Draw(ui =>
        {
            ui.TreeTable("tree", Columns, nodes, (ui, node, depth) => ui.TreeTableName(names[node], ""),
                ref sort, ref cursor, new TreeTableOpts { Height = 160 }, node => names[node]);
        }, pixels);
        surface.MouseUp();
        surface.Draw(ui =>
        {
            ui.TreeTable("tree", Columns, nodes, (ui, node, depth) => ui.TreeTableName(names[node], ""),
                ref sort, ref cursor, new TreeTableOpts { Height = 160 }, node => names[node]);
        }, pixels);

        surface.Type("b");
        surface.Draw(ui =>
        {
            ui.TreeTable("tree", Columns, nodes, (ui, node, depth) => ui.TreeTableName(names[node], ""),
                ref sort, ref cursor, new TreeTableOpts { Height = 160 }, node => names[node]);
        }, pixels);

        Assert.Empty(surface.Faults);
        Assert.Equal(2, cursor);
    }

    //  0 root (open) -> 1 alpha, 2 beta     3 gamma (a second top level node)
    // Dragging alpha over beta has a target outside the dragged subtree; a model where every
    // node descends from the dragged one has none, and native rightly refuses it all.
    private static readonly string[] DragNames = ["root", "alpha", "beta", "gamma"];

    private static TreeNode[] NewDragModel()
    {
        var nodes = new TreeNode[4];
        nodes[0] = new TreeNode { Parent = -1, FirstChild = 1, NextSibling = 3, Flags = TreeNodeFlags.Open };
        nodes[1] = new TreeNode { Parent = 0, FirstChild = -1, NextSibling = 2 };
        nodes[2] = new TreeNode { Parent = 0, FirstChild = -1, NextSibling = -1 };
        nodes[3] = new TreeNode { Parent = -1, FirstChild = -1, NextSibling = -1 };
        return nodes;
    }

    private static TreeTableEvents DragFromAlphaToBeta(CuiSurface surface, TreeNode[] nodes,
        TreeTableOpts o, Func<int, string?>? label = null, Func<int, TreeDrop, bool>? canDrop = null)
    {
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        var sort = new TableSort();
        int cursor = 0;
        TreeTableEvents events = default, reported = default;

        // Rows start at y=40 (below the header) and step 32: alpha is the row at 72; the drag
        // ends over beta's bottom half, so the drop lands after beta.
        void Draw()
        {
            surface.Draw(ui =>
            {
                events = ui.TreeTable("tree", Columns, nodes,
                    (ui, node, depth) => ui.TreeTableName(DragNames[node], ""), ref sort, ref cursor, o,
                    label, canDrop);
                // The drop is reported on the frame the release lands in; later frames report nothing.
                if (!events.Dropped.IsEmpty) reported = events;
            }, pixels);
            File.AppendAllText("tt-debug.txt",
                $"  draw: dropped=[{string.Join(',', events.Dropped.ToArray())}] target={events.DropTarget} sel={string.Join(',', Enumerable.Range(0, nodes.Length).Where(n => (nodes[n].Flags & TreeNodeFlags.Selected) != 0))} px={surface.PixelWidth}\n");
        }

        surface.Draw(ui =>
        {
            events = ui.TreeTable("tree", Columns, nodes,
                (ui, node, depth) => ui.TreeTableName(DragNames[node], ""), ref sort, ref cursor, o,
                label, canDrop);
        }, pixels);   // warmup: the drop target reads the previous frame's laid-out rows
        surface.Focus(true);
        surface.MouseMove(300, 88);
        Draw();
        surface.MouseDown();
        Draw();
        for (int step = 1; step <= 6; step++)
        {
            surface.MouseMove(300, 88 + (128 - 88) * step / 6);
            Draw();
        }
        surface.MouseUp();
        Draw();
        Draw();
        Assert.Empty(surface.Faults);
        return reported;
    }

    [Fact]
    public void ADragDropsWhereTheButtonWentUpAndTheModelIsUnchangedUntilMove()
    {
        if (!OperatingSystem.IsWindows()) return;

        var nodes = NewDragModel();
        using var surface = CuiSurface.Create(600, 400);
        File.AppendAllText("tt-debug.txt", $"surface {surface.PixelWidth}x{surface.PixelHeight}\n");
        var events = DragFromAlphaToBeta(surface, nodes, new TreeTableOpts { Height = 300, Draggable = true });

        Assert.NotEmpty(events.Dropped.ToArray());
        Assert.Equal(1, events.Dropped[0]);       // the row that was dragged
        Assert.Equal(2, events.DropTarget);       // the row the button went up over
        Assert.Equal(TreeDrop.After, events.DropWhere);

        // The table only reports the drop; the model is unchanged until the app moves the nodes.
        Assert.Equal(0, nodes[1].Parent);
        Assert.Equal(2, nodes[1].NextSibling);

        int root = CuiTree.Move(nodes, 0, events.Dropped, events.DropTarget, events.DropWhere);
        Assert.Equal(0, root);
        Assert.Equal(2, nodes[0].FirstChild);     // alpha now comes after beta
        Assert.Equal(1, nodes[2].NextSibling);
        Assert.Equal(-1, nodes[1].NextSibling);
        Assert.Equal(0, nodes[1].Parent);
    }

    [Fact]
    public void ARefusedDropLeavesTheModelUnchanged()
    {
        if (!OperatingSystem.IsWindows()) return;

        var nodes = NewDragModel();
        using var surface = CuiSurface.Create(600, 400);
        var attempts = new List<(int Target, TreeDrop Where)>();
        var events = DragFromAlphaToBeta(surface, nodes, new TreeTableOpts { Height = 300, Draggable = true },
            label: node => DragNames[node], canDrop: (target, where) =>
            {
                attempts.Add((target, where));
                return false;
            });

        Assert.Contains((2, TreeDrop.After), attempts);
        Assert.True(events.Dropped.IsEmpty, "a drop the callback refuses must not be reported");
        Assert.Equal(0, nodes[1].Parent);
        Assert.Equal(2, nodes[1].NextSibling);
    }

    [Fact]
    public void TheContextMenuBelongsToTheTableAndReportsTheClickedRow()
    {
        using var surface = CuiSurface.Create(600, 400);
        var nodes = NewDragModel();
        int contextNode = -1;
        bool opened = false;
        void Draw()
        {
            surface.BeginFrame();
            var ui = surface.Ui;
            ui.TreeTable("tree", Columns, nodes, (draw, node, _) => draw.TreeTableName(DragNames[node], ""),
                new TreeTableOpts { Height = 240 });
            using (var menu = ui.TreeTableMenu("tree", ref contextNode))
            {
                opened = menu.Open;
                if (menu) ui.MenuItem("Inspect");
            }
            surface.EndFrame();
        }
        Draw();
        Assert.False(opened);
        surface.Focus(true);
        surface.MouseMove(120, 88);
        Draw();
        surface.MouseDown(MouseButton.Right);
        Draw();
        surface.MouseUp(MouseButton.Right);
        Draw();
        Assert.True(opened);
        Assert.Equal(1, contextNode);
        Assert.Empty(surface.Faults);
    }

    [Fact]
    public void MoveRelinksTheModelAndRejectsACycle()
    {
        if (!OperatingSystem.IsWindows()) return;

        //  0 root -> 1 alpha, 2 beta
        //   1 alpha -> 3 alpha-1
        var nodes = new TreeNode[4];
        nodes[0] = new TreeNode { Parent = -1, FirstChild = 1, NextSibling = -1, Flags = TreeNodeFlags.Open };
        nodes[1] = new TreeNode { Parent = 0, FirstChild = 3, NextSibling = 2 };
        nodes[2] = new TreeNode { Parent = 0, FirstChild = -1, NextSibling = -1 };
        nodes[3] = new TreeNode { Parent = 1, FirstChild = -1, NextSibling = -1 };

        // Move alpha (with its child) after beta: root's children become beta, alpha.
        int root = CuiTree.Move(nodes, 0, [1], 2, TreeDrop.After);
        Assert.Equal(0, root);
        Assert.Equal(2, nodes[0].FirstChild);
        Assert.Equal(1, nodes[2].NextSibling);
        Assert.Equal(-1, nodes[1].NextSibling);
        Assert.Equal(0, nodes[1].Parent);
        Assert.Equal(1, nodes[3].Parent); // the subtree travels along

        // Moving a node into itself or its own subtree moves nothing.
        var before = (TreeNode[])nodes.Clone();
        root = CuiTree.Move(nodes, 0, [1], 1, TreeDrop.Inside);
        Assert.Equal(0, root);
        Assert.Equal(before, nodes);

        // Nothing to move, nothing changes.
        Assert.Equal(0, CuiTree.Move(nodes, 0, ReadOnlySpan<int>.Empty, 2, TreeDrop.Before));
        Assert.Equal(0, CuiTree.Move(nodes, 0, [2], -1, TreeDrop.Before));
    }
}
