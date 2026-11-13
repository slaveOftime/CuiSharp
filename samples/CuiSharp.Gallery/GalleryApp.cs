// A faithful C# port of CUI's component gallery: the thirteen pages of
// examples/gallery_ui.c and the fifteen of examples/gallery_more.c, the same
// components, the same state machine.
//
// State lives in fields instead of CUI. That is the whole point of the
// immediate-mode model: nothing to invalidate, no view model to keep in sync.
//
// Arrays handed to CUI are copied into the frame arena, so per-frame arrays are
// legal; hoisting the static ones documents that they never change.
using CuiSharp;

namespace CuiSharp.Gallery;


internal sealed partial class GalleryApp : IDisposable
{
    public const int PageCount = 29;

    /// <summary>The release this gallery shows. A literal on purpose: bump it by hand with
    /// <c>Directory.Build.props</c>, and every host that links these sources — desktop and
    /// browser — shows the same number.</summary>
    internal const string ReleaseVersion = "0.4.2";

    /// <summary>The first page ported from <c>examples/gallery_more.c</c>; from here on every
    /// page draws inside its own id scope, as the C gallery's second half does.</summary>
    private const int FirstExtendedPage = 13;

    /// <summary>The page index of <em>Social and media</em>: the only page that shows the
    /// scene textures, so the only page they are kept alive for.</summary>
    private const int MediaPage = 21;

    internal static readonly string[] PageNames =
    {
        "Overview", "Buttons", "Inputs", "Selection", "Navigation",
        "Overlays", "Feedback", "Data display", "Charts", "Studio", "Tools",
        "Developer tools", "More developer tools",
        "Actions", "Smart inputs", "Pickers", "Capture", "Disclosure", "Reader",
        "Data grid", "Code", "Social and media", "Planning", "More charts",
        "Motion", "AI", "Audio", "Files", "Instruments",
    };

    internal static readonly string[] PageIcons =
    {
        Icons.LayoutDashboard, Icons.Zap, Icons.Type, Icons.SlidersHorizontal,
        Icons.Compass, Icons.Layers, Icons.Bell, Icons.Database, Icons.ChartLine,
        Icons.SlidersHorizontal, Icons.Terminal, Icons.SquareTerminal, Icons.Bug,
        Icons.Command, Icons.SquarePen, Icons.Calendar, Icons.Upload, Icons.Layers,
        Icons.FileText, Icons.Grid3x3, Icons.Code, Icons.MessageSquare, Icons.ListChecks,
        Icons.ChartPie, Icons.Activity, Icons.MessageSquare, Icons.AudioLines, Icons.Folder,
        Icons.Activity,
    };

    private static readonly string[] AccentNames =
    {
        "Neutral", "Violet", "Blue", "Green", "Amber", "Orange", "Coral", "Rose",
    };

    private static readonly string[] Modes = { "Light", "Dark" };
    private static readonly string[] ModeIcons = { Icons.Sun, Icons.Moon };
    private static readonly string[] ViewNames = { "List", "Board", "Timeline" };
    private static readonly string[] ViewIcons = { Icons.List, Icons.LayoutDashboard, Icons.History };
    private static readonly string[] RangeNames = { "Day", "Week", "Month", "Year" };
    private static readonly string[] CountryNames =
    {
        "Canada", "Denmark", "Germany", "Iran", "Japan", "Kenya", "Netherlands", "Portugal", "United States",
    };
    private static readonly string[] LanguageNames =
    {
        "C99", "C11", "C17", "C23", "C++17", "Objective-C", "Zig", "Odin", "Rust",
    };
    private static readonly string[] ChipNames =
    {
        "Design", "Engineering", "Marketing", "Sales", "Support", "Finance",
    };
    private static readonly string[] TabNames = { "Overview", "Analytics", "Reports", "Settings" };
    private static readonly string[] Tab2Names = { "Account", "Password", "Billing" };
    private static readonly string[] Tab2Icons = { Icons.User, Icons.Lock, Icons.CreditCard };
    private static readonly string[] Crumbs = { "Home", "Projects", "CUI", "Settings" };
    private static readonly string[] Steps = { "Account", "Workspace", "Invite team", "Done" };
    private static readonly string[] FileNames = { "button.c", "dialog.c", "slider.c", "table.c" };
    private static readonly string[] TaskNames =
    {
        "Write the release notes", "Record the demo video", "Update the screenshots",
        "Tag version 1.0", "Post the announcement",
    };
    private static readonly string[] TaskLabels = { "Docs", "Video", "Docs", "Release", "Blog" };

