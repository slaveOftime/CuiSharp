#:package CuiSharp@0.4.1

// The properties below are what make `dotnet publish disk.cs -c Release -o .`
// leave one small native executable beside this file, with no .NET on the target.
#:property PublishAot=true
#:property OptimizationPreference=Size
#:property InvariantGlobalization=true
#:property StripSymbols=true
#:property DebugType=none
#:property OutputType=WinExe
#:property ApplicationIcon=disk.ico
#:property EventSourceSupport=false
#:property HttpActivityPropagationSupport=false
#:property MetadataUpdaterSupport=false
#:property UseSystemResourceKeys=true
#:property StackTraceSupport=false
#:property MetricsSupport=false

using System.Diagnostics;
using System.Globalization;
using CuiSharp;

// ---------------------------------------------------------------------------
// Disk Usage — a native CuiSharp treemap over the file system.
//
//   run      dotnet run disk.cs                        the folder the terminal is in
//            dotnet run disk.cs -- C:\Windows C:\Program Files
//   ship     dotnet publish disk.cs -c Release -o .     one disk.exe, no runtime needed
//            (add -p:CuiSharpHeadless=true to publish a build `dotnet cui` can drive)
//
//   iterate  CUISHARP_HEADLESS=1 ./disk.exe             no window; serves the tool's protocol
//            dotnet cui tree                            the controls the last frame drew
//            dotnet cui click Up                        click one, print the frame it produced
//            dotnet cui png shot.png                    that frame as a PNG
//            dotnet cui docs read live-debugging        the guide, offline
//            dotnet cui session < steps.txt             many clicks against one app instance
//
// Below: the window, its sections, the scan, and the model it fills.
// ---------------------------------------------------------------------------

var targets = Arguments.Existing(args);

var start = targets.Length switch
{
    0 => Location.Folder(Environment.CurrentDirectory),
    1 when Directory.Exists(targets[0]) => Location.Folder(targets[0]),
    _ => Location.Group(targets),
};

var scanner = new DiskScanner();
var browser = new Browser(scanner, start);
var app = new DiskApp(browser, scanner);

return CuiApp.Run(app.Options, app.Draw);

// ---------------------------------------------------------------------------
// The window. Map and list side by side on a wide screen; one scrolling column
// on a narrow one.
// ---------------------------------------------------------------------------

internal sealed class DiskApp(Browser browser, DiskScanner scanner)
{
    private const float WideBreakpoint = 1000f;

    private readonly MapPanel _map = new();
    private readonly FolderPicker _picker = new();

    public CuiAppOptions Options { get; } = new()
    {
        Title = "Disk Usage",
        Width = 1360,
        Height = 920,
        Theme = Theme.Dark,
        Accent = Accent.Blue,
        Renderer = Renderer.Software,
    };

    public void Draw(Ui ui)
    {
        SquareCorners(ui);

        var result = scanner.Current;
        var (width, height) = ui.ViewportSize();

        if (width >= WideBreakpoint) DrawWide(ui, result, width, height);
        else DrawNarrow(ui, result, height);

        _picker.Draw(ui, browser);

        // Five frames a second while sizes are still arriving, one otherwise.
        ui.RequestWakeup(scanner.IsBusy ? 0.2f : 1f);
    }

    private static void SquareCorners(Ui ui)
    {
        var theme = ui.Style;
        theme.RadiusPanel = 0;
        theme.RadiusControl = 0;
        theme.RadiusSurface = 0;
    }

    private void DrawWide(Ui ui, ScanResult? result, float width, float height)
    {
        var listWidth = Math.Clamp(width * 0.28f, 320, 420);
        var mapHeight = Math.Max(280, height - 380);

        using (ui.Column(new Box { Pad = 12, Gap = 12, Height = Size.Fill }))
        {
            HeaderView.Draw(ui, browser, _picker, result, wide: true);
            PathView.Draw(ui, browser);
            SummaryView.Draw(ui, result, wide: true);

            using (ui.Row(new Box { Gap = 12, Width = Size.Fill, Height = Size.Fill, Align = Align.Stretch }))
            {
                _map.Draw(ui, result, browser, wide: true, mapHeight);
                RankingPanel.Draw(ui, result, browser, Size.Fill, Size.Px(listWidth));
            }

            FooterView.Draw(ui, result, wide: true);
        }
    }

