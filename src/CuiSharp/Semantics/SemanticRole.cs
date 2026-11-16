namespace CuiSharp;

/// <summary>
/// What a recorded node is, independent of the wrapper that drew it: the vocabulary an
/// accessibility adapter, an agent or a test can group by.
/// </summary>
/// <remarks>
/// <see cref="SemanticNode.Kind"/> is the exact wrapper (<c>IconButton</c>, <c>DataGrid</c>);
/// the role is the smaller set of meanings those wrappers share. A custom control that records
/// itself with <see cref="WidgetRecorder.Note(nint, string, UiText)"/> keeps its own kind and
/// gets <see cref="Other"/> unless it maps to one of the common names.
/// </remarks>
public enum SemanticRole
{
    None,
    Frame,
    Container,
    Button,
    Link,
    CheckBox,
    RadioButton,
    Switch,
    TextBox,
    Slider,
    SpinButton,
    ComboBox,
    TabList,
    Tab,
    List,
    ListItem,
    Menu,
    MenuItem,
    Dialog,
    Tooltip,
    Table,
    TableRow,
    TableCell,
    Tree,
    TreeItem,
    Image,
    Text,
    Heading,
    ProgressBar,
    Separator,
    Other,
}

/// <summary>What is true of a control in the frame that recorded it.</summary>
[Flags]
public enum SemanticState
{
    /// <summary>Nothing is known or nothing applies.</summary>
    None = 0,

    /// <summary>The control accepts input; absent when a <c>ui.Disabled</c> scope drew it.</summary>
    Enabled = 1,

    /// <summary>The control gained keyboard focus in this frame.</summary>
    Focused = 2,

    /// <summary>A checkbox, switch or toggle is on.</summary>
    Checked = 4,

    /// <summary>A tab, row, chip or segmented item is selected.</summary>
    Selected = 8,

    /// <summary>A disclosure, menu, popover or picker is open.</summary>
    Expanded = 16,

    /// <summary>The value can be read but not edited.</summary>
    ReadOnly = 32,
}

/// <summary>The role names people and scripts type, e.g. <c>role:button</c>.</summary>
public static class SemanticRoles
{
    /// <summary>The lowercase spelling a query and a detail line use.</summary>
    public static string Name(SemanticRole role) => role.ToString().ToLowerInvariant();

