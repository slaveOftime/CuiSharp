using CuiSharp;
using CuiSharp.App.Pages;
using CuiSharp.App.Services;
using CuiSharp.Hosting;

namespace CuiSharp.App.Tests;

/// <summary>
/// The other half of a render test: not "does it draw", but "does it do what it says". A control is
/// found by the label it shows, acted on for real — pointer over it, a press frame, a release frame
/// — and the assertion is on the state the click changed. A page that looks right and does nothing
/// cannot pass these.
/// </summary>
public class PageInteractionTests
{
    private const int Width = 640;
    private const int Height = 480;

    [Fact]
    public void TypingAndClickingAddAddsAnItem()
    {
        var todos = new TodoService();
        int before = todos.Items.Count;
        using var app = HeadlessApp.Start(new TodoPage(todos), Width, Height);

        app.FindInput("draft").Click();

        // A text field takes keys only once the window has focus.
        app.Focus().Type("Milk");
        app.FindButton("Add").ClickAndFrame();

        Assert.Equal(before + 1, todos.Items.Count);
        Assert.Equal("Milk", todos.Items[^1].Title);

        // The row the click added is in the frame that click produced.
        Assert.Contains(app.Tree.Descendants(), n => n.Text == "Milk");
    }

    [Fact]
    public void TheFilterHidesWhatItSaysItHides()
    {
        using var app = HeadlessApp.Start(new TodoPage(new TodoService()), Width, Height);

        Assert.Contains(app.Tree.Descendants(), n => n.Text == "Sketch the onboarding flow");

        app.Find("Segment", "Done").ClickAndFrame();
        Assert.DoesNotContain(app.Tree.Descendants(), n => n.Text == "Sketch the onboarding flow");
        Assert.Contains(app.Tree.Descendants(), n => n.Text == "Nothing done yet");

        app.Find("Segment", "All").ClickAndFrame();
        Assert.Contains(app.Tree.Descendants(), n => n.Text == "Sketch the onboarding flow");
    }

    [Fact]
    public void TheShellSwitchesPagesAndTheme()
    {
        using var app = HeadlessApp.Start(new App(new TodoPage(new TodoService()), NewMarkets()), Width, Height);

        Assert.Contains(app.Tree.Descendants(), n => n.Kind == "Heading" && n.Text == "Todos");

        // A tab is a control of its own, so a page is one click away by name.
        app.Find("Tab", "Markets").ClickAndFrame();
        Assert.Contains(app.Tree.Descendants(), n => n.Kind == "Heading" && n.Text == "Markets");

        // The theme switch is the frame assigning what the control decided, so a click is enough.
        app.Find("Segment", "Dark").ClickAndFrame();
        Ui ui = app.Surface.Ui;
        Assert.Equal(Theme.Dark, ui.Theme);
    }

    [Fact]
    public void AMutationAppliesItsResultBeforeTheNextFrame()
    {
        var completion = new TaskCompletionSource<int>();
        var applied = new int[1];
        applied[0] = -1;

        // The shape the pages use: start it once, keep drawing, and the result is applied on the UI
        // thread before the next frame — so Draw reads state instead of polling.
        var mutation = new Mutation<int>(_ => completion.Task, value => applied[0] = value);

        mutation.Run();
        Assert.True(mutation.IsRunning);
        Assert.Equal(-1, applied[0]);

        completion.SetResult(7);
        for (var i = 0; i < 500 && mutation.IsRunning; i++) Thread.Sleep(10);
        Assert.False(mutation.IsRunning);

        // One frame is enough: the frame boundary is where a result is applied.
        using var surface = CuiSurface.Create(64, 64);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        surface.Draw(_ => { }, pixels);

        Assert.Equal(7, applied[0]);
        Assert.Null(mutation.Error);
    }

    private static MarketsPage NewMarkets() => new(new MarketFeed(new PriceFeed(), new Clock()));

    /// <summary>A clock a test advances by hand, so no test waits in real time.</summary>
    private sealed class Clock : TimeProvider
    {
        private long _ms;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => _ms;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(_ms);

        public void Advance(double milliseconds) => _ms += (long)milliseconds;
    }
}
