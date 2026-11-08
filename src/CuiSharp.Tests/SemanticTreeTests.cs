using CuiSharp.Hosting;
using CuiSharp.Testing;

namespace CuiSharp.Tests;

/// <summary>
/// The recorded tree: a test (or an agent) reads which controls a frame drew and how they nest,
/// so these tests assert on the shape rather than on the numbers inside it.
/// </summary>
public class SemanticTreeTests
{
    private sealed class Editor : Component
    {
        public int Saves;

        protected override void Draw(Ui ui)
        {
            using (ui.Card(new() { Id = "editor", Width = Size.Fill, Pad = 12, Gap = 8 }))
            {
                ui.Heading("Note", 3);
                ui.Input("draft", ref Draft, new InputOpts { AutoFocus = true });
                using (ui.Row(new() { Gap = 8 }))
                {
                    if (ui.Button("Save")) Saves++;
                    ui.Button("Discard");
                }
            }
        }

        public string Draft = "";
    }

    private sealed class AnonymousColumns : Component
    {
        protected override void Draw(Ui ui)
        {
            // No id anywhere: nothing to name a container by, so the button is the only node.
            using (ui.Column(new() { Width = Size.Fill, Pad = 8 }))
            {
                using (ui.Column(new() { Gap = 4 }))
                {
                    ui.Button("Save");
                }
            }
        }
    }

    [Fact]
    public void ControlsNestTheWayTheAppDrewThem()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = TestApp.Start(new Editor(), 600, 400);

        string tree = app.Print();
        Assert.Equal(
            """
            Frame
              Card "editor"
                Heading "Note"
                Input "draft"
                Button "Save"
                Button "Discard"
            """.ReplaceLineEndings("\n"),
            tree.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void ACardIsFoundByItsIdAndItsControlsByPath()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = TestApp.Start(new Editor(), 600, 400);

        SemanticNode editor = app.Tree.Find("editor");
        Assert.Equal("Card", editor.Kind);
        Assert.Equal("Card \"editor\" > Button \"Save\"", editor.Find("Save").Path);

        // The card's own rectangle encloses the button inside it: the nesting is real, not
        // just bookkeeping.
        SemanticNode save = editor.Find("Save");
        Assert.InRange(save.Rect.X, editor.Rect.X, editor.Rect.X + editor.Rect.W);
        Assert.InRange(save.Rect.Y, editor.Rect.Y, editor.Rect.Y + editor.Rect.H);
        // Two containers are open around it - the card and the row the buttons share - even
        // though only the card is named and so only the card is a node.
        Assert.Equal(2, save.Depth);
    }

    private sealed class Confirm : Component
    {
        public bool Open = true;

        protected override void Draw(Ui ui)
        {
            // Kept open for the whole draw: a dropped scope closes the dialog immediately.
            using var dialog = ui.Dialog("confirm", ref Open);
            if (!dialog) return;
            ui.Label("Are you sure?");
            if (ui.Button("Delete")) Open = false;
        }
    }

    [Fact]
    public void ADialogsControlsAreInsideTheDialog()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = TestApp.Start(new Confirm(), 600, 400);

