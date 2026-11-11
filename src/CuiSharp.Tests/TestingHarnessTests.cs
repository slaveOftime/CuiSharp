using CuiSharp.Hosting;
using CuiSharp.Testing;

namespace CuiSharp.Tests;

/// <summary>
/// The testing harness: find a control by its label, click it, type into it, and read what the
/// frame drew. These tests are deliberately written the way an app's own UI tests would be, so
/// the harness is checked by using it rather than by inspecting it.
/// </summary>
public class TestingHarnessTests
{
    /// <summary>A page with a counter, a text field and a list: enough to exercise find, click, type.</summary>
    private sealed class Form : Component
    {
        public int Clicks;
        public string Name = "";
        public bool Accepted;
        public List<string> Items = [];

        protected override void Draw(Ui ui)
        {
            using (ui.Column(new() { Width = Size.Fill, Pad = 16, Gap = 8 }))
            {
                ui.Heading("Form", 2);
                if (ui.Button("Count")) Clicks++;

                ui.Input("name", ref Name, new InputOpts { AutoFocus = true });
                if (ui.Button("Accept")) Accepted = true;

                foreach (string item in Items)
                {
                    ui.Label(item);
                }
            }
        }
    }

    private sealed class Broken : Component
    {
        protected override void Draw(Ui ui) => throw new InvalidOperationException("boom");
    }

    /// <summary>Forty buttons in a scroll area: the last one starts out below the fold.</summary>
    private sealed class LongList : Component
    {
        public int Clicks;

        protected override void Draw(Ui ui)
        {
            using (ui.Scroll(new ScrollOpts { Id = "list", Width = Size.Fill, Height = Size.Fill }))
            {
                for (int i = 0; i < 40; i++)
                {
                    if (ui.Button($"Row {i}")) Clicks++;
                }
            }
        }
    }

    [Fact]
    public void FindAControlByItsLabelAndClickIt()
    {
        if (!OperatingSystem.IsWindows()) return;

        var page = new Form();
        using var app = TestApp.Start(page, 480, 360);

        // The first frame recorded the widgets; the button is findable by what it says.
        Assert.Contains(app.Widgets, w => w.Kind == "Button" && w.Text == "Count");

        app.FindButton("Count").Click();

        Assert.Equal(1, page.Clicks);
        Assert.Empty(app.Faults);
        Assert.True(app.PaintedAnything());
    }

    [Fact]
    public void TypeIntoAFieldAndSeeItInTheModel()
    {
        if (!OperatingSystem.IsWindows()) return;

        var page = new Form();
        using var app = TestApp.Start(page, 480, 360);

        // AutoFocus is how a page says "this field starts with the keyboard"; the harness
        // only has to say the window has focus.
        app.Focus();
        app.Type("Milk");

        Assert.Equal("Milk", page.Name);
    }

    [Fact]
    public void TheHarnessFindsWhatTheFrameDrewAndNothingElse()
    {
        if (!OperatingSystem.IsWindows()) return;

        var page = new Form();
        page.Items.AddRange(["first", "second"]);
        using var app = TestApp.Start(page, 480, 360);

        Assert.True(app.TryFind("Accept", out WidgetTarget accept));
        Assert.Equal("Button", accept.Kind);

        // Labels are findable too, and the id is what a field is called.
        Assert.True(app.TryFind("second", out _));
        Assert.True(app.TryFind("name", out WidgetTarget input));
        Assert.Equal("Input", input.Kind);

        Assert.False(app.TryFind("nothing like this", out _));
        Assert.Throws<InvalidOperationException>(() => app.Find("nothing like this"));
    }

    [Fact]
    public void ARectLeadsToAClickThatHitsTheWidget()
    {
        if (!OperatingSystem.IsWindows()) return;

        var page = new Form();
        using var app = TestApp.Start(page, 480, 360);
        WidgetTarget accept = app.FindButton("Accept");

        // The recorded rect is where the widget really is: a click at its centre lands.
        Assert.True(accept.Rect.W > 0);
        Assert.True(accept.Rect.H > 0);

        app.MoveTo(accept.Rect.X + (accept.Rect.W / 2), accept.Rect.Y + (accept.Rect.H / 2));
        app.Press();
        app.Release();

        Assert.True(page.Accepted);
    }

