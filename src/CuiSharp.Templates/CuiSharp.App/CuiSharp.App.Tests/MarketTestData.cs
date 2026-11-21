using CuiSharp.App.Pages;
using CuiSharp.App.Services;

namespace CuiSharp.App.Tests;

/// <summary>Manual millisecond clock so feed tests don't depend on real time.</summary>
internal sealed class FakeClock : TimeProvider
{
    private long _ms;

    public override long TimestampFrequency => 1000;

    public override long GetTimestamp() => _ms;

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(_ms);

    public void Advance(double milliseconds) => _ms += (long)milliseconds;
}

internal static class MarketTestData
{
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

    public static MarketsPage CreateMarketsPage(int historyPoints) => new(CreateMarketFeed(historyPoints).Feed);

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
}
