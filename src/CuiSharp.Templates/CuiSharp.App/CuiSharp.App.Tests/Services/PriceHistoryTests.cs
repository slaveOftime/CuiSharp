using CuiSharp.App.Services;

namespace CuiSharp.App.Tests.Services;

/// <summary>
/// Unit tests for <see cref="PriceHistory"/> ring buffer wrap-around, ordering, and deltas.
/// </summary>
public class PriceHistoryTests
{
    private static PriceHistory Create(int capacity = 4) => new(symbols: 1, capacity: capacity);

    [Fact]
    public void CountStopsAtCapacityWhileTicksKeepAdvancing()
    {
        var history = Create();

        for (var i = 0; i < 10; i++)
        {
            history.Push(0, i);
        }

        Assert.Equal(4, history.Count(0));
        Assert.Equal(10, history.Ticks);
        Assert.Equal(9, history.Last(0));
    }

    [Fact]
    public void CopyTailReturnsNewestSamplesOldestFirst()
    {
        var history = Create();

        for (var i = 0; i < 6; i++)
        {
            history.Push(0, i);
        }

        var window = new float[4];
        var written = history.CopyTail(0, window);

        Assert.Equal(4, written);
        Assert.Equal([2f, 3f, 4f, 5f], window);
    }

    [Fact]
    public void CopyTailWritesOnlyAvailableSamples()
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
    public void ChangeComparesNewestSampleWithOldestInRing()
    {
        var history = Create(capacity: 2);
        history.Push(0, 100);
        history.Push(0, 110);

        Assert.Equal(0.1f, history.Change(0), precision: 4);

        var fresh = Create();

        Assert.Equal(0f, fresh.Change(0));

        fresh.Push(0, 50);

        Assert.Equal(0f, fresh.Change(0));
    }

    [Fact]
    public void LoadRetainsNewestTailWhenHistoryExceedsCapacity()
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
    public void ConstructorRejectsNonPositiveSymbolCountOrCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceHistory(symbols: 1, capacity: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceHistory(symbols: 0, capacity: 1));
    }
}