    private void DrawNarrow(Ui ui, ScanResult? result, float height)
    {
        var mapHeight = Math.Clamp(height * 0.40f, 240, 420);

        using (ui.Scroll(new ScrollOpts { Height = Size.Fill, Pad = 12, Gap = 12 }))
        using (ui.Column(new Box { Gap = 12 }))
        {
            HeaderView.Draw(ui, browser, _picker, result, wide: false);
            PathView.Draw(ui, browser);
            SummaryView.Draw(ui, result, wide: false);
            _map.Draw(ui, result, browser, wide: false, mapHeight);
            RankingPanel.Draw(ui, result, browser, Size.Px(440), Size.Fill);
            FooterView.Draw(ui, result, wide: false);
        }
    }
}

// ---------------------------------------------------------------------------
// Where the view is and how it got there.
// ---------------------------------------------------------------------------

internal sealed class Browser
{
    private readonly DiskScanner _scanner;
    private readonly Stack<Location> _history = new();

    public Browser(DiskScanner scanner, Location start)
    {
        _scanner = scanner;
        Current = start;
        _scanner.Scan(start);
    }

    public Location Current { get; private set; }
    public bool CanGoBack => _history.Count > 0;

    public void Open(Location location)
    {
        _history.Push(Current);
        Current = location;
        _scanner.Scan(location);
    }

    public void Back()
    {
        if (_history.Count == 0) return;

        Current = _history.Pop();
        _scanner.Scan(Current);
    }

    public void Up()
    {
        if (Current.Parent() is { } parent) Open(parent);
    }

    public void Refresh() => _scanner.Scan(Current);

    public void OpenPath(string path)
    {
        if (Directory.Exists(path)) Open(Location.Folder(path));
        else if (File.Exists(path)) Open(Location.Group([path]));
    }
}

// ---------------------------------------------------------------------------
// Header: what is being measured, and the four things you can do about it.
// ---------------------------------------------------------------------------

internal static class HeaderView
{
    public static void Draw(Ui ui, Browser browser, FolderPicker picker, ScanResult? result, bool wide)
    {
        if (wide)
        {
            using (ui.Row(new Box { Align = Align.Center, Gap = 12, Wrap = true }))
            {
                DrawTitle(ui, browser, result);
                ui.Spacer();
                DrawControls(ui, browser, picker, result);
            }
        }
        else
        {
            using (ui.Column(new Box { Gap = 10 }))
            {
                DrawTitle(ui, browser, result);

                using (ui.Row(new Box { Align = Align.Center, Gap = 8, Wrap = true }))
                    DrawControls(ui, browser, picker, result);
            }
        }
    }

    private static void DrawTitle(Ui ui, Browser browser, ScanResult? result)
    {
        using (ui.Row(new Box { Gap = 10, Align = Align.Center }))
        {
            ui.Text(browser.Current.Title, new TextOpts { Color = Color.Accent, Size = 32, Font = Font.Medium });
            using (ui.Column(new Box { Gap = 2 }))
            {
                ui.Text("DISK USAGE", new TextOpts
                {
                    Font = Font.Medium,
                    Size = 10,
                    Color = Color.Accent,
                });
                ui.Badge(Subtitle(result), new BadgeOpts
                {
                    Tone = Tone.Neutral,
                    Size = ControlSize.Sm,
                });
            }
        }
    }

    private static string Subtitle(ScanResult? result) => result switch
    {
        null => "reading…",
        { Error: { } error } => error,
        { Complete: false } => "scanning…",
        var done => $"{Format.Size(done.Total)} · {Format.Count(done.Count)} items",
    };

