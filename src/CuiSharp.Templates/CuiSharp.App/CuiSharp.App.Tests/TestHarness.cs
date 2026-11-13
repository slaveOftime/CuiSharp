using CuiSharp;
using CuiSharp.App.Pages;
using CuiSharp.App.Services;
using CuiSharp.Hosting;

namespace CuiSharp.App.Tests;

/// <summary>
/// Manual millisecond clock so time-driven tests never sleep or wait on the wall clock.
/// </summary>
internal sealed class FakeClock : TimeProvider
{
    private long _ms;

    public override long TimestampFrequency => 1000;

    public override long GetTimestamp() => _ms;

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(_ms);

    public void Advance(double milliseconds) => _ms += (long)milliseconds;
}

/// <summary>
/// Shared fixtures for market feed and page tests, plus the steady-state 0 B/frame budget gate.
/// </summary>
internal static class TestHarness
{
    public const int Width = 640;
    public const int Height = 480;

    public static (MarketFeed Feed, FakeClock Clock) CreateMarketFeed(int historyPoints = 0)
    {
        var clock = new FakeClock();
        var feed = new MarketFeed(new PriceFeed(), clock);

        if (historyPoints > 0)
        {
            feed.Apply(CreateSnapshot(historyPoints));
        }

        return (feed, clock);
    }

    public static MarketsPage CreateMarketsPage(int historyPoints = 0) =>
        new(CreateMarketFeed(historyPoints).Feed);

    public static MarketSnapshot CreateSnapshot(int points)
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

    /// <summary>
    /// Draws frames until the page is warm and asserts the steady state allocates 0 B: the median
    /// of five 10-frame batches, with no component faults.
    /// </summary>
    /// <param name="component">The page or shell to measure.</param>
    /// <param name="eachFrame">Runs before every measured frame — advance a fake clock here.</param>
    /// <param name="prepare">
    /// Runs once, before the warm-up, with widget recording on: click, type or scroll the page into
    /// the state worth measuring. A page that only allocates after an edit passes the plain call and
    /// fails this one, so the interesting state is the one to ask for. LastFrame measures the draw
    /// before the recorded snapshot is built, so recording can stay on without inflating the budget.
    /// </param>
    /// <param name="verify">Runs after measuring to assert that prepared state survived the frames.</param>
    public static void AssertZeroAllocations(
        Component component,
        Action? eachFrame = null,
        Action<HeadlessApp>? prepare = null,
        Action<HeadlessApp>? verify = null)
    {
        using var app = HeadlessApp.Start(component, Width, Height);
        Assert.Empty(app.Faults);

        prepare?.Invoke(app);
        Assert.Empty(app.Faults);

        for (var i = 0; i < 3; i++)
        {
            eachFrame?.Invoke();
            app.Frame();
            Assert.Empty(app.Faults);
        }

        var batches = new long[5];
        for (var b = 0; b < batches.Length; b++)
        {
            for (var i = 0; i < 10; i++)
            {
                eachFrame?.Invoke();
                app.Frame();
                batches[b] += app.Surface.LastFrame.AllocatedBytes;
                Assert.Empty(app.Faults);
            }
        }

        Array.Sort(batches);

        Assert.Equal(0, batches[batches.Length / 2]);
        verify?.Invoke(app);
        Assert.Empty(app.Faults);
    }
}
