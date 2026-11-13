namespace CuiSharp.App.Services;

/// <summary>
/// Preallocated per-symbol ring buffers holding the latest <see cref="Capacity"/> samples.
/// </summary>
public sealed class PriceHistory
{
    private readonly float[][] _rings;
    private readonly int[] _head;
    private readonly int[] _count;

    /// <summary>Creates one ring of <paramref name="capacity"/> samples per symbol.</summary>
    public PriceHistory(int symbols, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(symbols);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        Capacity = capacity;
        _rings = new float[symbols][];
        _head = new int[symbols];
        _count = new int[symbols];

        for (var s = 0; s < symbols; s++)
        {
            _rings[s] = new float[capacity];
        }
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

        if (_count[symbol] < Capacity)
        {
            _count[symbol]++;
        }

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
            var source = history[s];
            var take = Math.Min(source.Length, Capacity);

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
        var count = _count[symbol];
        if (count < 2)
        {
            return 0;
        }

        var oldest = (_head[symbol] - count + Capacity) % Capacity;
        var start = _rings[symbol][oldest];
        return start <= 0 ? 0 : (Last(symbol) / start) - 1;
    }

    /// <summary>
    /// Copies the newest samples into <paramref name="destination"/>, oldest first,
    /// and returns how many were written. The copy is what a chart needs: contiguous
    /// and in reading order.
    /// </summary>
    public int CopyTail(int symbol, Span<float> destination)
    {
        var written = Math.Min(_count[symbol], destination.Length);
        if (written is 0)
        {
            return 0;
        }

        var ring = _rings[symbol];
        var start = (_head[symbol] - written + Capacity) % Capacity;
        var first = Math.Min(written, Capacity - start);

        ring.AsSpan(start, first).CopyTo(destination);

        if (written > first)
        {
            ring.AsSpan(0, written - first).CopyTo(destination[first..]);
        }

        return written;
    }

    private static int Tail(int head, int capacity) => (head - 1 + capacity) % capacity;
}