    private static void DrawControls(Ui ui, Browser browser, FolderPicker picker, ScanResult? result)
    {
        var busy = result is { Complete: false };

        if (ui.Button("Back", new ButtonOpts
        {
            Icon = Icons.ArrowLeft,
            Size = ControlSize.Sm,
            Variant = ButtonVariant.Secondary,
            Disabled = !browser.CanGoBack,
        }))
        {
            browser.Back();
        }

        if (ui.Button("Up", new ButtonOpts
        {
            Icon = Icons.ArrowUp,
            Size = ControlSize.Sm,
            Variant = ButtonVariant.Secondary,
            Disabled = browser.Current.IsGroup,
        }))
        {
            browser.Up();
        }

        if (ui.Button("Rescan", new ButtonOpts
        {
            Icon = Icons.RefreshCw,
            Size = ControlSize.Sm,
            Variant = busy ? ButtonVariant.Secondary : ButtonVariant.Default,
            Loading = busy,
        }))
        {
            browser.Refresh();
        }

        if (ui.Button("Choose folder", new ButtonOpts
        {
            Icon = Icons.FolderOpen,
            Size = ControlSize.Sm,
            Variant = ButtonVariant.Secondary,
        }))
        {
            picker.Request();
        }
    }
}

// ---------------------------------------------------------------------------
// The folder dialog. Drawn by CUI inside the frame rather than opened as an OS
// window: the system dialog runs its own message loop on top of the one
// drawing this frame, which deadlocks the app (AppHang) before it can close.
// ---------------------------------------------------------------------------

internal sealed class FolderPicker
{
    private bool _open;
    private string _path = "";

    /// <summary>Called from the button; the app draws the dialog at the end of the frame.</summary>
    public void Request() => _open = true;

    public void Draw(Ui ui, Browser browser)
    {
        if (!_open) return;

        var confirmed = ui.FilePicker("folder-picker", ref _open, ref _path, 1024, new FilePickerOpts
        {
            Folders = true,
            Title = "Choose a folder",
            Start = Start(browser.Current),
        });

        if (confirmed) browser.OpenPath(_path);
    }

    /// <summary>A group of paths has no folder of its own; start where its first path lives.</summary>
    private static string? Start(Location location) => location.IsGroup
        ? Path.GetDirectoryName(location.Paths[0])
        : location.Paths[0];
}

// ---------------------------------------------------------------------------
// Breadcrumb. Click a segment to go there, or type a path (Ctrl+L).
// ---------------------------------------------------------------------------

internal static class PathView
{
    public static void Draw(Ui ui, Browser browser)
    {
        var location = browser.Current;

        if (location.IsGroup)
        {
            ui.Muted("Grouped: " + string.Join(" · ", location.Paths));
            return;
        }

        var (labels, paths) = PathText.Segments(location.Paths[0]);

        var bar = ui.PathBar("path", labels, new PathBarOpts
        {
            Size = ControlSize.Sm,
            Icon = Icons.Folder,
            Placeholder = "Type a path",
        });

        if (bar.Clicked >= 0 && bar.Clicked < paths.Length)
            browser.Open(Location.Folder(paths[bar.Clicked]));
        else if (bar.Submitted is { Length: > 0 } typed)
            browser.OpenPath(typed);
    }
}

// ---------------------------------------------------------------------------
// Summary: the four numbers worth knowing before you look at the map.
// ---------------------------------------------------------------------------

internal static class SummaryView
{
    public static void Draw(Ui ui, ScanResult? result, bool wide)
    {
        if (result is null) return;

        if (wide) DrawRow(ui, result);
        else DrawGrid(ui, result);
    }

    private static void DrawRow(Ui ui, ScanResult result)
    {
        using (ui.Card(new Card { PadY = 12, PadX = 0.1f, Dir = Dir.Row, Gap = 0 }))
        {
            ui.Stat("Total size", Format.Size(result.Total), StatusDetail(result));
            ui.VLine();
            ui.Stat("Items", Format.Count(result.Count), Breakdown(result));
            ui.VLine();
            ui.Stat("Largest", LargestValue(result), LargestDetail(result));
            ui.VLine();
            ui.Stat("Free space", FreeValue(result), FreeDetail(result));
        }
    }

    private static void DrawGrid(Ui ui, ScanResult result)
    {
        using (ui.Box(new Box { Dir = Dir.Grid, Columns = 2, MinColumnWidth = 190, Gap = 10 }))
        {
            ui.StatCard("Total size", Format.Size(result.Total), StatusDetail(result));
            ui.StatCard("Items", Format.Count(result.Count), Breakdown(result));
            ui.StatCard("Largest", LargestValue(result), LargestDetail(result));
            ui.StatCard("Free space", FreeValue(result), FreeDetail(result));
        }
    }

