// Page 20 of 28: a filter bar, a 2,000-row virtualized data grid, and a tree
// table: tree cells inside a table.
// Part of the CuiSharp.Gallery sample: Data grid
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    private const int PeopleCount = 2000;

    private static readonly string[] FirstNames =
    [
        "Maya", "Priya", "Jonas", "Lena", "Sam", "Tomas", "Aiko", "Daniel",
        "Rosa", "Omar", "Elif", "Ben", "Nora", "Kwame", "Hana", "Felix",
    ];

    private static readonly string[] LastNames =
    [
        "Okafor", "Nair", "Berg", "Fischer", "Whitfield", "Rivera", "Tanaka", "Mensah",
        "Delgado", "Haddad", "Kaya", "Carter", "Lindqvist", "Asante", "Novak", "Moreau",
    ];

    private static readonly string[] Teams = ["Engine", "Design", "Platform", "Docs", "Growth"];

    private static readonly string[] TeamOptions = ["Engine", "Design", "Platform", "Docs", "Growth"];
    private static readonly string[] StatusOptions = ["Active", "Away", "Invited"];
    private static readonly string[] RoleOptions = ["Owner", "Admin", "Member"];

    private static readonly FilterField[] PersonFilters =
    [
        new("Team", Icons.Users, TeamOptions),
        new("Status", Icons.CircleDot, StatusOptions),
        new("Role", Icons.KeyRound, RoleOptions),
    ];

    private static readonly GridColumn[] PersonGridColumns =
    [
        new("Name", 200, TextAlign.Left, true),
        new("Email", 196, TextAlign.Left, false),
        new("Team", 110, TextAlign.Left, true),
        new("Commits", 96, TextAlign.Right, true),
        new("Score", 130, TextAlign.Right, true),
    ];

    private static readonly TableColumn[] FileColumns =
    [
        new("Name", Size.Fill, TextAlign.Left, false),
        new("Files", Size.Px(80), TextAlign.Right, false),
        new("Size", Size.Px(100), TextAlign.Right, false),
    ];

    // The model C builds in a static array: a cheap hash so neighbouring rows do not
    // repeat each other, and a name an in-cell edit can rewrite.
    private readonly GridPerson[] _people = new GridPerson[PeopleCount];
    private readonly int[] _personOrder = new int[PeopleCount];
    private readonly uint[] _personFilters = [1u << 0 | 1u << 2, 0, 0];
    private string _personSearch = "";
    private readonly GridState _personGrid = new(PeopleCount);
    private TableSort _personSortShown = new(-1, false);
    private bool _treeSrcOpen = true, _treeUiOpen;
    private bool _gridSeeded;

    private void PageGrid(Ui ui)
    {
        if (!_gridSeeded) SeedPeople();

        // The grid publishes its sort into the state; the order array follows it,
        // and only on the frames the sort actually moved.
        var sort = _personGrid.Sort;
        if (sort.Column != _personSortShown.Column || sort.Descending != _personSortShown.Descending)
        {
            SortPeople(sort);
            _personSortShown = sort;
        }

        Section(ui, "Filter bar", "Chips for active filters, values in a list, and a search.");
        ui.FilterBar("filters", PersonFilters, _personFilters, ref _personSearch);

        Section(ui, "Data grid", "2,000 rows, virtualized. Sort, resize columns, select with Shift and Ctrl, edit names with a double click.");
        int selected = 0;
        for (int k = 0; k < PeopleCount; k++) selected += _personGrid.Selected[k] ? 1 : 0;

        using (var grid = ui.DataGrid("people", PersonGridColumns, PeopleCount, _personGrid, new GridOpts
        {
            Height = Size.Px(420),
        }))
        {
            if (grid.Open)
            {
                for (int r = _personGrid.First; r < _personGrid.Last; r++)
                {
                    var p = _people[_personOrder[r]];
                    ui.DataGridRow(r);
                    ui.DataGridCell();
                    ui.Avatar(p.Name, new AvatarOpts { Size = 24 });
                    ui.DataGridCellEdit(ref p.Name, 48);
                    ui.DataGridCellText(p.Email);
                    ui.DataGridCell();
                    ui.Badge(p.Team);
                    ui.DataGridCellText($"{p.Commits}");
                    ui.DataGridCell();
                    ui.Progress(p.Score / 100, new ProgressOpts { Width = Size.Px(70), Height = 5 });
                    ui.Text($"{p.Score:0.0}", new TextOpts
                    {
                        Tabular = true,
                        Size = 13,
                        Color = Color.TextSecondary,
                    });
                }
            }
        }

        ui.Muted(selected > 0
            ? "Rows selected. Press Esc to clear."
            : "Click a row, Shift+click to select a range, Ctrl+A for all.");

        Section(ui, "Tree table", "Tree cells inside a table: disclosure, indentation, totals.");
        using (var table = ui.Table("tree", FileColumns))
        {
            if (table.Open)
            {
                ui.TableRow(false);
                ui.TableCell();
                ui.TreeCell("src", 0, true, ref _treeSrcOpen, Icons.Folder);
                ui.TableCellText("42");
                ui.TableCellText("1.2 MB");
                if (_treeSrcOpen)
                {
                    ui.TableRow(false);
                    ui.TableCell();
                    ui.TreeCell("ui", 1, true, ref _treeUiOpen, Icons.Folder);
                    ui.TableCellText("18");
                    ui.TableCellText("640 KB");
                    if (_treeUiOpen)
                    {
                        ui.TableRow(false);
                        ui.TableCell();
                        ui.TreeCell("button.c", 2, false, ref _leafOpen, Icons.File);
                        ui.TableCellText("1");
                        ui.TableCellText("24 KB");
                    }
                    ui.TableRow(false);
                    ui.TableCell();
                    ui.TreeCell("main.c", 1, false, ref _leafOpen, Icons.File);
                    ui.TableCellText("1");
                    ui.TableCellText("8 KB");
                }
                ui.TableRow(false);
                ui.TableCell();
                ui.TreeCell("README.md", 0, false, ref _leafOpen, Icons.FileText);
                ui.TableCellText("1");
                ui.TableCellText("12 KB");
            }
        }
    }

    private void SeedPeople()
    {
        for (int k = 0; k < PeopleCount; k++)
        {
            uint h = (uint)k * 2654435761u;
            h ^= h >> 15;
            h *= 2246822519u;
            h ^= h >> 13;
            string fn = FirstNames[h % 16], ln = LastNames[(h >> 4) % 16];
            _people[k] = new GridPerson(
                $"{fn} {ln}",
                $"{fn}.{char.ToLowerInvariant(ln[0])}{(h >> 8) % 90 + 10}@cui.dev".ToLowerInvariant(),
                Teams[(h >> 12) % 5],
                (int)((h >> 16) % 1400) + 12,
                ((h >> 3) % 1000) / 10.0f);
            _personOrder[k] = k;
        }

        // C starts sorted by commits, descending.
        _personGrid.Sort = new TableSort { Column = 3, Descending = true };
        SortPeople(_personGrid.Sort);
        _personSortShown = _personGrid.Sort;
        _gridSeeded = true;
    }

    private void SortPeople(TableSort sort)
    {
        var order = _personOrder;
        var people = _people;
        Array.Sort(order, (a, b) =>
        {
            int r = sort.Column switch
            {
                0 => string.CompareOrdinal(people[a].Name, people[b].Name),
                2 => string.CompareOrdinal(people[a].Team, people[b].Team),
                3 => people[a].Commits - people[b].Commits,
                _ => people[a].Score.CompareTo(people[b].Score),
            };
            return sort.Descending ? -r : r;
        });
    }

    /// <summary>A grid row of the people model; the name is editable in place.</summary>
    private sealed class GridPerson(string name, string email, string team, int commits, float score)
    {
        public string Name = name;
        public readonly string Email = email;
        public readonly string Team = team;
        public readonly int Commits = commits;
        public readonly float Score = score;
    }
}