        // A dialog is opened by its title, not by an option: the title is the node's name.
        SemanticNode dialog = app.Tree.Find("confirm");
        Assert.Equal("Dialog", dialog.Kind);
        SemanticNode delete = dialog.Find("Delete");
        Assert.Equal("Button", delete.Kind);
        Assert.Equal("Dialog \"confirm\" > Button \"Delete\"", delete.Path);
    }

    [Fact]
    public void AContainerWithNoNameDoesNotInventANode()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = TestApp.Start(new AnonymousColumns(), 400, 200);

        // Two unnamed columns and one button: the tree says exactly that much.
        Assert.Equal("Frame\n  Button \"Save\"", app.Print().ReplaceLineEndings("\n"));
        // The depth still records the nesting the app wrote.
        Assert.Equal(2, app.Tree.Find("Save").Depth);
    }

    [Fact]
    public void FindingSomethingThatIsNotTherePrintsTheTree()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = TestApp.Start(new Editor(), 600, 400);

        var error = Assert.Throws<InvalidOperationException>(() => app.Tree.Find("Export"));
        Assert.Contains("Card \"editor\"", error.Message, StringComparison.Ordinal);
        Assert.Contains("Input \"draft\"", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTreeOfAFrameDescribesThatFrameOnly()
    {
        if (!OperatingSystem.IsWindows()) return;

        bool draws = true;
        using var app = TestApp.Start(ui =>
        {
            if (draws) ui.Button("Save");
        }, 400, 200);
        Assert.Contains(app.Tree.Descendants(), n => n.Text == "Save");

        // A frame that draws nothing leaves nothing to find.
        draws = false;
        app.Frame();
        Assert.Empty(app.Tree.Children);
        Assert.Equal("Frame", app.Print());
    }

    /// <summary>
    /// A container ends where its scope ends, not where the next container begins: a heading
    /// drawn after a table has closed belongs to whatever holds the table.
    /// </summary>
    private sealed class CardThenTable : Component
    {
        private static readonly TableColumn[] Columns = [new("Name", 120)];

        protected override void Draw(Ui ui)
        {
            using (ui.Column(new() { Gap = 8 }))
            {
                using (ui.Table("rows", Columns, new TableOpts { Height = 80 }))
                {
                    ui.TableRow(false);
                    ui.TableCellText("NVDA");
                }

                ui.Heading("After the table", 4);
            }
        }
    }

    [Fact]
    public void AControlDrawnAfterAClosedTableIsNotInsideIt()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = TestApp.Start(new CardThenTable(), 400, 260);

        SemanticNode heading = app.Tree.Find("After the table");
        Assert.Equal("Frame", heading.Parent!.Kind);
        Assert.Equal("Table", app.Tree.Find("Table \"rows\"").Kind);
    }

    /// <summary>A tab strip and a segmented control are containers of clickable items.</summary>
    private sealed class TabsAndSegments : Component
    {
        public int Page;
        public int Mode;

        protected override void Draw(Ui ui)
        {
            string[] pages = ["Todos", "Markets"];
            string[] modes = ["Light", "Dark"];
            ui.Tabs("pages", pages, ref Page);

            // The pattern a theme switch is: the control decides, the frame assigns.
            if (ui.Segmented("theme", modes, ref Mode)) ui.Theme = Mode == 1 ? Theme.Dark : Theme.Light;
            ui.Secondary(ui.Theme == Theme.Dark ? "dark" : "light");
        }
    }

    [Fact]
    public void TheItemsOfATabStripAreControlsOfTheirOwn()
    {
        if (!OperatingSystem.IsWindows()) return;

        var page = new TabsAndSegments();
        using var app = TestApp.Start(page, 500, 220);

        // The strip is a container holding one control per item, so an item can be named.
        Assert.Equal(
            """
            Frame
              Tabs "pages"
                Tab "Todos"
                Tab "Markets"
              Segmented "theme"
                Segment "Light"
                Segment "Dark"
              Secondary "light"
            """,
            app.Print().ReplaceLineEndings("\n"));

        app.Find("Tab", "Markets").ClickAndFrame();
        Assert.Equal(1, page.Page);
    }

    [Fact]
    public void ClickingASegmentSwitchesWhatTheAppDraws()
    {
        if (!OperatingSystem.IsWindows()) return;

        var page = new TabsAndSegments();
        using var app = TestApp.Start(page, 500, 220);

        // The item's id is derived the way CUI derives it, so the click lands on the segment and
        // the widget reports it. A port of that hash that drifts fails here, not in production.
        app.Find("Segment", "Dark").ClickAndFrame();
        Assert.Equal(1, page.Mode);
        Assert.Equal(Theme.Dark, app.Surface.Ui.Theme);

        // ...and the frame that drew it says so.
        app.Frame();
        Assert.Contains(app.Tree.Descendants(), n => n.Text == "dark");
    }

    [Fact]
    public void RecordingAFrameCostsTheDrawNothing()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(420, 260);
        surface.RecordWidgets = true;
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        var page = new Editor();
        DrawFn draw = page.Render;

        // Recording reads a widget's text when the frame ends, not while it is drawn, so what a
        // footer reports (the app's own draw) stays at zero even while a frame is being recorded.
        for (var i = 0; i < 3; i++) surface.Draw(draw, pixels);

        // Five batches, median: the first batch of any surface can still carry a warm-up.
        var batches = new long[5];
        for (var b = 0; b < batches.Length; b++)
        {
            long reported = 0;
            for (var i = 0; i < 10; i++)
            {
                surface.Draw(draw, pixels);
                reported += surface.LastFrame.AllocatedBytes;
            }
            batches[b] = reported;
        }

        // The snapshot itself is data that outlives the frame, so it allocates — the frame does not.
        Assert.Equal(0, batches.Median());
        Assert.NotEmpty(surface.Widgets);
        Assert.Empty(surface.Faults);
    }
}