    private static string StatusDetail(ScanResult result) => result switch
    {
        { Error: { } error } => error,
        { Complete: false } => "scanning…",
        var done => $"scanned in {done.Elapsed.TotalSeconds:0.0} s",
    };

    private static string Breakdown(ScanResult result) =>
        $"{Format.Count(result.Files)} files · {Format.Count(result.Folders)} folders";

    private static string LargestValue(ScanResult result) =>
        result.Entries.Length == 0 ? "—" : Format.Size(result.Entries[0].Size);

    private static string LargestDetail(ScanResult result) =>
        result.Entries.Length == 0 ? "nothing measured yet" : result.Entries[0].Name;

    private static string FreeValue(ScanResult result) =>
        Volume(result) is { } volume ? Format.Size(volume.Available) : "—";

    private static string FreeDetail(ScanResult result) =>
        Volume(result) is { } volume
            ? $"{Format.Size(volume.Total)} on the volume"
            : "not a single folder";

    private static VolumeInfo? Volume(ScanResult result)
    {
        if (result.Location.IsGroup) return null;

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(result.Location.Paths[0]) ?? "");
            return drive.IsReady ? new(drive.AvailableFreeSpace, drive.TotalSize) : null;
        }
        catch
        {
            return null;
        }
    }
}

internal readonly record struct VolumeInfo(long Available, long Total);

// ---------------------------------------------------------------------------
// Map: rectangles whose area is size. Click a folder to go in.
// ---------------------------------------------------------------------------

internal sealed class MapPanel
{
    private VizItem[] _items = [];
    private string _format = "%.0f bytes";

    public void Draw(Ui ui, ScanResult? result, Browser browser, bool wide, float height)
    {
        using (ui.Card(new Card
        {
            Width = wide ? Size.Fill : Size.Fit,
            Height = wide ? Size.Fill : Size.Fit,
            Pad = 12,
            Gap = 12,
            Dir = Dir.Column,
        }))
        {
            DrawHeading(ui, result);

            if (result is null)
            {
                DrawLoading(ui, height);
                return;
            }

            if (result.Entries.Length == 0 || result.Total <= 0)
            {
                DrawEmpty(ui, result, height);
                return;
            }

            Fill(result);

            var clicked = ui.Treemap("map", _items, new TreemapOpts { Height = height, Format = _format });
            if (clicked >= 0 && clicked < result.Entries.Length)
            {
                var entry = result.Entries[clicked];
                if (entry.IsDirectory) browser.Open(Location.Folder(entry.Path));
            }

            DrawCaption(ui, result, wide);
        }
    }

    private static void DrawHeading(Ui ui, ScanResult? result)
    {
        using (ui.Row(new Box { Align = Align.Center, Gap = 10, Wrap = true }))
        {
            using (ui.Column(new Box { Gap = 3 }))
            {
                ui.Text("Treemap", new TextOpts { Font = Font.Medium, Size = 15 });
                ui.Muted("Area is size · click a folder to open it");
            }

            ui.Spacer();

            ui.Badge(result is null ? "—" : Format.Size(result.Total), new BadgeOpts
            {
                Tone = Tone.Neutral,
                Size = ControlSize.Sm,
            });
        }
    }

    private static void DrawLoading(Ui ui, float height)
    {
        using (ui.Row(new Box { Height = height, Align = Align.Center, Justify = Justify.Center, Gap = 10 }))
        {
            ui.Spinner(18);
            ui.Muted("Reading the folder...");
        }
    }

    private static void DrawEmpty(Ui ui, ScanResult result, float height)
    {
        using (ui.Box(new Box { Height = height, Align = Align.Center, Justify = Justify.Center }))
        {
            ui.EmptyState(result.Error is null ? "Nothing to measure" : "Cannot read this folder", new EmptyOpts
            {
                Description = result.Error
                    ?? (result.Complete ? "The folder is empty." : "Sizes are still arriving."),
                Icon = Icons.Folder,
            });
        }
    }

    private void Fill(ScanResult result)
    {
        var largest = Math.Max(result.Entries[0].Size, result.OtherBytes);
        var (divisor, format) = UnitFor(largest);
        _format = format;

        var count = result.Entries.Length + (result.OtherCount > 0 ? 1 : 0);
        if (_items.Length != count) _items = new VizItem[count];

        for (var i = 0; i < result.Entries.Length; i++)
        {
            var entry = result.Entries[i];
            _items[i] = new VizItem(Label(entry), entry.Size / divisor, Palette.ColorOf(i, entry.IsDirectory));
        }

        if (result.OtherCount > 0)
        {
            _items[^1] = new VizItem(
                $"{Format.Count(result.OtherCount)} smaller items",
                result.OtherBytes / divisor,
                Palette.Other);
        }
    }