    [Fact]
    public void TheWheelReachesAControlBelowTheFold()
    {
        if (!OperatingSystem.IsWindows()) return;

        var page = new LongList();
        using var app = TestApp.Start(page, 400, 300);

        // Row 39 starts out under the 300 px viewport, where a click would hit something else.
        float before = app.FindButton("Row 39").Rect.Y;

        // The wheel at the scroll area's centre, down forty notches: more than the list has.
        // The spring wants frames to settle, not just the next one.
        app.ScrollAt(200, 150, -40);
        app.Frame(60);
        float after = app.FindButton("Row 39").Rect.Y;

        Assert.True(after < before, $"the wheel moved Row 39 from {before} to {after}");
        app.FindButton("Row 39").Click();
        Assert.Equal(1, page.Clicks);
    }

    /// <summary>A two-step tour over two buttons: the shape every gallery page with a tour uses.</summary>
    private sealed class Guided : Component
    {
        public int Step;
        public bool Running = true;
        private readonly TourStep[] _steps = new TourStep[2];

        protected override void Draw(Ui ui)
        {
            using (ui.Column(new() { Width = Size.Fill, Pad = 16, Gap = 8 }))
            {
                ui.Button("First");
                _steps[0] = new TourStep(ui.LastId, "One", "The first thing.");
                ui.Button("Second");
                _steps[1] = new TourStep(ui.LastId, "Two", "The second thing.");

                // The step is the tour's, written through the ref: its return value is the action
                // the user took, and assigning it here would replace the step with that code.
                if (Step >= 0) _ = ui.Tour("tour", _steps, ref Step);
                Running = Step >= 0;
            }
        }
    }

    [Fact]
    public void TheTourMovesItsStepThroughTheRefAndEndsItself()
    {
        if (!OperatingSystem.IsWindows()) return;

        var page = new Guided();
        using var app = TestApp.Start(page, 500, 400);
        app.Settle();

        Assert.Equal(0, page.Step);

        // Right is the tour's own next gesture, so the step moves without the card's button.
        app.Key(Key.Right);
        app.Settle();
        Assert.Equal(1, page.Step);

        // The last step's next is Done, which ends the tour: -1 is the only value that stops it.
        app.Key(Key.Right);
        app.Settle();
        Assert.Equal(-1, page.Step);
        Assert.False(page.Running);
    }

    [Fact]
    public void AFailingComponentReportsItsFaultInsteadOfCrashing()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = TestApp.Start(new Broken(), 320, 200);

        Assert.Single(app.Faults);
        Assert.Contains("boom", app.Faults[0], StringComparison.Ordinal);
        // The error boundary still drew something.
        Assert.True(app.PaintedAnything());
    }

    [Fact]
    public void ScreenshotsAreRealPngFiles()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = TestApp.Start(ui => ui.Heading("hello", 1), 200, 100);
        string path = app.SavePng();
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            // The signature every PNG starts with, and the size in the IHDR chunk.
            Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], bytes[..4]);
            int w = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            int h = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            Assert.Equal(app.Surface.PixelWidth, w);
            Assert.Equal(app.Surface.PixelHeight, h);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RecordingCostsNothingWhenItIsOff()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(400, 300);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        DrawFn draw = ui =>
        {
            using (ui.Column(new() { Width = Size.Fill }))
            {
                ui.Button("one");
                ui.Button("two");
            }
        };

        surface.Draw(draw, pixels);
        Assert.False(surface.RecordWidgets);
        Assert.Empty(surface.Widgets);

        surface.RecordWidgets = true;
        surface.Draw(draw, pixels);
        Assert.Equal(2, surface.Widgets.Count);
        Assert.Equal(["one", "two"], surface.Widgets.Select(w => w.Text));

        // Steady state with recording on still allocates nothing per frame beyond the records.
        surface.RecordWidgets = false;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++) surface.Draw(draw, pixels);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
