using CuiSharp.App.Services;

namespace CuiSharp.App.Tests;

/// <summary>
/// Tests for the ring buffer behind the markets page. It is the part of a live
/// dashboard that is easy to get subtly wrong — wrap-around, ordering, the change
/// calculation — and it is testable without a single frame.
/// </summary>
public class PriceHistoryTests
{
    private static PriceHistory Create(int capacity = 4) => new(symbols: 1, capacity: capacity);

    [Fact]
    public void Count_stops_at_capacity_while_ticks_keep_coming()
    {
        var history = Create();

        for (var i = 0; i < 10; i++) history.Push(0, i);

        Assert.Equal(4, history.Count(0));
        Assert.Equal(10, history.Ticks);
        Assert.Equal(9, history.Last(0));
    }

    [Fact]
    public void CopyTail_returns_the_newest_samples_oldest_first()
    {
        var history = Create();
        for (var i = 0; i < 6; i++) history.Push(0, i);   // 2, 3, 4, 5 survive

        var window = new float[4];
        var written = history.CopyTail(0, window);

        Assert.Equal(4, written);
        Assert.Equal([2f, 3f, 4f, 5f], window);
    }

    [Fact]
    public void CopyTail_fills_only_what_exists()
    {
        var history = Create();
        history.Push(0, 10);
        history.Push(0, 11);

        var window = new float[4];
        var written = history.CopyTail(0, window);

        Assert.Equal(2, written);
        Assert.Equal([10f, 11f, 0f, 0f], window);
    }

    [Fact]
    public void Change_compares_the_newest_sample_with_the_oldest()
    {
        var history = Create(capacity: 2);
        history.Push(0, 100);
        history.Push(0, 110);

        Assert.Equal(0.1f, history.Change(0), precision: 4);

        // With one sample there is nothing to compare, and a fresh ring reports 0
        // rather than NaN, so a view can draw it without a special case.
        var fresh = Create();
        Assert.Equal(0f, fresh.Change(0));
        fresh.Push(0, 50);
        Assert.Equal(0f, fresh.Change(0));
    }

    [Fact]
    public void Load_keeps_the_newest_samples_of_a_longer_history()
    {
        var history = Create(capacity: 3);

        history.Load([[1f, 2f, 3f, 4f, 5f]]);

        Assert.Equal(3, history.Count(0));
        var window = new float[3];
        history.CopyTail(0, window);
        Assert.Equal([3f, 4f, 5f], window);
        Assert.Equal(5f, history.Last(0));
    }

    [Fact]
    public void Rejects_a_capacity_that_cannot_hold_anything()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceHistory(symbols: 1, capacity: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceHistory(symbols: 0, capacity: 1));
    }
}
