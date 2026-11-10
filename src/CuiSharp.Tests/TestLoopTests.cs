using CuiSharp.Hosting;
using CuiSharp.Testing;

namespace CuiSharp.Tests;

/// <summary>
/// The loop a test lives in: settle an animation instead of sleeping, address a control by id and
/// not by a word that gets translated, and photograph the UI rather than the mouse. Each of these
/// replaces a workaround every app writes for itself.
/// </summary>
public class TestLoopTests
{
    [Fact]
    public void SettlingRunsAnAnimationToItsEndWithoutSleeping()
    {
        var page = new GrowingBar();
        using var app = TestApp.Start(page, 400, 300);

        Assert.True(app.Animating, "a bar a tenth of the way in asks for the next frame");

        // No sleeps: each frame is a sixtieth of a second of the app's own clock.
        int frames = app.Settle();

        Assert.False(app.Animating);
        Assert.InRange(frames, 4, 240);
        Assert.Equal(1f, page.Progress, 3);
    }

    [Fact]
    public void AdvancingTimeMovesAnAnimationWithoutDrawingSixtyFrames()
    {
        var page = new GrowingBar();
        using var app = TestApp.Start(page, 400, 300);
        int after = page.FramesSeen;
        float before = page.Progress;

        app.Advance(TimeSpan.FromMilliseconds(250), frames: 1);

        // One frame of a quarter second, not fifteen frames of a sixtieth.
        Assert.Equal(0.5f, page.Progress - before, 2);
        Assert.Equal(after + 1, page.FramesSeen);
    }

    [Fact]
    public void AnIdFindsTheControlItsLabelWouldNot()
    {
        using var app = TestApp.Start(new TranslatedLabels(), 400, 300);

        // The label is the user's language; the id is the app's contract.
        WidgetTarget byId = app.FindById("draft");
        Assert.Equal("Input", byId.Kind);

        // And the id form of the query language reaches the same control.
        Assert.Equal(byId.Rect, app.Find("id:draft").Rect);
        Assert.Contains("Input \"draft\"", app.Print(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnItemIsFoundByPositionWhenItsLabelChanges()
    {
        using var app = TestApp.Start(new TranslatedLabels(), 400, 300);

        WidgetTarget second = app.FindItem("pages", 1);

        Assert.Equal("Segment", second.Kind);
        Assert.Equal("行情", second.Text);
    }

    [Fact]
    public void AScreenshotIsOfTheUiAndNotOfTheMouse()
    {
        using var app = TestApp.Start(new TranslatedLabels(), 400, 300);

        // The first frame a surface draws is not the settled one (the glyph atlas and layout are
        // still filling in), so a pixel baseline has to come after it.
        app.Settle();
        byte[] untouched = app.RawPixels.ToArray();

        // The pointer over a control changes the pixels a raw capture sees, which is why a
        // screenshot taken after a click used to carry the tooltip that click left behind.
        app.FindButton("Add").Hover();
        Assert.True(Differing(app.RawPixels, untouched) > 40, "hovering a button repaints it");

        // A screenshot parks the pointer first, so the picture is the UI at rest.
        string shot = app.SavePng();
        int left = Differing(app.RawPixels, untouched);
        Assert.True(left < 10, $"{left} bytes still differ from the untouched frame");

        File.Delete(shot);
    }

    /// <summary>How many bytes two frames disagree on.</summary>
    private static int Differing(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int count = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) count++;
        }

        return count;
    }

    /// <summary>A bar that fills over half a second of app time.</summary>
    private sealed class GrowingBar : Component
    {
        private float _elapsed;

        internal int FramesSeen { get; private set; }

        internal float Progress => Math.Min(1f, _elapsed / 0.5f);

        protected override bool WantsFrames => Progress < 1f;

        protected override void Draw(Ui ui)
        {
            FramesSeen++;
            _elapsed += ui.DeltaTime;
            using (ui.Column(new Box { Pad = 12 }))
            {
                ui.Label($"progress {Progress:0.00}");
            }
        }
    }

    /// <summary>An id on a field whose label is translated, a button, and a two-item strip.</summary>
    private sealed class TranslatedLabels : Component
    {
        private string _draft = "";

        internal bool Hovering { get; private set; }
        private int _page;
        private static readonly string[] Pages = ["自选", "行情"];

        protected override void Draw(Ui ui)
        {
            using (ui.Column(new Box { Pad = 12, Gap = 8 }))
            {
                ui.Input("draft", ref _draft);
                ui.Button("Add");
                ui.Segmented("pages", Pages, ref _page);
            }

        }
    }
}