    /// <summary>Reads a role name without case sensitivity; <c>check-box</c> and <c>checkbox</c> agree.</summary>
    public static bool TryParse(string? text, out SemanticRole role)
    {
        role = SemanticRole.None;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string wanted = Normalize(text);
        foreach (SemanticRole candidate in Enum.GetValues<SemanticRole>())
        {
            if (candidate == SemanticRole.None) continue;
            if (Normalize(candidate.ToString()).Equals(wanted, StringComparison.OrdinalIgnoreCase))
            {
                role = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>The role a wrapper kind belongs to. Exact wrapper meanings win over prefixes.</summary>
    public static SemanticRole Of(string kind) => kind switch
    {
        "Frame" => SemanticRole.Frame,
        "Card" or "Box" or "Row" or "Column" or "Scroll" or "Split" or "Dockspace" or "DockPanel" or
        "Inspector" or "InspectorGroup" or "Accordion" or "ExpandableCard" or "Reasoning" or
        "Toolbar" or "StatusBar" or "DialogFooter" or "Panel" or "Header" or "Footer" or "Page" => SemanticRole.Container,

        "Button" or "IconButton" or "MenuButton" or "SplitButton" or "ConfirmButton" or "StatusButton" or
        "HoldButton" or "CopyButton" or "ToolbarButton" or "SpeedDial" or "ControlTile" or "Chip" => SemanticRole.Button,
        "Link" or "TocAnchor" => SemanticRole.Link,
        "Checkbox" or "Checklist" or "ToggleGroup" or "PropBool" => SemanticRole.CheckBox,
        "Radio" or "RadioGroup" or "RadioCards" => SemanticRole.RadioButton,
        "Switch" => SemanticRole.Switch,

        "Input" or "TextArea" or "Search" or "TagInput" or "OtpInput" or "InlineEdit" or
        "PropText" or "ChatComposer" or "FilterBar" or "Comment" or "CodeEditor" => SemanticRole.TextBox,
        "Number" or "MoneyInput" or "Scrub" or "SpinButton" or "PropFloat" or "PropInt" => SemanticRole.SpinButton,
        "Slider" or "RangeSlider" or "RulerSlider" or "HistogramSlider" or "Knob" or "Fader" or
        "LevelMeter" or "Rating" => SemanticRole.Slider,
        "Select" or "MultiSelect" or "Combobox" or "DatePicker" or "TimePicker" or "WheelPicker" or
        "TimeDial" or "DateRangePicker" or "RangeCalendar" or "MonthCalendar" or "Calendar" or
        "ColorPicker" or "PropColor" or "PropEnum" or "ModelPicker" or "EmojiPicker" or "SchedulePicker" => SemanticRole.ComboBox,

        "Tabs" or "TabBar" or "Segmented" or "DocTabs" => SemanticRole.TabList,
        "Tab" or "Segment" => SemanticRole.Tab,
        "List" or "VirtualList" => SemanticRole.List,
        "Tree" or "TreeView" or "TreeTable" => SemanticRole.Tree,
        "ListItem" or "NavItem" => SemanticRole.ListItem,
        "Menu" or "MenuBar" or "MenuBarMenu" or "UserMenu" or "PathBarMenu" or "ContextMenu" or "CommandPalette" => SemanticRole.Menu,
        "MenuItem" or "MenuLabel" => SemanticRole.MenuItem,
        "Dialog" or "Sheet" or "Alert" or "Banner" or "Toast" or "Popover" or "ConfirmPopover" or "HoverCard" or "Tour" => SemanticRole.Dialog,
        "Tooltip" => SemanticRole.Tooltip,
        "Table" or "DataGrid" => SemanticRole.Table,
        "TableRow" or "DataGridRow" => SemanticRole.TableRow,
        "TableCell" or "TableCellText" or "DataGridCellText" or "TreeCell" => SemanticRole.TableCell,
        "TreeTableName" => SemanticRole.TreeItem,
        "Image" or "Avatar" or "Emoji" or "ImageViewer" or "Cropper" => SemanticRole.Image,
        "Heading" => SemanticRole.Heading,
        "Text" or "Label" or "Secondary" or "Muted" or "Code" or "Kbd" or "Markdown" or
        "CodeBlock" or "JsonView" or "ChatMessage" or "Badge" or "StatusBarText" or "StatusBarItem" or
        "Readout" or "DotDisplay" or "Metric" or "EmptyState" or "ExpandableText" or "AgentStatus" => SemanticRole.Text,
        "Progress" or "ProgressRing" or "Meter" or "Gauge" or "RadialGauge" or
        "StatusBarProgress" or "Skeleton" or "Spinner" => SemanticRole.ProgressBar,
        "Separator" or "Divider" or "Spacer" or "Gap" or "ToolbarSeparator" or "ChatDivider" => SemanticRole.Separator,
        _ => SemanticRole.Other,
    };

    private static string Normalize(string text)
    {
        Span<char> buffer = stackalloc char[text.Length];
        int length = 0;
        foreach (char ch in text)
        {
            if (ch is '-' or '_' or ' ') continue;
            buffer[length++] = ch;
        }

        return new string(buffer[..length]);
    }
}

/// <summary>
/// One node of a frame reduced to what a platform accessibility tree needs: a role, a name, an
/// optional value, its state, an optional stable automation id, its bounds and its children.
/// </summary>
/// <remarks>
/// This is the seam a UIA, AT-SPI or NSAccessibility adapter consumes. The platform layers are
/// not implemented yet, but a tree that carries these fields can be projected without asking the
/// app to draw again or reaching into CUI's private state.
/// </remarks>
public sealed record SemanticAccessibilityNode(
    SemanticRole Role,
    string Name,
    string? Value,
    SemanticState State,
    string? AutomationId,
    RectF Bounds,
    IReadOnlyList<SemanticAccessibilityNode> Children);

/// <summary>The memorable spelling of <see cref="SemanticState"/> flags.</summary>
public static class SemanticStates
{
    /// <summary><c>enabled, checked, focused</c>, in a stable order.</summary>
    public static string Format(SemanticState state)
    {
        if (state == SemanticState.None) return "none";

        var parts = new List<string>(4);
        Add(SemanticState.Enabled, "enabled");
        Add(SemanticState.Focused, "focused");
        Add(SemanticState.Checked, "checked");
        Add(SemanticState.Selected, "selected");
        Add(SemanticState.Expanded, "expanded");
        Add(SemanticState.ReadOnly, "read-only");
        return string.Join(", ", parts);

        void Add(SemanticState flag, string name)
        {
            if ((state & flag) != 0) parts.Add(name);
        }
    }

}
