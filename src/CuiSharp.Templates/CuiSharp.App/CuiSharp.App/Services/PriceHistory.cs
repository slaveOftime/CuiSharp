namespace CuiSharp.App.Services;

/// <summary>
/// The last N prices per symbol, in one fixed-size ring each.
/// <para>
/// This is the part of a live dashboard that is actually worth unit testing: it
/// wraps around, it never grows, and it answers the three questions a view asks
/// (how many samples, what changed since the oldest one, give me the newest window)
/// in constant time. It knows nothing about CuiSharp, so a test drives it directly.
/// </para>
/// </summary>
public sealed class PriceHistory
{
    private readonly float[][] _rings;
    private readonly int[] _head;
    private readonly int[] _count;

    /// <summary>Creates one ring of <paramref name="capacity"/> samples per symbol.</summary>
    public PriceHistory(int symbols, int capacity)
    {
        if (symbols <= 0) throw new ArgumentOutOfRangeException(nameof(symbols));
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));

        Capacity = capacity;
        _rings = new float[symbols][];
        _head = new int[symbols];
        _count = new int[symbols];
        for (var s = 0; s < symbols; s++) _rings[s] = new float[capacity];
    }

    /// <summary>Samples per symbol. Memory is fixed at construction.</summary>
    public int Capacity { get; }

    /// <summary>How many rings there are, one per symbol.</summary>
    public int SymbolCount => _rings.Length;

    /// <summary>How many ticks have been accepted. Monotonic.</summary>
    public long Ticks { get; private set; }

    /// <summary>Appends one price, dropping the oldest sample once the ring is full.</summary>
    public void Push(int symbol, float price)
    {
        _rings[symbol][_head[symbol]] = price;
        _head[symbol] = (_head[symbol] + 1) % Capacity;
        if (_count[symbol] < Capacity) _count[symbol]++;
        Ticks++;
    }

    /// <summary>
    /// Replaces every ring with a freshly loaded history, oldest first. A history
    /// longer than the ring keeps its newest samples.
    /// </summary>
    public void Load(float[][] history)
    {
        for (var s = 0; s < _rings.Length; s++)
        {
            float[] source = history[s];
            int take = Math.Min(source.Length, Capacity);
            Array.Copy(source, source.Length - take, _rings[s], 0, take);
            _head[s] = take % Capacity;
            _count[s] = take;
        }
    }

    /// <summary>Samples currently held for one symbol.</summary>
    public int Count(int symbol) => _count[symbol];

    /// <summary>The newest price, or 0 before any sample arrived.</summary>
    public float Last(int symbol) => _count[symbol] is 0 ? 0 : _rings[symbol][Tail(_head[symbol], Capacity)];

    /// <summary>
    /// Change from the oldest sample in the ring to the newest, as a fraction:
    /// 0.012 means up 1.2%. Zero until there are two samples to compare.
    /// </summary>
    public float Change(int symbol)
    {
        int count = _count[symbol];
        if (count < 2) return 0;

        int oldest = (_head[symbol] - count + Capacity) % Capacity;
        float start = _rings[symbol][oldest];
        return start <= 0 ? 0 : Last(symbol) / start - 1;
    }

    /// <summary>
    /// Copies the newest samples into <paramref name="destination"/>, oldest first,
    /// and returns how many were written. The copy is what a chart needs: contiguous
    /// and in reading order.
    /// </summary>
    public int CopyTail(int symbol, Span<float> destination)
    {
        int n = Math.Min(_count[symbol], destination.Length);
        if (n == 0) return 0;

        float[] ring = _rings[symbol];
        int start = (_head[symbol] - n + Capacity) % Capacity;
        int first = Math.Min(n, Capacity - start);
        ring.AsSpan(start, first).CopyTo(destination);
        if (n > first) ring.AsSpan(0, n - first).CopyTo(destination[first..]);
        return n;
    }

    private static int Tail(int head, int capacity) => (head - 1 + capacity) % capacity;
}