    private static string Label(DiskEntry entry) => entry switch
    {
        { IsDirectory: true, Files: > 0 } => $"{entry.Name} · {Format.Count(entry.Files)} files",
        { IsDirectory: true } => entry.Name,
        _ => $"{entry.Name} · File",
    };

    private static (float Divisor, string Format) UnitFor(long bytes) => bytes switch
    {
        >= 1L << 40 => ((float)(1L << 40), "%.4g TB"),
        >= 1L << 30 => ((float)(1L << 30), "%.4g GB"),
        >= 1L << 20 => ((float)(1L << 20), "%.4g MB"),
        >= 1L << 10 => ((float)(1L << 10), "%.4g KB"),
        _ => (1f, "%.0f bytes"),
    };

    private static void DrawCaption(Ui ui, ScanResult result, bool wide)
    {
        var left = $"{Format.Count(result.Count)} items · {Format.Size(result.Total)}";
        var right = result.Complete ? "scan complete" : "sizes still arriving";

        if (wide)
        {
            using (ui.Row(new Box { Align = Align.Center, Gap = 10 }))
            {
                ui.Muted(left + " · hover to inspect");
                ui.Spacer();
                ui.Muted(right);
            }
        }
        else
        {
            using (ui.Column(new Box { Gap = 2 }))
            {
                ui.Muted(left);
                ui.Muted(right);
            }
        }
    }
}

// ---------------------------------------------------------------------------
// Ranking: the same entries as a table, largest first.
// ---------------------------------------------------------------------------

internal static class RankingPanel
{
    public static void Draw(Ui ui, ScanResult? result, Browser browser, Size height, Size width)
    {
        using (ui.Card(new Card
        {
            Width = width,
            Height = height,
            Pad = 12,
            Dir = Dir.Column,
        }))
        {
            using (ui.Column(new Box { Gap = 2 }))
            {
                ui.Text("Largest items", new TextOpts { Font = Font.Medium, Size = 15 });
                ui.Muted("Click a folder to open it");
            }

            ui.Separator();

            if (result is null || result.Entries.Length == 0)
            {
                DrawEmpty(ui, result);
                return;
            }

            using (ui.Scroll(new ScrollOpts { Height = Size.Fill, Width = Size.Fill, Pad = 0, Gap = 0 }))
            using (ui.Column(new Box { Gap = 4, Width = Size.Fill }))
            {
                for (var i = 0; i < result.Entries.Length; i++)
                {
                    var entry = result.Entries[i];
                    var share = result.Total > 0 ? entry.Size * 100f / result.Total : 0;

                    if (ui.SizeRow(i + 1, entry, Palette.ColorOf(i, entry.IsDirectory), share) && entry.IsDirectory)
                        browser.Open(Location.Folder(entry.Path));
                }
            }
        }
    }

    private static void DrawEmpty(Ui ui, ScanResult? result)
    {
        using (ui.Box(new Box { Height = Size.Fill, Align = Align.Center, Justify = Justify.Center }))
        {
            ui.EmptyState(result is null ? "Reading the folder" : "No entries", new EmptyOpts
            {
                Description = result is null ? "The scan has not started yet." : "Nothing to list here.",
            });
        }
    }
}

// ---------------------------------------------------------------------------
// Footer: what was measured, and how long it took.
// ---------------------------------------------------------------------------

internal static class FooterView
{
    public static void Draw(Ui ui, ScanResult? result, bool wide)
    {
        var left = result is null ? "" : Describe(result.Location);
        var right = result switch
        {
            null => "",
            { Error: { } error } => error,
            { Complete: false } => "scanning…",
            var done => $"scanned in {done.Elapsed.TotalSeconds:0.0} s",
        };

        if (wide)
        {
            using (ui.Row(new Box { Align = Align.Center, Gap = 10 }))
            {
                ui.Muted(left);
                ui.Spacer();
                if (right.Length > 0) ui.FooterTime(right);
            }
        }
        else
        {
            using (ui.Column(new Box { Gap = 3 }))
            {
                ui.Muted(left);
                if (right.Length > 0) ui.FooterTime(right);
            }
        }
    }

