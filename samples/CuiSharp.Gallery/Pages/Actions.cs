// Page 14 of 28: split buttons, toggle groups, morphing buttons, reactions,
// theme switches, a user menu, swipe rows and a speed dial.
// Part of the CuiSharp.Gallery sample: Actions
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    private static readonly string[] SplitItems = ["Save as draft", "Save and publish", "Schedule…"];
    private static readonly string[] SplitItemIcons = [Icons.FileText, Icons.Send, Icons.Clock];
    private static readonly string[] FormatLabels = ["Bold", "Italic", "Underline", "Code"];
    private static readonly string[] FormatIcons = [Icons.Bold, Icons.Italic, Icons.Underline, Icons.Code];
    private static readonly string[] AlignLabels = ["Left", "Center", "Right"];
    private static readonly string[] AlignIcons = [Icons.AlignLeft, Icons.List, Icons.Menu];
    private static readonly string[] SwipeTitles =
    {
        "Review the design tokens", "Reply to Jonas about the release", "Book the offsite venue",
    };

    private static readonly SwipeAction[] SwipeLeft = [new(Icons.Check, "Done", Tone.Success)];
    private static readonly SwipeAction[] SwipeRight =
    [
        new(Icons.Archive, "Archive", Tone.Info), new(Icons.Trash2, "Delete", Tone.Danger),
    ];

    private static readonly SpeedAction[] DialActions =
    [
        new(Icons.FileText, "New note"), new(Icons.Image, "Upload image"), new(Icons.Mic, "Record memo"),
    ];

    // The widget edits these in place: clicking a pill toggles it and rolls its count.
    private readonly Reaction[] _reactions =
    [
        new(Icons.ThumbsUp, 12, true), new(Icons.Heart, 4, false),
        new(Icons.Rocket, 7, false), new(Icons.Smile, 2, false),
    ];

    private readonly bool[] _formatToggles = [true, false, false, false];
    private readonly bool[] _alignToggles = [true, false, false];
    private Status _saveStatus = Status.Idle;
    private double _statusAt;
    private int _dialChoice = -1;
    private int _themeMode = -1;

    // Design tokens the page drives, at CUI's own defaults so the gallery opens looking stock.
    private float _tokenRadius = 10f;
    private float _tokenScale = 1f;
    private string _tokenSample = "";
    private int _deletedProjects;
    private int _splitChoice = -1;

    private void PageActions(Ui ui)
    {
        Section(ui, "Split button", "A main action with related actions one click away.");
        using (ui.Row(new Box { Gap = 12, Wrap = true }))
        {
            int r = ui.SplitButton("Save", SplitItems, new SplitButtonOpts
            {
                ItemIcons = SplitItemIcons,
                Icon = Icons.Save,
            });
            if (r >= 0) _splitChoice = r;
            ui.SplitButton("Export", SplitItems, new SplitButtonOpts
            {
                Variant = ButtonVariant.Outline,
                Icon = Icons.Download,
                ItemIcons = SplitItemIcons,
            });
            if (_splitChoice >= 0) ui.Muted(_splitChoice == 0 ? "Saved" : SplitItems[_splitChoice - 1]);
        }

        Section(ui, "Toggle group", "Independent toggles for toolbars, or exclusive like radio buttons.");
        using (ui.Row(new Box { Gap = 12, Wrap = true }))
        {
            ui.ToggleGroup("format", FormatLabels, _formatToggles, new ToggleGroupOpts
            {
                Icons = FormatIcons,
                IconOnly = true,
            });
            ui.ToggleGroup("align", AlignLabels, _alignToggles, new ToggleGroupOpts
            {
                Icons = AlignIcons,
                Exclusive = true,
            });
        }

        Section(ui, "Morphing buttons", "Confirm before destroying, and show the result of work in place.");
        using (ui.Row(new Box { Gap = 12, Wrap = true }))
        {
            if (ui.ConfirmButton("Delete project", new ConfirmOpts { Icon = Icons.Trash2 })) _deletedProjects++;
            if (ui.StatusButton("Save changes", _saveStatus, new StatusButtonOpts
            {
                Icon = Icons.Save,
                SuccessLabel = "Saved",
            }))
            {
                _saveStatus = Status.Busy;
                _statusAt = ui.Time;
            }

            double age = ui.Time - _statusAt;
            if (_saveStatus == Status.Busy && age > 1.4)
            {
                _saveStatus = Status.Success;
                _statusAt = ui.Time;
            }
            else if (_saveStatus == Status.Success && age > 1.6) _saveStatus = Status.Idle;

            // The morph animates on its own clock, so the page asks for frames while it runs.
            if (_saveStatus != Status.Idle) ui.RequestRedraw();
            if (_deletedProjects > 0) ui.Muted("Project deleted");
        }

        Section(ui, "Reactions", "Counts roll when they change.");
        ui.Reactions("reactions", _reactions);

        Section(ui, "Theme", "A compact switcher and a single toggle that morphs between sun and moon.");
        using (ui.Row(new Box { Gap = 16, Wrap = true }))
        {
            ui.ThemeSwitcher(ref _themeMode, true);
            ui.ThemeToggle();
        }

        Section(ui, "Design tokens",
            "ui.Style is the live theme: a radius and a text multiplier reshape every control below, " +
            "and CUI's own defaults come back when they are set back to them.");
        using (ui.Box(new Box { Dir = Dir.Grid, Columns = 2, MinColumnWidth = 220, Gap = 16 }))
        {
            ui.Slider("Corner radius", ref _tokenRadius, 0, 22, new SliderOpts { ShowValue = true, Format = "%.0f pt" });
            ui.Slider("Text scale", ref _tokenScale, 0.85f, 1.4f, new SliderOpts { ShowValue = true, Format = "%.2fx" });
        }

        // Assigned every frame: a token reaches the theme CUI draws with on the next one, so there
        // is no state to invalidate and no draw call to make after it.
        ThemeStyle style = ui.Style;
        style.RadiusControl = _tokenRadius;
        style.RadiusPanel = _tokenRadius + 4;
        style.RadiusSurface = _tokenRadius + 8;
        style.FontScale = _tokenScale;

        using (ui.Row(new Box { Gap = 8, Wrap = true, PadTop = 8 }))
        {
            ui.Button("Primary", new ButtonOpts { Variant = ButtonVariant.Primary });
            ui.Button("Secondary");
            ui.Input("token-sample", ref _tokenSample, new InputOpts { Placeholder = "An input follows the radius" });
            ui.Badge("Badge");
        }

        Section(ui, "User menu", "Account trigger for sidebars and headers.");
        using (ui.Box(new Box { Width = Size.Px(260) }))
        {
            using (var menu = ui.UserMenu("Maya Okafor", new UserMenuOpts
            {
                Detail = "maya@fieldwork.studio",
                Status = "online",
                Placement = Placement.BottomStart,
            }))
            {
                if (menu.Open)
                {
                    ui.MenuItem("Profile", new MenuItemOpts { Icon = Icons.User, Shortcut = "Ctrl P" });
                    ui.MenuItem("Billing", new MenuItemOpts { Icon = Icons.CreditCard });
                    ui.MenuItem("Settings", new MenuItemOpts { Icon = Icons.Settings, Shortcut = "Ctrl ," });
                    ui.MenuSeparator();
                    ui.MenuItem("Log out", new MenuItemOpts { Icon = Icons.LogOut });
                }
            }
        }

        Section(ui, "Swipe actions", "Drag a row sideways or swipe with two fingers.");
        using (ui.Card(new Card { Pad = 6, Gap = 6 }))
        {
            for (int i = 0; i < 3; i++)
            {
                _swipeRow = i;
                using (ui.Id(i))
                {
                    int a = ui.SwipeItem("row", SwipeLeft, SwipeRight, _swipeRowBody ??= SwipeRowBody);
                    if (a >= 0)
                    {
                        ui.Toast(a == 0 ? "Marked as done" : a == 100 ? "Archived" : "Deleted", new ToastOpts
                        {
                            Tone = a == 101 ? Tone.Danger : Tone.Success,
                        });
                    }
                }
            }
        }

        Section(ui, "Speed dial", "A floating action button that fans out related actions.");
        using (ui.Row(new Box { Gap = 16, Align = Align.Center, Height = Size.Px(80) }))
        {
            int c = ui.SpeedDial("dial", DialActions, new SpeedDialOpts { Tooltip = "Create" });
            if (c >= 0) _dialChoice = c;
            if (_dialChoice >= 0) ui.Muted(DialActions[_dialChoice].Label);
        }
    }

    // The swipe row body runs through a DrawFn, so it is a fixed method reading one
    // field, not a closure per row per frame.
    private int _swipeRow;
    private DrawFn? _swipeRowBody;

    private void SwipeRowBody(Ui ui)
    {
        ui.Icon(Icons.Circle, 16, Color.TextMuted);
        ui.Text(SwipeTitles[_swipeRow], new TextOpts { Width = Size.Fill, NoWrap = true });
        ui.Text("Today", new TextOpts { Size = 12, Color = Color.TextMuted });
    }
}
