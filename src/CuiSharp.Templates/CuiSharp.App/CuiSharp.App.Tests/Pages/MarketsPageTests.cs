using CuiSharp;
using CuiSharp.App.Pages;
using CuiSharp.App.Services;
using CuiSharp.Hosting;
using Xunit.Abstractions;

namespace CuiSharp.App.Tests.Pages;

public class MarketsPageTests(ITestOutputHelper output)
{
    [Fact]
    public void TheFirstMarketsVisitShowsLoadingThenTheLiveChart()
    {
        var clock = new FakeClock();
        MarketFeed? feed = null;
        using var app = HeadlessApp.Start(new App(
            (TodoPage.PageTitle, static () => new TodoPage(new TodoService())),
            (MarketsPage.PageTitle, () => new MarketsPage(feed = new MarketFeed(new PriceFeed(), clock)))),
            TestHarness.Width, TestHarness.Height);
        Assert.Null(feed);

        // Click ends on the release frame, which creates the page. ClickAndFrame would skip it.
        app.Find("Tab", MarketsPage.PageTitle).Click();
        var first = app.Surface.LastFrame;
        Assert.NotNull(feed);
        Assert.Contains(app.Tree.Descendants(), node => node.Text == "Loading history…");

        var loaded = app.WaitFor("LineChart \"prices\"", TimeSpan.FromSeconds(10));
        Assert.True(loaded.Satisfied, app.Print());
        Assert.Contains(app.Tree.Find("page").Descendants(), node => node.Kind == "LineChart");
        Assert.DoesNotContain(app.Tree.Find("header").Descendants(), node => node.Kind == "LineChart");

        app.Frame(10); // Warm the loaded chart before sampling normal live draws.
        var drawTimes = new double[31];
        long maxBytes = 0;
        for (var i = 0; i < drawTimes.Length; i++)
        {
            clock.Advance(20);
            app.Frame();
            var frame = app.Surface.LastFrame;
            drawTimes[i] = frame.DrawMs;
            maxBytes = Math.Max(maxBytes, frame.AllocatedBytes);
        }

        Array.Sort(drawTimes);
        output.WriteLine($"First Markets draw: {first.DrawMs:F3} ms, {first.AllocatedBytes:N0} B");
        output.WriteLine($"Steady Markets draws: {drawTimes[drawTimes.Length / 2]:F3} ms median, {maxBytes:N0} B max");
        Assert.True(feed.Ticks > 0);
        Assert.Empty(app.Faults);
    }

    [Fact]
    public void TheChartUsesTheNewSymbolsHistoryOnTheSelectionFrame()
    {
        var (feed, _) = MarketTestData.CreateMarketFeed();
        var snapshot = MarketTestData.CreateSnapshot(points: 3);
        snapshot.History[0] = [126, 130, 128];
        snapshot.History[1] = [434, 430, 432, 428, 433];
        snapshot.Last[0] = snapshot.History[0][^1];
        snapshot.Last[1] = snapshot.History[1][^1];
        feed.Apply(snapshot);

        using var app = HeadlessApp.Start(new MarketsPage(feed), TestHarness.Width, TestHarness.Height);
        Assert.Equal("NVIDIA", app.Find("Heading", "NVIDIA").Text);
        Assert.Contains(app.Tree.Descendants(), node => node.Text == "3 samples buffered, 3 drawn");

        // Inspect the key-down frame; the key-up frame would hide stale chart data.
        app.Key(Key.Tab).Key(Key.Tab);
        app.Surface.KeyDown(Key.Enter);
        app.Frame();

        Assert.Equal("Microsoft", app.Find("Heading", "Microsoft").Text);
        Assert.Equal("5 samples buffered, 5 drawn", app.Find("Secondary", "5 samples buffered").Text);
        Assert.Contains(app.Tree.Descendants(), node => node.Kind == "LineChart");
    }

    [Theory]
    [InlineData(false, "NVIDIA")]
    [InlineData(true, "Microsoft")]
    public void TheMarketsPageAllocatesNothingPerLiveFrame(bool selectSecondRow, string symbol)
    {
        var (feed, clock) = MarketTestData.CreateMarketFeed(historyPoints: 240);
        using var page = new MarketsPage(feed);

        TestHarness.AssertZeroAllocations(page, () => clock.Advance(16.6), prepare: app =>
        {
            if (selectSecondRow)
            {
                app.Key(Key.Tab).Key(Key.Tab).Key(Key.Enter);
            }
        }, verify: app =>
        {
            Assert.Equal(symbol, app.Find("Heading", symbol).Text);
            Assert.Contains(app.Tree.Descendants(), node => node.Kind == "LineChart");
        });
        Assert.True(feed.Ticks > 0);
    }

    [Fact]
    public void TheMarketsShellAllocatesNothingPerSteadyFrame()
    {
        using var shell = new App(
            (TodoPage.PageTitle, static () => new TodoPage(new TodoService())),
            (MarketsPage.PageTitle, static () => MarketTestData.CreateMarketsPage(historyPoints: 240)));

        TestHarness.AssertZeroAllocations(shell,
            prepare: app => app.Find("Tab", MarketsPage.PageTitle).ClickAndFrame(),
            verify: app => Assert.Equal(MarketsPage.PageTitle, app.Find("Heading", MarketsPage.PageTitle).Text));
    }
}