    private static string Describe(Location location) =>
        location.IsGroup
            ? $"{location.Paths.Length} paths grouped"
            : location.Paths[0];
}

// ---------------------------------------------------------------------------
// The pieces the sections share.
// ---------------------------------------------------------------------------

internal static class UiExtensions
{
    public static void Stat(this Ui ui, string label, string value, string detail)
    {
        using (ui.Column(new Box { Width = Size.Fill, PadX = 14, Gap = 2 }))
            ui.StatText(label, value, detail);
    }

    public static void StatCard(this Ui ui, string label, string value, string detail)
    {
        using (ui.Card(new Card { MinWidth = 190, PadX = 14, PadY = 10 }))
        using (ui.Column(new Box { Gap = 2 }))
            ui.StatText(label, value, detail);
    }

    public static void VLine(this Ui ui)
    {
        using (ui.Box(new Box
        {
            Width = Size.Px(1),
            Height = Size.Px(42),
            Bg = Color.Border,
        })) { }
    }

    public static void FooterTime(this Ui ui, string text)
    {
        ui.Text(text, new TextOpts
        {
            Size = 11,
            Tabular = true,
            Color = Color.TextMuted,
        });
    }

    public static bool SizeRow(this Ui ui, int rank, DiskEntry entry, Color color, float share)
    {
        using (ui.Card(new Card
        {
            Id = "row:" + entry.Path,
            Hoverable = true,
            Width = Size.Fill,
            PadX = 10,
            PadY = 5,
            Dir = Dir.Row,
            Gap = 9,
            Align = Align.Center,
        }, out var clicked))
        {
            ui.Text(rank.ToString(CultureInfo.InvariantCulture), new TextOpts
            {
                Size = 11,
                Tabular = true,
                Align = TextAlign.Right,
                Color = Color.TextMuted,
                Width = Size.Px(18),
            });

            using (ui.Box(new Box
            {
                Width = Size.Px(8),
                Height = Size.Px(8),
                Radius = 4,
                Bg = color,
            })) { }

            using (ui.Column(new Box { Width = Size.Fill, MinWidth = 0, Gap = 1 }))
            {
                ui.Text(entry.Name, new TextOpts
                {
                    Font = Font.Medium,
                    Size = 13,
                    NoWrap = true,
                    Color = Color.Foreground,
                });

                ui.Text(Detail(entry), new TextOpts
                {
                    Size = 11,
                    NoWrap = true,
                    Color = Color.TextSecondary,
                });
            }

            using (ui.Column(new Box { Width = Size.Px(84), Gap = 1, Align = Align.End }))
            {
                ui.Text(Format.Size(entry.Size), new TextOpts
                {
                    Size = 13,
                    Font = Font.Medium,
                    Tabular = true,
                    Align = TextAlign.Right,
                    Color = Color.Foreground,
                });

                ui.Text(share.ToString("0.0", CultureInfo.InvariantCulture) + "%", new TextOpts
                {
                    Size = 11,
                    Tabular = true,
                    Align = TextAlign.Right,
                    Color = Color.TextMuted,
                });
            }

            return clicked;
        }
    }

    private static string Detail(DiskEntry entry) => entry.IsDirectory
        ? $"Folder · {Format.Count(entry.Files)} files"
        : "File";

    private static void StatText(this Ui ui, string label, string value, string detail)
    {
        ui.Text(label, new TextOpts { Size = 11, Color = Color.TextMuted });

        ui.Text(value, new TextOpts
        {
            Font = Font.Medium,
            Size = 21,
            Tabular = true,
        });

        ui.Text(detail, new TextOpts { Size = 11, Color = Color.TextSecondary, NoWrap = true });
    }
}

// ---------------------------------------------------------------------------
// Numbers and colours.
// ---------------------------------------------------------------------------

internal static class Format
{
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.##} TB",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.##} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0.#} KB",
        1 => "1 byte",
        _ => $"{bytes} bytes",
    };

    public static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}

internal static class Palette
{
    public static readonly Color Other = Color.Oklch(0.58f, 0.02f, 250f);