    private static readonly Tone[] TaskTones =
    {
        Tone.Info, Tone.Neutral, Tone.Info, Tone.Warning, Tone.Teal,
    };

    private static readonly MonthData Months = new();

    private static readonly Choice[] Places =
    [
        new("This computer", "Saved next to the project. Nothing leaves the machine.", Icons.Monitor),
        new("Team server", "Synced to your company server every few minutes.", Icons.Server, "Default"),
        new("Both", "A local copy, uploaded to the server every night.", Icons.Layers),
    ];

    private static readonly Command[] Commands =
    [
        new("Go to Overview", Icons.LayoutDashboard, "Navigation"),
        new("Go to Buttons", Icons.Zap, "Navigation"),
        new("Go to Inputs", Icons.Type, "Navigation"),
        new("Go to Charts", Icons.ChartLine, "Navigation"),
        new("Toggle dark mode", Icons.Moon, "Preferences", "Ctrl J"),
        new("Change accent color", Icons.Palette, "Preferences"),
        new("New project", Icons.Plus, "Actions", "Ctrl N"),
        new("Invite teammate", Icons.UserPlus, "Actions"),
        new("Open settings", Icons.Settings, "Actions", "Ctrl ,"),
    ];

    private static readonly TableColumn[] PeopleColumns =
    [
        new("Name", Size.Fill, TextAlign.Left, true),
        new("Email", Size.Grow(1.2f), TextAlign.Left, false),
        new("Role", Size.Px(110), TextAlign.Left, true),
        new("Status", Size.Px(120), TextAlign.Left, true),
        new("Revenue", Size.Px(110), TextAlign.Right, false),
    ];

    private static readonly PersonRow[] People =
    [
        new("Jonas Berg", "jonas@northlight.io", "Owner", "Active", Tone.Success, "$12,384"),
        new("Priya Nair", "priya@harbor.dev", "Admin", "Active", Tone.Success, "$9,846"),
        new("Tomas Rivera", "tomas@kiln.app", "Editor", "Invited", Tone.Warning, "$7,215"),
        new("Lena Fischer", "lena@fischer.dev", "Viewer", "Active", Tone.Success, "$3,127"),
        new("Aiko Tanaka", "aiko@tanaka.design", "Editor", "Suspended", Tone.Danger, "$1,962"),
    ];

    private static readonly string[] TeamNames =
    {
        "Jonas Berg", "Priya Nair", "Tomas Rivera", "Lena Fischer",
    };

    private static readonly string[] TeamMails =
    {
        "jonas@northlight.io", "priya@harbor.dev", "tomas@kiln.app", "lena@fischer.dev",
    };

    private static readonly string[] TeamRoles = { "Owner", "Admin", "Editor", "Viewer" };

    private static readonly string[] AvatarTeam =
    {
        "Kwame Asante", "Hana Novak", "Elif Kaya", "Ben Carter", "Omar Haddad", "Felix Moreau", "Iris Coleman",
    };

    // ---- state (the C gallery's `gallery` struct) -----------------------
    public int Page { get; set; }
    private bool _drawer;

    private bool _loading;
    private int _segView = 1, _segSize;
    private int _tab, _tab2, _pageIndex, _step;

    private string _name = "";
    private string _email = "";
    private string _password = "";
    private string _search = "";
    private string _bio = "";
    private string _tags = "";
    private string _otp = "";

