using System.Globalization;
using System.Text;
using CuiSharp.Hosting;
using CuiSharp.Testing;

namespace CuiSharp.Tests;

/// <summary>
/// The layout view: what it prints must be exactly what CUI proved, and everything it cannot
/// prove — true scroll extents, virtualized content, unnamed containers, text-only widgets —
/// must be printed as an unknown, never as a number that looks like an answer.
/// </summary>
public class LayoutInspectionTests
{
    private int Counter;

    private sealed class LayoutPage : Component
    {
        protected override void Draw(Ui ui)
        {
            using (ui.Column(new() { Id = "root", Width = Size.Fill, Height = Size.Fill, Pad = 12, Gap = 8 }))
            {
                if (ui.Button("Go")) Clicks++;
                ui.Heading("Title", 2);
                ui.Button("Second");
                using (ui.Scroll(new() { Id = "area", Height = Size.Px(60) }))
                    ui.Button("Deep");
                using (ui.VirtualList("rows", 1000, 28, Size.Px(90), out int first, out int last))
                    for (int r = first; r < last; r++)
                        ui.ListItem($"Row {r}");
            }
        }

        public int Clicks;
    }

    private static string[] LayoutLines(TestApp app) =>
        app.PrintLayout().ReplaceLineEndings("\n").Split('\n');

    [Fact]
    public void EveryLayoutViewStartsWithItsWarning()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = TestApp.Start(new LayoutPage(), 420, 480);