    /// <summary>Folders a shade brighter than files, so the two read apart in one map.</summary>
    public static Color ColorOf(int index, bool folder)
    {
        var hue = (22f + index * 137.50776f) % 360f;
        return folder
            ? Color.Oklch(0.74f, 0.16f, hue)
            : Color.Oklch(0.62f, 0.10f, hue);
    }
}

// ---------------------------------------------------------------------------
// Model.
// ---------------------------------------------------------------------------

/// <summary>One folder, or a group of paths shown as if they were one folder.</summary>
internal sealed class Location
{
    public required string Title { get; init; }
    public required string[] Paths { get; init; }
    public required bool IsGroup { get; init; }

    public static Location Folder(string path)
    {
        var full = Path.GetFullPath(path);
        var name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        return new()
        {
            Title = name.Length > 0 ? name : full,
            Paths = [full],
            IsGroup = false,
        };
    }

    public static Location Group(IReadOnlyList<string> paths) => new()
    {
        Title = paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} selected items",
        Paths = [.. paths],
        IsGroup = true,
    };

    public Location? Parent()
    {
        if (IsGroup) return null;

        var parent = Directory.GetParent(Paths[0]);
        return parent is null ? null : Folder(parent.FullName);
    }
}

internal sealed class DiskEntry
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required bool IsDirectory { get; init; }

    // Written by the scan threads while the UI reads them. A long write is
    // atomic on the 64-bit targets this app publishes for.
    public long Size;
    public int Files;
    public int Folders;
}

/// <summary>An immutable view of the scan at one moment; replaced, never mutated.</summary>
internal sealed class ScanResult
{
    public required Location Location { get; init; }
    public DiskEntry[] Entries { get; init; } = [];
    public long Total { get; init; }
    public long OtherBytes { get; init; }
    public int OtherCount { get; init; }
    public int Files { get; init; }
    public int Folders { get; init; }
    public bool Complete { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string? Error { get; init; }

    public int Count => Entries.Length + OtherCount;
}

internal readonly record struct Measurement(long Size, int Files, int Folders);

// ---------------------------------------------------------------------------
// The scan: children first, then their sizes in parallel, published as they
// arrive so the map grows while the disk is still being read.
// ---------------------------------------------------------------------------

internal sealed class DiskScanner
{
    private const int MapEntries = 28;

    private static readonly long PublishEvery = Stopwatch.Frequency / 6;

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        RecurseSubdirectories = false,
    };

    private readonly Lock _publishGate = new();

    private ScanResult? _current;
    private CancellationTokenSource? _cancel;
    private int _generation;
    private long _publishedAt;
    private volatile bool _busy;

    public ScanResult? Current => Volatile.Read(ref _current);
    public bool IsBusy => _busy;

    public void Scan(Location location)
    {
        var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _cancel, cancellation)?.Cancel();
        var generation = Interlocked.Increment(ref _generation);

        _busy = true;
        Volatile.Write(ref _current, new() { Location = location });

        var thread = new Thread(() => Run(location, generation, cancellation.Token))
        {
            IsBackground = true,
            Name = "disk-scan",
        };

