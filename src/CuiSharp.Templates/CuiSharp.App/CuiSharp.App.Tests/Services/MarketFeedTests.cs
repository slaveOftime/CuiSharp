using CuiSharp.App.Services;

namespace CuiSharp.App.Tests.Services;

/// <summary>
/// Deterministic fake-clock tests for <see cref="MarketFeed"/> cadence, stall recovery, and frame caps.
/// </summary>
public class MarketFeedTests
{
    [Fact]
    public void NothingTicksBeforeHistoryIsApplied()
    {
        var (feed, clock) = TestHarness.CreateMarketFeed();

        for (var i = 0; i < 10; i++)
        {
            clock.Advance(16.6);
            feed.Advance();
        }

        Assert.False(feed.Live);
        Assert.Equal(0, feed.Ticks);
        Assert.Equal(0, feed.History.Count(0));
    }

    [Fact]
    public void AFramesShareOfTicksFollowsTheClockNotTheFrameCount()
    {
        var (feed, clock) = TestHarness.CreateMarketFeed(historyPoints: 200);
        feed.Advance();

        for (var i = 0; i < 60; i++)
        {
            clock.Advance(1000.0 / 60);
            feed.Advance();
        }

        Assert.InRange(feed.Ticks, 47, 53);
        Assert.Equal(0, feed.Skipped);
        Assert.InRange(feed.Rate, 40, 60);
    }

    [Fact]
    public void AStallIsCountedOnceAndNotReplayed()
    {
        var (feed, clock) = TestHarness.CreateMarketFeed(historyPoints: 200);
        feed.Advance();

        clock.Advance(2000);
        feed.Advance();

        Assert.InRange(feed.Ticks, 0, 4);
        Assert.InRange(feed.Skipped, 96, 100);

        var skipped = feed.Skipped;

        clock.Advance(1000.0 / 60);
        feed.Advance();

        Assert.Equal(skipped, feed.Skipped);
        Assert.InRange(feed.Ticks, 1, 4);
    }

    [Fact]
    public void ABusyFrameIsCappedAndSurplusDebtIsDropped()
    {
        var (feed, clock) = TestHarness.CreateMarketFeed(historyPoints: 200);
        feed.Advance();

        clock.Advance(150);
        feed.Advance();

        Assert.Equal(4, feed.Ticks);
        Assert.InRange(feed.Skipped, 3, 4);

        var skipped = feed.Skipped;

        for (var i = 0; i < 10; i++)
        {
            clock.Advance(1000.0 / 60);
            feed.Advance();
        }

        Assert.Equal(skipped, feed.Skipped);
    }

    [Fact]
    public void ApplyingHistoryContinuesWalkFromItsLastPrice()
    {
        var (feed, clock) = TestHarness.CreateMarketFeed();
        var snapshot = TestHarness.CreateSnapshot(points: 200);

        feed.Apply(snapshot);
        clock.Advance(16.6);
        feed.Advance();

        var loadedLast = snapshot.Last[0];

        Assert.InRange(feed.History.Last(0), loadedLast * 0.99f, loadedLast * 1.01f);
        Assert.Equal(200, feed.History.Count(0));
    }
}
