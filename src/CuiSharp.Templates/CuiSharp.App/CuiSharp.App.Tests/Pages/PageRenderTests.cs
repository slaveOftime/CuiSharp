using CuiSharp.App.Pages;
using CuiSharp.App.Services;
using CuiSharp.Hosting;

namespace CuiSharp.App.Tests.Pages;

/// <summary>
/// Headless render and steady-state 0 B/frame allocation tests for every screen.
/// </summary>
public class PageRenderTests
{
    [Fact]
    public void TheTodoPageDrawsItsSeededItemsWithNoFaults()
    {
        using var app = HeadlessApp.Start(new TodoPage(new TodoService()), TestHarness.Width, TestHarness.Height);

        Assert.True(app.PaintedAnything());
        Assert.Empty(app.Faults);
        Assert.Contains(app.Tree.Descendants(), node => node.Text == "Sketch the onboarding flow");
        Assert.Contains(app.Tree.Descendants(), node => node.Text == "3 open");
    }

    [Fact]
    public void TheMarketsPageDrawsLoadingAndLiveStates()
    {
        var (feed, clock) = TestHarness.CreateMarketFeed();

        using var app = HeadlessApp.Start(new MarketsPage(feed), TestHarness.Width, TestHarness.Height);

        Assert.Empty(app.Faults);
        Assert.Contains(app.Tree.Descendants(), node => node.Text == "Loading history…");

        feed.Apply(TestHarness.CreateSnapshot(points: 400));

        for (var i = 0; i < 3; i++)
        {
            clock.Advance(16.6);
            app.Frame();
        }

        Assert.Empty(app.Faults);
        Assert.True(feed.Ticks > 0);
        Assert.Contains(app.Tree.Descendants(), node => node.Kind == "LineChart");
    }

    [Fact]
    public void TheTodoPageAllocatesNothingPerFrame()
    {
        using var page = new TodoPage(new TodoService());

        TestHarness.AssertZeroAllocations(page);
    }

    [Fact]
    public void TheTodoPageAllocatesNothingOnceItHasBeenEdited()
    {
        var todos = new TodoService();
        using var page = new TodoPage(todos);

        // Keep the draft nonempty while measuring; Add would clear it and hide draft-only allocations.
        TestHarness.AssertZeroAllocations(page, prepare: app =>
        {
            app.FindById("draft").Click();
            app.Type("Milk");
        }, verify: app =>
        {
            app.FindButton("Add").Click();
            Assert.Contains(todos.Items, item => item.Title == "Milk");
        });
    }

    [Fact]
    public void TheTodoPageAllocatesNothingAfterAddingAnItem()
    {
        var todos = new TodoService();
        using var page = new TodoPage(todos);

        TestHarness.AssertZeroAllocations(page, prepare: app =>
        {
            app.FindById("draft").Click();
            app.Type("Milk");
            app.FindButton("Add").Click();
            Assert.Contains(todos.Items, item => item.Title == "Milk");
        });
    }

    [Fact]
    public void TheMarketsPageAllocatesNothingPerLiveFrame()
    {
        var (feed, clock) = TestHarness.CreateMarketFeed(historyPoints: 240);

        using var page = new MarketsPage(feed);

        TestHarness.AssertZeroAllocations(page, () => clock.Advance(16.6));
        Assert.True(feed.Ticks > 0);
    }

    [Fact]
    public void TheShellRegionsKeepPageControlsOutOfTheThemeSwitch()
    {
        using var app = HeadlessApp.Start(
            new App(new TodoPage(new TodoService()), TestHarness.CreateMarketsPage(historyPoints: 240)),
            TestHarness.Width, TestHarness.Height);

        var tree = app.Tree;
        var header = tree.Find("header");
        var page = tree.Find("page");
        var footer = tree.Find("footer");

        Assert.Same(header.Parent, page.Parent);
        Assert.Same(header.Parent, footer.Parent);
        Assert.Contains(page.Descendants(), node => node.Text == "draft");
        Assert.DoesNotContain(header.Descendants(), node => node.Text == "draft");
        Assert.Equal("Segment", tree.Find("theme > Dark").Kind);

        app.Find("Tab \"Markets\"").ClickAndFrame();

        Assert.Empty(app.Faults);
        Assert.Contains(app.Tree.Find("page").Descendants(), node => node.Kind == "LineChart");
        Assert.DoesNotContain(app.Tree.Find("header").Descendants(), node => node.Kind == "LineChart");
    }

    [Fact]
    public void TheShellAllocatesNothingPerFrame()
    {
        using var app = new App(new TodoPage(new TodoService()), TestHarness.CreateMarketsPage(historyPoints: 240));

        TestHarness.AssertZeroAllocations(app);
    }
}