        thread.Start();
    }

    private void Run(Location location, int generation, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var entries = new List<DiskEntry>(256);

        try
        {
            foreach (var path in location.Paths)
            {
                token.ThrowIfCancellationRequested();

                if (Directory.Exists(path)) entries.AddRange(ReadChildren(path));
                else if (File.Exists(path)) entries.Add(ReadFile(new(path)));
            }

            Publish(location, entries, clock.Elapsed, complete: false, error: null);

            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount),
                CancellationToken = token,
            };

            Parallel.ForEach(entries.Where(static entry => entry.IsDirectory), options, entry =>
            {
                var measured = Measure(entry.Path, token);
                entry.Size = measured.Size;
                entry.Files = measured.Files;
                entry.Folders = measured.Folders;

                PublishThrottled(location, entries, clock.Elapsed);
            });

            Publish(location, entries, clock.Elapsed, complete: true, error: null);
        }
        catch (OperationCanceledException)
        {
            // Navigated away; the new scan owns the window.
        }
        catch (Exception error)
        {
            Publish(location, entries, clock.Elapsed, complete: true, error: error.Message);
        }
        finally
        {
            if (generation == Volatile.Read(ref _generation)) _busy = false;
        }
    }

    private static List<DiskEntry> ReadChildren(string folder)
    {
        var children = new List<DiskEntry>();

        foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", Options))
        {
            if (info is FileInfo file) children.Add(ReadFile(file));
            else if (info is DirectoryInfo directory) children.Add(ReadDirectory(directory));
        }

        return children;
    }

    private static DiskEntry ReadFile(FileInfo file) => new()
    {
        Name = file.Name,
        Path = file.FullName,
        IsDirectory = false,
        Size = Length(file),
        Files = 1,
    };

    private static DiskEntry ReadDirectory(DirectoryInfo directory) => new()
    {
        Name = directory.Name,
        Path = directory.FullName,
        IsDirectory = true,
    };

    private static Measurement Measure(string folder, CancellationToken token)
    {
        var size = 0L;
        var files = 0;
        var folders = 0;

        try
        {
            foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", Options))
            {
                token.ThrowIfCancellationRequested();

                if (info is FileInfo file)
                {
                    size += Length(file);
                    files++;
                }
                else if (info is DirectoryInfo directory)
                {
                    var inner = Measure(directory.FullName, token);
                    size += inner.Size;
                    files += inner.Files;
                    folders += inner.Folders + 1;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A folder that vanished or denies access keeps what it had.
        }

        return new(size, files, folders);
    }

    private static long Length(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch
        {
            return 0;
        }
    }

    private void PublishThrottled(Location location, List<DiskEntry> entries, TimeSpan elapsed)
    {
        var now = Stopwatch.GetTimestamp();
        var last = Volatile.Read(ref _publishedAt);

        if (now - last < PublishEvery) return;
        if (Interlocked.CompareExchange(ref _publishedAt, now, last) != last) return;

        Publish(location, entries, elapsed, complete: false, error: null);
    }

    private void Publish(Location location, List<DiskEntry> entries, TimeSpan elapsed, bool complete, string? error)
    {
        var sorted = entries.ToArray();
        Array.Sort(sorted, static (a, b) => b.Size.CompareTo(a.Size));

        var total = 0L;
        var files = 0;
        var folders = 0;

        foreach (var entry in sorted)
        {
            total += entry.Size;

            if (entry.IsDirectory) folders += entry.Folders + 1;
            else files += entry.Files;
        }

        var shown = Math.Min(sorted.Length, MapEntries);
        var otherBytes = 0L;

        for (var i = shown; i < sorted.Length; i++) otherBytes += sorted[i].Size;

        var result = new ScanResult
        {
            Location = location,
            Entries = sorted[..shown],
            Total = total,
            OtherBytes = otherBytes,
            OtherCount = sorted.Length - shown,
            Files = files,
            Folders = folders,
            Complete = complete,
            Elapsed = elapsed,
            Error = error,
        };

        lock (_publishGate) Volatile.Write(ref _current, result);
    }
}

// ---------------------------------------------------------------------------
// Arguments and paths.
// ---------------------------------------------------------------------------

internal static class Arguments
{
    /// <summary>The paths that exist, made absolute and de-duplicated.</summary>
    public static string[] Existing(string[] args)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new List<string>(args.Length);

        foreach (var argument in args)
        {
            if (AbsolutePath(argument) is not { } full) continue;
            if (!Directory.Exists(full) && !File.Exists(full)) continue;
            if (seen.Add(full)) paths.Add(full);
        }

        return [.. paths];
    }

    private static string? AbsolutePath(string argument)
    {
        try
        {
            return Path.GetFullPath(argument);
        }
        catch
        {
            return null;
        }
    }
}

internal static class PathText
{
    /// <summary>One label and one full path per segment, so a click can jump to it.</summary>
    public static (string[] Labels, string[] Paths) Segments(string path)
    {
        var normal = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var root = Path.GetPathRoot(normal) ?? normal;

        var labels = new List<string> { Trim(root) };
        var paths = new List<string> { root };

        var current = root;

        foreach (var part in normal[root.Length..].Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            labels.Add(part);
            paths.Add(current);
        }

        return ([.. labels], [.. paths]);
    }

    private static string Trim(string root)
    {
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length > 0 ? trimmed : root;
    }
}
