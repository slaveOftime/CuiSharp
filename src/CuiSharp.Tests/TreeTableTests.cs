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
}