        string[] lines = LayoutLines(app);
        // The disclaimer is line one, so no line below it can read as more than it is.
        Assert.Equal(LayoutFormatter.Header, lines[0]);
        Assert.Contains("lower bound", LayoutFormatter.Header, StringComparison.Ordinal);
        Assert.Contains("UNKNOWN", LayoutFormatter.Header, StringComparison.Ordinal);
        Assert.Contains("unnamed", LayoutFormatter.Header, StringComparison.Ordinal);
    }

    [Fact]
    public void ScrolledAndVirtualizedNodesReportTheirExtentAsUnknown()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = TestApp.Start(new LayoutPage(), 420, 480);

        string scroll = Assert.Single(LayoutLines(app), l => l.Contains("Scroll \"area\"", StringComparison.Ordinal));
        string list = Assert.Single(LayoutLines(app), l => l.Contains("VirtualList \"rows\"", StringComparison.Ordinal));
        Assert.Contains("extent:UNKNOWN", scroll, StringComparison.Ordinal);
        Assert.Contains("extent:UNKNOWN", list, StringComparison.Ordinal);

        // The named scroll's own viewport rect is still verified — it is the extent, not the
        // viewport, that is unknown — and it is the scroll area's own, not a borrowed one.
        SemanticNode area = app.Tree.Find("area");
        Assert.Contains($"[{area.Rect}]", scroll, StringComparison.Ordinal);
        Assert.NotEqual(app.Tree.Find("Second").Rect, area.Rect);
    }

    [Fact]
    public void ATextOnlyWidgetHasNoVerifiedRect()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = TestApp.Start(new LayoutPage(), 420, 480);

        // A heading after an action widget records under the button's id, so its rect is
        // someone else's and the view says so instead of printing it as a fact.
        string heading = Assert.Single(LayoutLines(app), l => l.Contains("Heading \"Title\"", StringComparison.Ordinal));
        Assert.Contains("[rect:unverified]", heading, StringComparison.Ordinal);
        Assert.DoesNotContain($"[{app.Tree.Find("Title").Rect}]", heading, StringComparison.Ordinal);
    }

    [Fact]
    public void ObservedBoundsAreTheUnionOfTheVerifiedRectsAndSaySo()
    {
        if (!OperatingSystem.IsWindows()) return;

        // A page small enough for the union to be hand-checked: two buttons in a named box.
        using var app = TestApp.Start(ui =>
        {
            using (ui.Column(new() { Id = "pair", Width = Size.Fill, Pad = 10, Gap = 6 }))
            {
                ui.Button("A");
                ui.Button("B");
            }
        }, 300, 200);

        RectF a = app.Tree.Find("A").Rect;
        RectF b = app.Tree.Find("B").Rect;
        float x = MathF.Min(a.X, b.X), y = MathF.Min(a.Y, b.Y);
        float right = MathF.Max(a.X + a.W, b.X + b.W), bottom = MathF.Max(a.Y + a.H, b.Y + b.H);
        var expected = new RectF(x, y, right - x, bottom - y);

        string pair = Assert.Single(
            app.PrintLayout().ReplaceLineEndings("\n").Split('\n'),
            l => l.Contains("Column \"pair\"", StringComparison.Ordinal));
        Assert.Contains($"union≥[{expected}] (lower-bound, recorded rects)", pair, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTreeAndItsRankingAreNotRewrittenByTheLayoutView()
    {
        if (!OperatingSystem.IsWindows()) return;

        // A frame whose heading repeats the button's label: the page is as ambiguous as the
        // ranking rule was built for.
        using var app = TestApp.Start(ui =>
        {
            ui.Heading("Save");
            if (ui.Button("Save")) Counter++;
            using (ui.Column(new() { Id = "pair", Width = Size.Fill }))
                ui.Button("Later");
        }, 400, 300);

        string before = app.Print();
        string beforeTree = app.Tree.Print(rects: true);
        string layout = app.PrintLayout();

        // The layout view is additive: after printing it, the plain tree is the same string it
        // was before, down to the rects, and the view did print something of its own.
        Assert.Equal(before, app.Print());
        Assert.Equal(beforeTree, app.Tree.Print(rects: true));
        Assert.Contains("Column \"pair\"", layout, StringComparison.Ordinal);
        Assert.StartsWith(LayoutFormatter.Header, layout, StringComparison.Ordinal);

        // And the query still ranks what can be acted on above the heading that repeats it.
        Assert.Equal("Button", app.Tree.Find("Save").Kind);
        app.Find("Save").ClickAndFrame();
        Assert.Equal(1, Counter);
    }

    [Fact]
    public void RecordingOnAndOffDrawTheIdenticalFrame()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(400, 300);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        var captured = new byte[pixels.Length];
        DrawFn draw = ui =>
        {
            ui.Heading("Title", 2);
            ui.Button("Go");
            using (ui.Column(new() { Id = "panel", Width = Size.Fill, Pad = 8 }))
                ui.Button("Inside");
        };

        // The same surface, the same callback, the same frame time, recording toggled between
        // draws: the picture must not be able to tell whether it was watched.
        surface.RecordWidgets = false;
        surface.Draw(draw, pixels, PixelFormat.Rgba);
        surface.Render(captured, PixelFormat.Rgba);
        byte[] off = [.. captured];

        surface.RecordWidgets = true;
        surface.Draw(draw, pixels, PixelFormat.Rgba);
        surface.Render(captured, PixelFormat.Rgba);
        byte[] on = [.. captured];

        Assert.Contains(on, b => b != 255);   // dark text pixels: something was actually painted
        Assert.Equal(off, on);

        // Through another off/on cycle the frame is still itself, and the recorder really did
        // have content on the frames that were watched.
        surface.RecordWidgets = false;
        surface.Draw(draw, pixels, PixelFormat.Rgba);
        surface.Render(captured, PixelFormat.Rgba);
        Assert.True(off.SequenceEqual(captured));
        surface.RecordWidgets = true;
        surface.Draw(draw, pixels, PixelFormat.Rgba);
        surface.Render(captured, PixelFormat.Rgba);
        Assert.True(off.SequenceEqual(captured));
        Assert.Contains(surface.SemanticFrame, w => w.Kind == "Column" && w.Text == "panel");
    }

    [Fact]
    public void TheSameInputOnSurfacesWatchedAndUnwatchedEndsIdentical()
    {
        if (!OperatingSystem.IsWindows()) return;

        var watchedPage = new AButton();
        var plainPage = new AButton();
        using var watched = TestApp.Start(watchedPage, 400, 300);
        using var plain = TestApp.Start(plainPage, 400, 300);
        plain.Surface.RecordWidgets = false;

        // The script is fixed by the first frame's geometry, not by each run's tree: both
        // surfaces get the identical event sequence regardless of what they record.
        RectF button = watched.Tree.Find("Go").Rect;
        byte[] watchedBefore = [.. watched.RawPixels];
        Drive(watched, button);
        byte[] plainBefore = [.. plain.RawPixels];
        Drive(plain, button);

        // The click is real and observable, not an id lookup: the model moved, the frame
        // changed with it, and the watched surface ends on exactly the plain surface's pixels.
        Assert.Equal(1, watchedPage.Clicks);
        Assert.Equal(1, plainPage.Clicks);
        Assert.False(watchedBefore.SequenceEqual(watched.RawPixels.ToArray()));
        Assert.False(plainBefore.SequenceEqual(plain.RawPixels.ToArray()));
        Assert.True(watched.RawPixels.SequenceEqual(plain.RawPixels));
        Assert.Empty(watched.Faults);
        Assert.Empty(plain.Faults);
    }

    private static void Drive(HeadlessApp app, RectF target)
    {
        app.MoveTo(target.X + target.W / 2f, target.Y + target.H / 2f);
        app.Press();
        app.Release();
        app.Frame();
    }

    private sealed class AButton : Component
    {
        public int Clicks;

        protected override void Draw(Ui ui)
        {
            using (ui.Column(new() { Width = Size.Fill, Pad = 16, Gap = 8 }))
            {
                ui.Label($"clicks {Clicks}");
                if (ui.Button("Go")) Clicks++;
            }
        }
    }

    [Fact]
    public void TheHeadlessWireAnswersLayoutWithTheHeaderView()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = TestApp.Start(new LayoutPage(), 420, 480);
        string answer = Wire(app, "layout");
        Assert.StartsWith(LayoutFormatter.Header, answer, StringComparison.Ordinal);
        Assert.Contains("extent:UNKNOWN", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void NamedVirtualAndSplitContainersDoNotBorrowThePreviousWidgetsRectangle()
    {
        uint rowsId = 0, panesId = 0;
        float ratio = 0.5f;
        using var app = TestApp.Start(ui =>
        {
            ui.Button("before");
            using (ui.Id(42))
            {
                rowsId = ui.MakeId("rows");
                using (ui.VirtualList("rows", 50, 24, Size.Px(100), out int first, out int last))
                    for (int i = first; i < last; i++) ui.ListItem($"row {i}");
                panesId = ui.MakeId("panes");
                using (ui.Split("panes", ref ratio, new SplitOpts { Height = Size.Px(80) }))
                {
                    ui.Button("Left");
                    ui.SplitNext();
                    ui.Button("Right");
                }
            }
        }, 400, 400);

        SemanticNode rows = app.Tree.Find("rows");
        SemanticNode panes = app.Tree.Find("panes");
        Assert.Equal(rowsId, rows.Id);
        Assert.Equal(panesId, panes.Id);
        Assert.NotEqual(app.Tree.Find("before").Id, rows.Id);
        Assert.Equal(100, rows.Rect.H);
        Assert.Equal(80, panes.Rect.H);
        Assert.True(panes.Rect.W > 240);
    }

    [Fact]
    public void ObservedChildrenBeyondAFixedContainerAreMarkedAsOverflow()
    {
        using var app = TestApp.Start(ui =>
        {
            using (ui.Column(new Box { Id = "clip", Height = Size.Px(40), Clip = true }))
            {
                ui.Button("A");
                ui.Gap(24);
                ui.Button("B");
            }
        }, 300, 200);
        string line = Assert.Single(LayoutLines(app), l => l.Contains("Column \"clip\"", StringComparison.Ordinal));
        Assert.Contains("OVERFLOW(observed bounds)", line, StringComparison.Ordinal);
        Assert.True(app.Tree.Find("B").Rect.Y + app.Tree.Find("B").Rect.H > app.Tree.Find("clip").Rect.Y + 40);
    }

    [Fact]
    public void AUnicodeLayoutPayloadRoundTripsExactlyByItsByteCount()
    {
        using var app = TestApp.Start(ui =>
        {
            using (ui.Column(new Box { Id = "布局" })) ui.Button("保存");
        }, 300, 200);
        Assert.Equal(app.PrintLayout(), Wire(app, "layout"));
    }

    [Fact]
    public void AContainerThatOnlyBorrowsThePreviousIdPrintsItsRectAsUnverified()
    {
        if (!OperatingSystem.IsWindows()) return;

        // cui_table_begin never publishes an id of its own, so after a button the table
        // records under the button's id. The layout view must not sell that as the table's,
        // and the column's bounds must rest on the button alone.
        int clicks = 0;
        using var app = TestApp.Start(ui =>
        {
            TableColumn[] columns = [new("Name", Size.Fill)];
            using (ui.Column(new() { Id = "host", Width = Size.Fill }))
            {
                if (ui.Button("Mark")) clicks++;
                using (ui.Table("grid", columns))
                {
                }
            }
        }, 400, 300);

        string[] lines = LayoutLines(app);
        SemanticNode table = app.Tree.Find("grid");
        Assert.False(table.HasOwnRect);
        // The borrowed id is real, so the honest label is unverified, not none.
        Assert.NotEqual(0u, table.Id);

        string tableLine = Assert.Single(lines, l => l.Contains("Table \"grid\"", StringComparison.Ordinal));
        Assert.Contains("[rect:unverified]", tableLine, StringComparison.Ordinal);

        string hostLine = Assert.Single(lines, l => l.Contains("Column \"host\"", StringComparison.Ordinal));
        Assert.Contains($"union≥[{app.Tree.Find("Mark").Rect}] (lower-bound", hostLine, StringComparison.Ordinal);
        Assert.DoesNotContain("OVERFLOW", hostLine, StringComparison.Ordinal);

        // A button's own rect is proven, and the same frame still answers queries and clicks.
        string buttonLine = Assert.Single(lines, l => l.Contains("Button \"Mark\"", StringComparison.Ordinal));
        Assert.DoesNotContain("rect:", buttonLine, StringComparison.Ordinal);
        app.Find("Mark").ClickAndFrame();
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void AFloatingDialogIsNotCountedIntoTheColumnThatDrawsIt()
    {
        if (!OperatingSystem.IsWindows()) return;

        bool open = true;
        using var app = TestApp.Start(ui =>
        {
            using (ui.Column(new() { Id = "host", Width = Size.Fill, Height = Size.Px(60), Pad = 4 }))
            {
                ui.Button("Inside");
                using (ui.Dialog("peek", ref open))
                {
                    ui.Button("Deep");
                    using (ui.DialogFooter())
                        ui.Button("Ok");
                }
            }
        }, 400, 300);

        string[] lines = LayoutLines(app);

        // Floating is not invisible: the dialog prints where the app drew it, marked as
        // floating, still holding its own children's bounds.
        string dialogLine = Assert.Single(lines, l => l.Contains("Dialog \"peek\"", StringComparison.Ordinal));
        Assert.Contains("(floating)", dialogLine, StringComparison.Ordinal);
        Assert.Contains("union≥[", dialogLine, StringComparison.Ordinal);
        Assert.Contains("Button \"Ok\"", string.Join('\n', lines), StringComparison.Ordinal);

        // The nesting the app wrote is untouched; only the geometry attribution changed.
        // One tree snapshot: app.Tree rebuilds the tree on every access.
        SemanticNode snapshot = app.Tree;
        SemanticNode host = snapshot.Find("host");
        SemanticNode dialog = snapshot.Find("peek");
        Assert.Equal(host, dialog.Parent);
        Assert.True(dialog.Detached);
        Assert.False(host.Detached);

        // The dialog's buttons are laid out on its own full-screen layer, far outside a
        // 60-point column: that must not become the column's union or its overflow.
        string hostLine = Assert.Single(lines, l => l.Contains("Column \"host\"", StringComparison.Ordinal));
        Assert.Contains($"union≥[{app.Tree.Find("Inside").Rect}] (lower-bound", hostLine, StringComparison.Ordinal);
        Assert.DoesNotContain("OVERFLOW", hostLine, StringComparison.Ordinal);
    }

    [Fact]
    public void TheKindsProvenToPublishTheirOwnIdPrintTheirOwnRects()
    {
        if (!OperatingSystem.IsWindows()) return;

        bool check = true, sw = true, chip = true;
        int radio = 1, segment = 0;
        using var app = TestApp.Start(ui =>
        {
            using (ui.Card(new() { Id = "proven" }))
            {
                ui.Button("B");
                ui.Checkbox("C", ref check);
                ui.Switch("S", ref sw);
                ui.Radio("R", ref radio, 1);
                ui.Chip("H", ref chip);
                ui.Link("L");
                ui.Segmented("G", ["one", "two"], ref segment);
            }
        }, 400, 300);

        string[] lines = LayoutLines(app);
        (string Kind, string Text)[] proven =
        [
            ("Card", "proven"), ("Button", "B"), ("Checkbox", "C"), ("Switch", "S"),
            ("Radio", "R"), ("Chip", "H"), ("Link", "L"), ("Segmented", "G"),
        ];
        foreach ((string kind, string text) in proven)
        {
            string line = Assert.Single(lines, l => l.TrimStart().StartsWith($"{kind} \"{text}\"", StringComparison.Ordinal));
            Assert.DoesNotContain("rect:", line, StringComparison.Ordinal);
            Assert.Contains($"[{app.Tree.Find(text).Rect}]", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AWidgetThatPublishesAPartIdIsNotGivenAnOwnedRect()
    {
        if (!OperatingSystem.IsWindows()) return;

        // cui_slider_ex lays the slider out under one id and leaves the track leaf's id as
        // last_id: the recorded id is real and inside the slider, but it is not the slider's
        // own layout id, so the view prints doubt instead of a part's rect for the whole.
        float v = 0.5f;
        using var app = TestApp.Start(ui =>
        {
            ui.Button("Ref");
            ui.Slider("vol", ref v, 0f, 1f);
        }, 400, 300);

        string slider = Assert.Single(LayoutLines(app), l => l.Contains("Slider \"vol\"", StringComparison.Ordinal));
        Assert.Contains("[rect:unverified]", slider, StringComparison.Ordinal);
        // Unverified because of provenance, not because no id was found: the track's id is real.
        Assert.NotEqual(0u, app.Tree.Find("vol").Id);
    }

    [Fact]
    public void AnEmptyGroupThatBuiltNothingBorrowsThePreviousRect()
    {
        if (!OperatingSystem.IsWindows()) return;

        // cui__segments and cui_toggle_group_ex return before creating a node or publishing
        // an id when given no items, so an empty group records under whoever drew before it.
        // Only the strip native actually built may claim its rect.
        bool[] none = [];
        using var app = TestApp.Start(ui =>
        {
            int segIndex = 0;
            ui.Button("Prior");
            using (ui.Column(new() { Id = "host", Width = Size.Fill }))
            {
                ui.Segmented("seg", [], ref segIndex);
                ui.ToggleGroup("tg", [], none);
                ui.Segmented("full", ["one", "two"], ref segIndex);
            }
        }, 400, 300);

        string[] lines = LayoutLines(app);
        SemanticNode snapshot = app.Tree;
        uint prior = snapshot.Find("Prior").Id;

        SemanticNode seg = snapshot.Find("seg");
        SemanticNode tg = snapshot.Find("tg");
        // The borrowed id is the button's, unchanged: real, and not theirs.
        Assert.Equal(prior, seg.Id);
        Assert.Equal(prior, tg.Id);
        Assert.False(seg.HasOwnRect);
        Assert.False(tg.HasOwnRect);
        Assert.Contains("[rect:unverified]", LineFor(lines, "Segmented \"seg\""), StringComparison.Ordinal);
        Assert.Contains("[rect:unverified]", LineFor(lines, "ToggleGroup \"tg\""), StringComparison.Ordinal);

        // The nonempty strip built its node and published its id: it owns its rect again.
        SemanticNode full = snapshot.Find("full");
        Assert.True(full.HasOwnRect);
        Assert.NotEqual(prior, full.Id);
        string fullLine = LineFor(lines, "Segmented \"full\"");
        Assert.DoesNotContain("rect:", fullLine, StringComparison.Ordinal);
        Assert.Contains($"[{full.Rect}]", fullLine, StringComparison.Ordinal);

        // The column's bounds rest on the one thing it truly holds, not on the button drawn
        // outside it that the empty groups borrowed.
        string hostLine = LineFor(lines, "Column \"host\"");
        Assert.Contains($"union≥[{full.Rect}] (lower-bound", hostLine, StringComparison.Ordinal);
        Assert.DoesNotContain("OVERFLOW", hostLine, StringComparison.Ordinal);
    }

    [Fact]
    public void AGroupThatSharesADrawnWidgetsIdStillBorrowsIt()
    {
        if (!OperatingSystem.IsWindows()) return;

        // cui_make_id hashes only the "###id" part of a name, so a group that was never built can
        // find its very own hash already sitting in last_id, left there by somebody else. An id
        // is not evidence that this call drew a widget; the count the wrapper handed native is.
        bool[] none = [];
        bool[] two = [true, false];
        string[] labels = ["one", "two"];
        using var app = TestApp.Start(ui =>
        {
            ui.Button("Open###shared");
            using (ui.Column(new() { Id = "host", Width = Size.Fill, Height = Size.Px(120), Pad = 4 }))
            {
                ui.ToggleGroup("Empty###shared", [], none);
                ui.ToggleGroup("Built", labels, two);
            }
        }, 400, 300);

        string[] lines = LayoutLines(app);
        SemanticNode snapshot = app.Tree;
        SemanticNode prior = snapshot.Find("Open###shared");
        SemanticNode empty = snapshot.Find("Empty###shared");
        SemanticNode built = snapshot.Find("Built");

        // The collision is exact, as cui_make_id intends: same id, same cached rect, one widget.
        Assert.Equal(prior.Id, empty.Id);
        Assert.Equal(prior.Rect, empty.Rect);
        Assert.True(prior.Rect.W > 0 && prior.Rect.H > 0);

        // The button built itself; the group with no labels did not, and cannot borrow it.
        Assert.True(prior.HasOwnRect);
        Assert.False(empty.HasOwnRect);
        Assert.Contains("[rect:unverified]", LineFor(lines, "ToggleGroup \"Empty###shared\""), StringComparison.Ordinal);

        // Same name shape, real labels: the group that built its row keeps its own rect.
        Assert.True(built.HasOwnRect);
        Assert.DoesNotContain("rect:", LineFor(lines, "ToggleGroup \"Built\""), StringComparison.Ordinal);

        // The column holds exactly the one group that exists: the button outside it, whose rect
        // the empty group shares, is not in its bounds and does not make it overflow.
        Assert.NotEqual(built.Rect, prior.Rect);
        string hostLine = LineFor(lines, "Column \"host\"");
        Assert.Contains($"union≥[{built.Rect}] (lower-bound", hostLine, StringComparison.Ordinal);
        Assert.DoesNotContain("OVERFLOW", hostLine, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOpenedUserMenuStaysOffTheColumnsBooks()
    {
        if (!OperatingSystem.IsWindows()) return;

        // cui_user_menu_begin (cui_actions.c) draws a trigger in the flow, then opens its
        // body through cui__popup_begin: the body belongs to the popup's layer, not to the
        // column the app drew the trigger inside.
        RectF trigger = default;
        using var app = TestApp.Start(ui =>
        {
            using (ui.Column(new() { Id = "host", Width = Size.Px(140), Height = Size.Px(60), Pad = 4 }))
            {
                ui.Button("Body");
                // The trigger's own id, the same derivation the native widget uses.
                trigger = ui.RectOf(ui.MakeId("me") ^ 0x05E4u);
                using (ui.UserMenu("me"))
                    ui.Button("Sign out");
            }
        }, 400, 300);

        // One more frame: RectOf reads the frame before, so the trigger has a rect to aim at.
        app.Frame();
        Assert.True(trigger.W > 0 && trigger.H > 0);

        // Hover, press, release: cui_user_menu_begin toggles on the click, so the release
        // frame is the first frame whose body draws.
        app.MoveTo(trigger.X + trigger.W / 2f, trigger.Y + trigger.H / 2f);
        app.Press();
        app.Release();

        string[] lines = LayoutLines(app);
        Assert.Contains("(floating)", LineFor(lines, "UserMenu \"me\""), StringComparison.Ordinal);

        SemanticNode snapshot = app.Tree;
        SemanticNode signOut = snapshot.Find("Sign out");
        SemanticNode host = snapshot.Find("host");
        Assert.True(signOut.HasOwnRect);
        // The popup landed below the 60-point column, so counting it would have moved the
        // column's union: the exact union is the proof it did not.
        Assert.True(signOut.Rect.Y >= host.Rect.Y + host.Rect.H);
        string hostLine = LineFor(lines, "Column \"host\"");
        Assert.Contains($"union≥[{snapshot.Find("Body").Rect}] (lower-bound", hostLine, StringComparison.Ordinal);
        Assert.DoesNotContain("OVERFLOW", hostLine, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOpenedMenuBarMenuStaysOffTheHostsBounds()
    {
        if (!OperatingSystem.IsWindows()) return;

        // cui_menu_bar_menu_begin (cui_chrome.c) opens its menu through cui__popup_begin.
        RectF trigger = default;
        using var app = TestApp.Start(ui =>
        {
            using (ui.Column(new() { Id = "host", Width = Size.Px(240), Height = Size.Px(120), Pad = 4 }))
            {
                using (ui.MenuBar("mb"))
                {
                    // Inside the bar's id scope, MakeId derives the trigger's id exactly as
                    // cui_menu_bar_menu_begin does.
                    trigger = ui.RectOf(ui.MakeId("File"));
                    using (ui.MenuBarMenu("File"))
                        ui.Button("New file");
                }

                ui.Button("Body");
            }
        }, 400, 300);

        app.Frame();
        Assert.True(trigger.W > 0 && trigger.H > 0);
        app.MoveTo(trigger.X + trigger.W / 2f, trigger.Y + trigger.H / 2f);
        app.Press();
        app.Release();

        string[] lines = LayoutLines(app);
        Assert.Contains("(floating)", LineFor(lines, "MenuBarMenu \"File\""), StringComparison.Ordinal);

        SemanticNode snapshot = app.Tree;
        SemanticNode newFile = snapshot.Find("New file");
        Assert.True(newFile.HasOwnRect);
        Assert.NotEqual(snapshot.Find("Body").Rect, newFile.Rect);

        // Only the in-flow button counts toward the column; the bar itself stays unverified,
        // and the popup's verified button must not join either.
        string hostLine = LineFor(lines, "Column \"host\"");
        Assert.Contains($"union≥[{snapshot.Find("Body").Rect}] (lower-bound", hostLine, StringComparison.Ordinal);
        Assert.DoesNotContain("OVERFLOW", hostLine, StringComparison.Ordinal);
    }

    [Fact]
    public void FloatingClassificationFollowsNativeLayersNotLabels()
    {
        // Every kind here was read out of the pinned native source at a cui__float_begin or
        // cui__popup_begin call site; MenuBarMenu, UserMenu and PathBarMenu are the scopes
        // whose body a popup carries though their trigger sits in the flow.
        string[] nativeLayers =
        [
            "Dialog", "Sheet", "Popover", "Menu", "ContextMenu", "HoverCard",
            "Toast", "Tooltip", "Tour", "MenuBarMenu", "UserMenu", "PathBarMenu",
        ];
        foreach (string kind in nativeLayers)
            Assert.True(GeometryProvenance.IsFloating(kind), $"{kind} draws on a cui__float_begin layer");

        // The things around a floating layer stay on the caller's layer: the bar that owns a
        // trigger, the footer inside a dialog, the item inside a menu, and the commands that
        // only poke a popup's open flag.
        foreach (string kind in new[] { "MenuBar", "DialogFooter", "MenuItem", "MenuLabel", "PopoverOpen", "PathBar", "Button", "Column" })
            Assert.False(GeometryProvenance.IsFloating(kind), $"{kind} is drawn in the flow");
    }

    [Fact]
    public void UnnamedFloatingScopesKeepTheirContentOffTheHostsBounds()
    {
        if (!OperatingSystem.IsWindows()) return;

        // An unnamed dialog and an unnamed popover record no node of their own, but native
        // still draws their bodies on layers of their own: the recorder must carry that
        // boundary, or their buttons would land in the column's bounds by name of nothing.
        bool open = true;
        using var app = TestApp.Start(ui =>
        {
            using (ui.Column(new() { Id = "host", Width = Size.Fill, Height = Size.Px(120), Pad = 4 }))
            {
                ui.Button("Before");
                ui.PopoverOpen(string.Empty);
                using (ui.Dialog(string.Empty, ref open))
                    ui.Button("Modal move");
                using (ui.Popover(string.Empty))
                    ui.Button("Quiet note");
                ui.Button("After");
            }
        }, 400, 300);

        string[] lines = LayoutLines(app);
        string joined = string.Join('\n', lines);
        Assert.Contains("Button \"Modal move\"", joined, StringComparison.Ordinal);
        Assert.Contains("Button \"Quiet note\"", joined, StringComparison.Ordinal);

        SemanticNode snapshot = app.Tree;
        SemanticNode modal = snapshot.Find("Modal move");
        SemanticNode note = snapshot.Find("Quiet note");
        Assert.True(modal.HasOwnRect && note.HasOwnRect);

        // Both in-flow buttons count - including the one drawn after both layers closed -
        // and nothing else does: the union is exactly their own, although the off-layer
        // buttons are real content that would have widened it.
        var expected = Union(snapshot.Find("Before").Rect, snapshot.Find("After").Rect);
        Assert.NotEqual(expected, Union(expected, modal.Rect));
        Assert.NotEqual(expected, Union(expected, note.Rect));
        string hostLine = LineFor(lines, "Column \"host\"");
        Assert.Contains($"union≥[{expected}] (lower-bound", hostLine, StringComparison.Ordinal);
        Assert.DoesNotContain("OVERFLOW", hostLine, StringComparison.Ordinal);
    }

    [Fact]
    public void ANestedPopupStaysOutOfItsDialogsOwnBounds()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Layers stack: the dialog raises one, the popover raises another, and each scope's
        // bounds are its own layer's bounds - never the layer of something drawn inside it.
        bool open = true;
        using var app = TestApp.Start(ui =>
        {
            ui.Button("Anchor");
            using (ui.Dialog("named", ref open))
            {
                ui.Button("In dialog");
                // Armed inside the dialog: the popover's id is derived under the dialog's
                // id scope, and read back under the same one.
                ui.PopoverOpen("deep");
                using (ui.Popover("deep"))
                    ui.Button("In popover");
            }
        }, 500, 400);

        string[] lines = LayoutLines(app);
        SemanticNode snapshot = app.Tree;
        SemanticNode inDialog = snapshot.Find("In dialog");
        SemanticNode inPopover = snapshot.Find("In popover");
        Assert.True(inDialog.HasOwnRect && inPopover.HasOwnRect);

        string dialogLine = LineFor(lines, "Dialog \"named\"");
        Assert.Contains("(floating)", dialogLine, StringComparison.Ordinal);
        Assert.Contains($"union≥[{inDialog.Rect}] (lower-bound", dialogLine, StringComparison.Ordinal);

        string popoverLine = LineFor(lines, "Popover \"deep\"");
        Assert.Contains("(floating)", popoverLine, StringComparison.Ordinal);
        Assert.Contains($"union≥[{inPopover.Rect}]", popoverLine, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeepUnnamedFloatingScopeKeepsItsBodyOffTheHostsBounds()
    {
        if (!OperatingSystem.IsWindows()) return;

        // CUI nests layouts as deep as its own parent stack, and the layer table is as deep, so a
        // floating scope is a boundary wherever the app happened to open it. Seventy unnamed
        // columns are pure nesting the semantic tree never sees: only the layer tells the dialog's
        // centered body apart from the column that drew it.
        bool open = true;
        using var app = TestApp.Start(ui =>
        {
            using (ui.Column(new() { Id = "host", Width = Size.Px(180), Height = Size.Px(80), Pad = 4 }))
            {
                ui.Button("Before");
                NestUnnamedColumns(ui, 70, ref open);
                ui.Button("After");
            }
        }, 400, 300);

        string[] lines = LayoutLines(app);
        Assert.Contains("Button \"Deep move\"", string.Join('\n', lines), StringComparison.Ordinal);

        SemanticNode snapshot = app.Tree;
        SemanticNode modal = snapshot.Find("Deep move");
        Assert.True(modal.HasOwnRect);

        // Both in-flow buttons count, the modal one does not - and its rect is real content that
        // would have moved the union, since the dialog is placed in the middle of the viewport.
        var expected = Union(snapshot.Find("Before").Rect, snapshot.Find("After").Rect);
        Assert.NotEqual(expected, Union(expected, modal.Rect));
        string hostLine = LineFor(lines, "Column \"host\"");
        Assert.Contains($"union≥[{expected}] (lower-bound", hostLine, StringComparison.Ordinal);
        Assert.DoesNotContain("OVERFLOW", hostLine, StringComparison.Ordinal);

        static void NestUnnamedColumns(Ui ui, int depth, ref bool open)
        {
            if (depth == 0)
            {
                using (ui.Dialog(string.Empty, ref open))
                    ui.Button("Deep move");
                return;
            }

            using (ui.Column(new() { Width = Size.Fill, Height = Size.Fill }))
                NestUnnamedColumns(ui, depth - 1, ref open);
        }
    }

    [Fact]
    public void PastNativesOwnNestingLimitNothingIsCountedIntoTheFlow()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Deeper than CUI's parent stack the recorder cannot remember which layer it was on, so
        // it claims none: what a too-deep frame draws belongs to no layer, and never to the flow
        // that enclosed it. CUI itself stops parenting here, which is why the depth is only ever
        // reached by an app that lost control of its nesting - the view loses a union, not a lie.
        bool open = true;
        using var app = TestApp.Start(ui =>
        {
            using (ui.Column(new() { Id = "host", Width = Size.Px(180), Height = Size.Px(80), Pad = 4 }))
            {
                ui.Button("Before");
                NestUnnamedColumns(ui, 300, ref open);
            }
        }, 400, 300);

        string[] lines = LayoutLines(app);
        SemanticNode snapshot = app.Tree;
        SemanticNode modal = snapshot.Find("Deep move");
        SemanticNode before = snapshot.Find("Before");

        // The dialog's button is real content, outside the host's box: counted, it would have
        // both widened the union and made the column overflow.
        Assert.True(modal.Rect.H > 0 && modal.Rect.Y > before.Rect.Y + before.Rect.H);
        string hostLine = LineFor(lines, "Column \"host\"");
        Assert.Contains($"union≥[{before.Rect}] (lower-bound", hostLine, StringComparison.Ordinal);
        Assert.DoesNotContain("OVERFLOW", hostLine, StringComparison.Ordinal);

        static void NestUnnamedColumns(Ui ui, int depth, ref bool open)
        {
            if (depth == 0)
            {
                using (ui.Dialog(string.Empty, ref open))
                    ui.Button("Deep move");
                return;
            }

            using (ui.Column(new() { Width = Size.Fill, Height = Size.Fill }))
                NestUnnamedColumns(ui, depth - 1, ref open);
        }
    }

    private static string LineFor(string[] lines, string label) =>
        Assert.Single(lines, l => l.TrimStart().StartsWith(label + " ", StringComparison.Ordinal));

    private static RectF Union(RectF a, RectF b)
    {
        float x = MathF.Min(a.X, b.X), y = MathF.Min(a.Y, b.Y);
        float right = MathF.Max(a.X + a.W, b.X + b.W), bottom = MathF.Max(a.Y + a.H, b.Y + b.H);
        return new RectF(x, y, right - x, bottom - y);
    }

    /// <summary>One command down the headless protocol, the answer payload back — the loop's own wire.</summary>
    private static string Wire(HeadlessApp app, string command)
    {
        var output = new StringWriter();
        HeadlessLoop.Run(app, new StringReader(command + "\nquit\n"), output);
        byte[] wire = Encoding.UTF8.GetBytes(output.ToString());
        int bannerEnd = Array.IndexOf(wire, (byte)'\n');
        int headerEnd = Array.IndexOf(wire, (byte)'\n', bannerEnd + 1);
        string header = Encoding.UTF8.GetString(wire, bannerEnd + 1, headerEnd - bannerEnd - 1);
        Assert.StartsWith("CUI-OK ", header, StringComparison.Ordinal);
        int length = int.Parse(header["CUI-OK ".Length..], CultureInfo.InvariantCulture);
        Assert.Equal((byte)'\n', wire[headerEnd + 1 + length]);
        return Encoding.UTF8.GetString(wire, headerEnd + 1, length);
    }
}
