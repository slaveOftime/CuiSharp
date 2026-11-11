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

        using var app = TestApp.Start(new LayoutPage(), 420, 480);

        // The layout view is additive: the plain tree still names the same nodes in the same
        // nesting, and a control still outranks a heading that repeats its text.
        Assert.Contains("Column \"root\"", app.Print(), StringComparison.Ordinal);
        Assert.Contains("Button \"Go\"", app.Print(), StringComparison.Ordinal);
        Assert.Equal("Button", app.Tree.Find("Go").Kind);
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
