using CuiSharp.Hosting;
using CuiSharp.Testing;

namespace CuiSharp.Tests;

/// <summary>
/// Finding a control among others that say the same thing, and keeping two apps on one thread
/// apart. Both are about ambiguity: a frame draws more than one "Save", and a test draws more
/// than one app.
/// </summary>
public class WidgetSearchTests
{
    /// <summary>A heading and a button that both say "Save", in and out of a named card.</summary>
    private sealed class Ambiguous : Component
    {
        public int Saved;
        public int SavedOutside;

        protected override void Draw(Ui ui)
        {
            ui.Heading("Save");
            if (ui.Button("Save type")) SavedOutside++;
            using (ui.Card(new() { Id = "editor", Width = Size.Fill, Gap = 4 }))
            {
                ui.Label("Save your work");
                if (ui.Button("Save")) Saved++;
            }
        }
    }

    private sealed class TwoButtons : Component
    {
        public int One;
        public int Two;

        protected override void Draw(Ui ui)
        {
            if (ui.Button("One")) One++;
            if (ui.Button("Two")) Two++;
        }
    }

    [Fact]
    public void AnExactLabelBeatsAHeadingThatRepeatsIt()
    {
        if (!OperatingSystem.IsWindows()) return;

        var page = new Ambiguous();
        using var app = TestApp.Start(page, 500, 400);

        // Three things say "Save" and one of them is a button: that is the one a click means.
        Assert.Equal("Button", app.Find("Save").Kind);
        app.Find("Save").Click();
        Assert.Equal(1, page.Saved);
        Assert.Equal(0, page.SavedOutside);
    }

    [Fact]
    public void AKindOrAPathSaysWhichOne()
    {
        if (!OperatingSystem.IsWindows()) return;

        var page = new Ambiguous();
        using var app = TestApp.Start(page, 500, 400);

        Assert.Equal("Heading", app.Find("Heading \"Save\"").Kind);
        Assert.Equal("Save type", app.Find("Button", "Save type").Text);

        // The card holds the short one; the long one is outside it.
        Assert.Equal("Save", app.Find("editor > Save").Text);
        Assert.Equal("Label", app.Find("editor > save your").Kind);

        app.Find("editor > Save").Click();
        Assert.Equal(1, page.Saved);
    }

    [Fact]
    public void APathThatLeadsNowhereExplainsItself()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = TestApp.Start(new Ambiguous(), 500, 400);

        var error = Assert.Throws<InvalidOperationException>(() => app.Find("nowhere > Save"));
        Assert.Contains("nowhere", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoAppsOnOneThreadKeepTheirOwnFrames()
    {
        if (!OperatingSystem.IsWindows()) return;

        var page = new TwoButtons();
        using var first = TestApp.Start(page, 400, 300);
        using var second = TestApp.Start(ui => ui.Button("Elsewhere"), 300, 200);

        // The second app drew last; the first still answers for its own frame.
        Assert.True(first.TryFind("One", out _));
        Assert.False(first.TryFind("Elsewhere", out _));
        Assert.True(second.TryFind("Elsewhere", out _));

        // And its clicks still land: recording was not switched off by the other surface.
        first.Find("One").Click();
        Assert.Equal(1, page.One);
        Assert.Equal(0, page.Two);
    }

    [Fact]
    public void DisposingOneAppLeavesTheOtherRecording()
    {
        if (!OperatingSystem.IsWindows()) return;

        var page = new TwoButtons();
        using var keeper = TestApp.Start(page, 400, 300);
        TestApp.Start(ui => ui.Button("Gone"), 300, 200).Dispose();

        // The recording that went away was not the one that is still in use.
        Assert.True(WidgetRecorder.Enabled);
        keeper.Frame();
        keeper.Find("Two").Click();
        Assert.Equal(1, page.Two);
        Assert.Equal(0, page.One);
    }

    [Fact]
    public void RecordOffIsOffForThatSurfaceOnlyAfterEveryoneIsDone()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(300, 200);
        DrawFn draw = ui => ui.Button("Only");
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];

        surface.RecordWidgets = true;
        surface.Draw(draw, pixels);
        Assert.Single(surface.Widgets);

        surface.RecordWidgets = false;
        // Turning it off publishes nothing new, and the next frame records nothing.
        surface.Draw(draw, pixels);
        Assert.Empty(surface.Widgets);
        Assert.False(WidgetRecorder.Enabled);
    }
}
