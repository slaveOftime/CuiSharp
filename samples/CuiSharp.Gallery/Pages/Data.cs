// Page 8 of 28: table, tree, lists, accordion, virtual list.
// Part of the CuiSharp.Gallery sample: Data display
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    // Leaf tree nodes have no disclosure state of their own; CUI ignores the flag.
    private bool _leafOpen;

    private void PageData(Ui ui)
    {
        Section(ui, "Table", "Sortable columns, row selection and components in cells.");

        int[] order = [0, 1, 2, 3, 4];
        for (int i = 1; i < 5; i++)
        {
            int v = order[i], j = i - 1;
            while (j >= 0)
            {
                int c = string.CompareOrdinal(Key(People[order[j]]), Key(People[v]));
                if (_sort.Descending ? c < 0 : c > 0) { order[j + 1] = order[j]; j--; }
                else break;
            }
            order[j + 1] = v;
        }

        using (var table = ui.Table("people", PeopleColumns, ref _sort, new TableOpts { Selectable = true }))
        {
            if (table.Open)
            {
                for (int k = 0; k < 5; k++)
                {
                    var r = People[order[k]];
                    if (ui.TableRow(_selectedRow == order[k])) _selectedRow = order[k];
                    ui.TableCell();
                    ui.Avatar(r.Name, new AvatarOpts { Size = 26 });
                    ui.Text(r.Name, new TextOpts { Font = Font.Medium, NoWrap = true });
                    ui.TableCell();
                    ui.Text(r.Email, new TextOpts { Color = Color.TextSecondary, NoWrap = true });
                    ui.TableCellText(r.Role);
                    ui.TableCell();
                    ui.Badge(r.Status, new BadgeOpts { Tone = r.Tone, Dot = true });
                    ui.TableCellText(r.Amount);
                }
            }
        }

        string Key(PersonRow p) => _sort.Column switch
        {
            0 => p.Name,
            2 => p.Role,
            _ => p.Status,
        };

        using (ui.Box(new Box { Dir = Dir.Grid, Columns = 2, MinColumnWidth = 260, Gap = 16 }))
        {
            using (ui.Column(new Box { Gap = 12 }))
            {
                Section(ui, "Tree view", null);
                using (ui.Card(new Card { Pad = 10, Gap = 1 }))
                using (var src = ui.Tree("src", ref _treeSrc))
                {
                    if (src.Open)
                    {
                        using (var comps = ui.Tree("components", ref _treeUi))
                        {
                            if (comps.Open)
                            {
                                for (int i = 0; i < FileNames.Length; i++)
                                {
                                    using (ui.Tree(FileNames[i], ref _leafOpen, new TreeOpts
                                    {
                                        Leaf = true,
                                        Selected = _selectedFile == i,
                                    }))
                                    {
                                        if (ui.TreeClicked()) _selectedFile = i;
                                    }
                                }
                            }
                        }

                        using (var assets = ui.Tree("assets", ref _treeAssets))
                        {
                            if (assets.Open)
                            {
                                using (ui.Tree("logo.png", ref _leafOpen,
                                           new TreeOpts { Leaf = true, Icon = Icons.Image })) { }
                            }
                        }

                        using (ui.Tree("main.c", ref _leafOpen, new TreeOpts { Leaf = true })) { }
                    }
                }
            }

            using (ui.Column(new Box { Gap = 12 }))
            {
                Section(ui, "Tree table", null);
                TreeTableView(ui);
            }

            using (ui.Column(new Box { Gap = 12 }))
            {
                Section(ui, "List", null);
                using (ui.Card(new Card { Pad = 8, Gap = 2 }))
                {
                    ui.ListItem("Design review", new ListItemOpts
                    {
                        Avatar = "Jonas Berg",
                        Subtitle = "Tomorrow at 10:00",
                        Trailing = "45m",
                    });
                    ui.ListItem("Release 1.0", new ListItemOpts
                    {
                        Avatar = "Priya Nair",
                        Subtitle = "Ship it",
                        Trailing = "2h",
                        Selected = true,
                    });
                    ui.ListItem("Planning", new ListItemOpts
                    {
                        Avatar = "Tomas Rivera",
                        Subtitle = "Q1 roadmap",
                        Trailing = "1h",
                    });
                }
            }
        }

        Section(ui, "Reorderable list", "Drag a row. The others make room as you go.");
        using (ui.Column(new Box { Width = Size.Fill }))
        {
            ui.ReorderBegin("tasks", _taskOrder);
            for (int i = 0; i < 5; i++)
            {
                using (ui.ReorderItem(i))
                {
                    int t = _taskOrder[i];
                    ui.Text(TaskNames[t], new TextOpts { NoWrap = true, Width = Size.Fill });
                    ui.Badge(TaskLabels[t], new BadgeOpts { Tone = TaskTones[t] });
                }
            }
            if (ui.ReorderEnd()) ui.Toast("Order saved");
        }

        Section(ui, "Accordion", null);
        using (ui.Card(new Card { PadY = 4, Gap = 0 }))
        {
            using (var a = ui.Accordion("Is it really dependency free?", ref _acc0))
            {
                if (a.Open)
                {
                    ui.Secondary("Yes. The core is plain C99. Windows uses Win32, macOS uses Cocoa and Linux uses X11. " +
                                 "Fonts and icons are embedded.");
                }
            }
            using (var a = ui.Accordion("Can I use my own renderer?", ref _acc1))
            {
                if (a.Open)
                {
                    ui.Secondary("CUI outputs vertices and draw commands. Port the 60 line shader to Vulkan, Metal or " +
                                 "Direct3D and you are done.");
                }
            }
            using (var a = ui.Accordion("Does it respect reduced motion?", ref _acc2))
            {
                if (a.Open)
                {
                    ui.Secondary("Every animation has a reduced motion path that keeps the final state and feedback.");
                }
            }
        }

        Section(ui, "Virtual list", "Ten thousand rows. Only the visible ones are built.");
        using (ui.Card(new Card { Pad = 6 }))
        using (ui.VirtualList("big", 10000, 36, Size.Px(220), out int first, out int last))
        {
            for (int i = first; i < last; i++)
            {
                uint h = (uint)(i + 1) * 2654435761u;
                using (ui.Row(new Box { Height = Size.Px(36), PadX = 12, Gap = 12 }))
                {
                    ui.Icon(Icons.FileText, 16, Color.TextMuted);
                    ui.Text($"Invoice 2026-{10000 - i:D5}", new TextOpts
                    {
                        NoWrap = true,
                        Width = Size.Fill,
                        Tabular = true,
                    });
                    ui.Text($"${40 + (h >> 8) % 2400}.{(h >> 4) % 100:D2}", new TextOpts
                    {
                        Size = 13,
                        Tabular = true,
                        NoWrap = true,
                    });
                    ui.Text((h >> 20) % 5 == 0 ? "Due" : "Paid", new TextOpts
                    {
                        Size = 12,
                        Color = Color.TextMuted,
                        NoWrap = true,
                        Width = Size.Px(32),
                    });
                }
            }
        }
    }

    // ---- tree table: a node model, lazy children, sorting -----------------

    private readonly TreeNode[] _nodes = new TreeNode[7];
    private readonly string[] _nodeNames = ["src", "components", "assets", "main.c", "button.c", "dialog.c", "logo.png"];
    private readonly string[] _nodeKinds = ["folder", "folder", "folder", "file", "file", "file", "image"];
    private readonly bool[] _nodeLoaded = [true, true, true, true, false, false, false];
    private TableSort _treeSort;
    private int _treeCursor;
    private string _treeStatus = "Nothing selected";
    private bool _treeSeeded;

    private static readonly GridColumn[] TreeColumns =
    [
        new("Name", Width: 200, TextAlign.Left, Sortable: true),
        new("Kind", Width: 90, TextAlign.Left, Sortable: false),
    ];

    /// <summary>
    /// A real tree model: a flat node array linked by index, where a folder's children
    /// arrive the first time it is opened. The table asks through
    /// <see cref="TreeTableEvents.Load"/> and relinks siblings through <see cref="CuiTree"/>.
    /// </summary>
    private void TreeTableView(Ui ui)
    {
        if (!_treeSeeded)
        {
            SeedTree();
            _treeSeeded = true;
        }

        var events = ui.TreeTable("files", TreeColumns, _nodes, (ui, node, depth) =>
        {
            ui.TreeTableName(_nodeNames[node], _nodeKinds[node] == "folder" ? Icons.Folder : Icons.File);
            ui.DataGridCellText(_nodeKinds[node]);
        }, ref _treeSort, ref _treeCursor, new TreeTableOpts { Height = 240, Guides = true }, node => _nodeNames[node]);

        foreach (int node in events.Load)
        {
            LoadChildren(node);
            _treeStatus = $"Loaded {_nodeNames[node]}";
        }

        if (events.SortChanged) _ = CuiTree.Sort(_nodes, 0, (a, b) => string.CompareOrdinal(_nodeNames[a], _nodeNames[b]), "gallery-tree");
        if (events.Activated >= 0) _treeStatus = $"Activated {_nodeNames[events.Activated]}";
        else if (events.SelectionChanged) _treeStatus = DescribeSelection();

        ui.Muted(_treeStatus);
    }

    private void SeedTree()
    {
        for (int i = 0; i < _nodes.Length; i++) _nodes[i] = TreeNode.Detached;
        _nodes[0].Flags = TreeNodeFlags.Open;
        _nodes[0].FirstChild = 1;
        _nodes[1].Parent = 0;
        _nodes[1].NextSibling = 2;
        _nodes[1].Flags = TreeNodeFlags.HasChildren;
        _nodes[2].Parent = 0;
        _nodes[2].NextSibling = 3;
        _nodes[2].Flags = TreeNodeFlags.HasChildren;
        _nodes[3].Parent = 0;
        _nodes[4].Parent = 1;
        _nodes[5].Parent = 1;
        _nodes[6].Parent = 2;
    }

    /// <summary>Links a folder's children the first time the table asks for them.</summary>
    private void LoadChildren(int parent)
    {
        int first = -1, last = -1;
        for (int i = 0; i < _nodes.Length; i++)
        {
            if (_nodes[i].Parent != parent || i == parent || i == 0) continue;
            if (_nodes[i].NextSibling >= 0 && _nodes[i].Parent == parent && parent == i) continue;
            if (first < 0) first = i;
            if (last >= 0) _nodes[last].NextSibling = i;
            last = i;
        }

        _nodes[parent].FirstChild = first;
        _nodes[parent].Flags &= ~TreeNodeFlags.Loading;
        _nodes[parent].Flags |= TreeNodeFlags.HasChildren | TreeNodeFlags.Open;
        _nodeLoaded[parent] = true;
    }

    private string DescribeSelection()
    {
        int count = 0, last = -1;
        for (int i = 0; i < _nodes.Length; i++)
        {
            if (!_nodes[i].IsSelected) continue;
            count++;
            last = i;
        }

        return count switch
        {
            0 => "Nothing selected",
            1 => _nodeNames[last],
            _ => $"{count} items selected",
        };
    }
}
