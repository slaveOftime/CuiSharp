using CuiSharp.App.Services;

namespace CuiSharp.App.Tests;

/// <summary>
/// The simulator, tested without a window and without waiting in real time: a fake
/// clock drives it, so the rules that matter (how many ticks a frame is owed, what
/// happens after a stall) are assertions rather than observations.
/// </summary>
public class MarketFeedTests
{
    /// <summary>A clock the test advances by hand, in milliseconds.</summary>
    private sealed class FakeClock : TimeProvider
    {
        private long _ms;

        public override long TimestampFrequency => 1000;          // one tick per millisecond

        public override long GetTimestamp() => _ms;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(_ms);

        public void Advance(double milliseconds) => _ms += (long)milliseconds;
    }

    private static (MarketFeed Feed, FakeClock Clock) Create()
    {
        var clock = new FakeClock();
        return (new MarketFeed(new PriceFeed(), clock), clock);
    }

    [Fact]
    public void Nothing_ticks_before_a_history_arrives()
    {
        (MarketFeed feed, FakeClock clock) = Create();

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
        (MarketFeed feed, FakeClock clock) = Create();
        feed.Apply(History(200));
        feed.Advance();                       // starts the clock

        // One second of 60 fps frames is 50 ticks at 50 Hz.
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
        (MarketFeed feed, FakeClock clock) = Create();
        feed.Apply(History(200));
        feed.Advance();

        clock.Advance(2000);                  // two seconds of no frames
        feed.Advance();

        // 100 ticks were owed; a handful run now and the rest are reported as skipped.
        Assert.InRange(feed.Ticks, 0, 4);
        Assert.InRange(feed.Skipped, 96, 100);

        // The cap must not leave the backlog in the debt: the next frame is normal.
        long skipped = feed.Skipped;
        clock.Advance(1000.0 / 60);
        feed.Advance();
        Assert.Equal(skipped, feed.Skipped);
        Assert.InRange(feed.Ticks, 1, 4);
    }

    [Fact]
    public void ABusyFrameIsCappedAndTheRestIsReported()
    {
        (MarketFeed feed, FakeClock clock) = Create();
        feed.Apply(History(200));
        feed.Advance();

        clock.Advance(150);                   // 7.5 ticks owed in one frame
        feed.Advance();

        Assert.Equal(4, feed.Ticks);
        Assert.InRange(feed.Skipped, 3, 4);

        // ...and the surplus is not carried into the following frames.
        long skipped = feed.Skipped;
        for (var i = 0; i < 10; i++)
        {
            clock.Advance(1000.0 / 60);
            feed.Advance();
        }
        Assert.Equal(skipped, feed.Skipped);
    }

    [Fact]
    public void ApplyingAHistoryContinuesFromItsLastPrice()
    {
        (MarketFeed feed, FakeClock clock) = Create();
        MarketSnapshot snapshot = History(200);
        feed.Apply(snapshot);
        clock.Advance(16.6);
        feed.Advance();

        // No cliff: the first live tick starts within a hair of the loaded price.
        float loadedLast = snapshot.Last[0];
        Assert.InRange(feed.History.Last(0), loadedLast * 0.99f, loadedLast * 1.01f);
        Assert.Equal(200, feed.History.Count(0));
    }

    /// <summary>A rising history per symbol, oldest first.</summary>
    private static MarketSnapshot History(int points)
    {
        var history = new float[PriceFeed.Symbols.Count][];
        var last = new float[PriceFeed.Symbols.Count];
        for (var s = 0; s < PriceFeed.Symbols.Count; s++)
        {
            history[s] = new float[points];
            var price = PriceFeed.Symbols[s].Start;
            for (var i = 0; i < points; i++)
            {
                price *= 1.001f;
                history[s][i] = price;
            }
            last[s] = price;
        }
        return new MarketSnapshot(history, last);
    }
}