    private bool _newsletter, _marketing, _terms, _wifi = true, _bluetooth, _airplane;
    private int _plan = 1, _planCard = 1, _rating = 4, _framework = -1, _country = 2;
    private float _volume = 64, _brightness = 40, _lo = 20, _hi = 75;
    private double _quantity = 3;
    private Date _date = new(2026, 11, 14);
    private Color _brand = Color.Rgb(0x7747ff);
    private readonly bool[] _chips = new bool[6];

    private bool _dialogOpen, _sheetOpen, _paletteOpen, _confirmOpen;
    private bool _acc0, _acc1, _acc2;
    private bool _treeSrc = true, _treeUi = true, _treeAssets;
    private int _selectedRow = 1, _selectedFile = 2;
    private TableSort _sort;
    private readonly int[] _taskOrder = [0, 1, 2, 3, 4];

    private readonly Series[] _lineSeries;
    private readonly Series[] _barSeries;
    private readonly Series[] _stackedSeries;

    public GalleryApp()
    {
        _name = "Maya Okafor";
        _email = "maya@fieldwork.studio";
        _bio = "Mathematician and writer. I like engines that compute and interfaces that stay out of the way.";
        _tags = "design, c99, native";
        _otp = "418";
        _chips[0] = true;
        _chips[2] = true;

        _lineSeries =
        [
            new Series { Name = "Revenue", Values = Months.Revenue, Color = Color.Accent },
            new Series { Name = "Costs", Values = Months.Costs, Color = Color.Series(1) },
        ];
        _barSeries =
        [
            new Series { Name = "Desktop", Values = Months.Desktop, Color = Color.Series(0) },
            new Series { Name = "Mobile", Values = Months.Mobile, Color = Color.Series(1) },
        ];
        _stackedSeries =
        [
            new Series { Name = "Passed", Values = Months.Passed, Color = Color.Series(0) },
            new Series { Name = "Retried", Values = Months.Retried, Color = Color.Series(1) },
            new Series { Name = "Failed", Values = Months.Failed, Color = Color.Series(2) },
        ];
    }

    // ---- frame ----------------------------------------------------------

    // ---- sidebar --------------------------------------------------------


    // ---- 1. overview ----------------------------------------------------

    // ---- 2. buttons -----------------------------------------------------

    // ---- 3. inputs ------------------------------------------------------

    // ---- 4. selection ---------------------------------------------------

    // ---- 5. navigation --------------------------------------------------

    // ---- 6. overlays ----------------------------------------------------

    // ---- 7. feedback ----------------------------------------------------

    // ---- 8. data display ------------------------------------------------

    // ---- 9. charts ------------------------------------------------------

    // ---- lifetime -------------------------------------------------------

    /// <summary>Text fields are plain strings now, so there is nothing unmanaged to release.</summary>
    public void Dispose() => _shell?.Dispose();

    // ---- shapes ---------------------------------------------------------

    private readonly record struct PersonRow(
        string Name, string Email, string Role, string Status, Tone Tone, string Amount);

    /// <summary>The static chart and sparkline data the original keeps in file scope.</summary>
    private sealed class MonthData
    {
        public readonly string[] Names =
        {
            "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
        };

        public readonly float[] Revenue = { 18, 22, 21, 27, 31, 29, 36, 41, 39, 46, 52, 58 };
        public readonly float[] Costs = { 12, 13, 15, 14, 18, 19, 21, 22, 24, 25, 27, 29 };
        public readonly float[] Desktop = { 42, 51, 38, 64, 58, 71 };
        public readonly float[] Mobile = { 28, 33, 30, 41, 46, 49 };
        public readonly float[] Passed = { 112, 124, 131, 128, 140, 152, 149, 161 };
        public readonly float[] Retried = { 9, 11, 8, 12, 10, 9, 13, 8 };
        public readonly float[] Failed = { 4, 6, 3, 5, 7, 4, 6, 3 };
        public readonly float[] Shares = { 46, 27, 17, 10 };
        public readonly string[] ShareLabels = { "Windows", "macOS", "Linux", "Other" };
        public readonly float[] Spark = { 9, 7, 8, 6, 5, 6, 4, 4, 3 };
        public readonly string[] Weeks = { "W1", "W2", "W3", "W4", "W5", "W6", "W7", "W8" };
    }
}
