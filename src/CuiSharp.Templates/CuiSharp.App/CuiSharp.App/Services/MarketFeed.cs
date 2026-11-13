namespace CuiSharp.App.Services;

/// <summary>
/// Clock-driven 50 Hz market feed over fixed-size <see cref="PriceHistory"/> rings.
/// Caps per-frame work and counts stalls in <see cref="Skipped"/> rather than replaying them.
/// </summary>
public sealed class MarketFeed(PriceFeed source, TimeProvider? clock = null)
{
    /// <summary>Ring capacity per symbol: 20 000 samples is about 6 minutes at 50 Hz.</summary>
    public const int CapacityPerSymbol = 20_000;

    private const float TicksPerSecond = 50f;
    private const double MaxFrameMs = 250;      // a longer gap means the loop slept
    private const int MaxTicksPerFrame = 4;
    private const double AssumedFrameMs = 1000.0 / 60;

    private readonly PriceFeed _source = source;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private long _stamp = -1;   // -1 until the first frame: a clock may legitimately read 0
    private double _debt;
    private double _rate;

    /// <summary>The rings the page draws from. Allocated once.</summary>
    public PriceHistory History { get; } = new(PriceFeed.Symbols.Count, CapacityPerSymbol);

    /// <summary>True once a history has arrived: the feed ticks from then on.</summary>
    public bool Live { get; private set; }

    /// <summary>Ticks generated so far.</summary>
    public long Ticks { get; private set; }

    /// <summary>Ticks the loop could not absorb: a stall, or a page that was not drawn.</summary>
    public long Skipped { get; private set; }

    /// <summary>Smoothed ticks per second, for the meters.</summary>
    public double Rate => _rate;

    /// <summary>Milliseconds since the previous frame.</summary>
    public double IntervalMs { get; private set; }

    /// <summary>Loads a history. Cheap enough to call from a mutation's apply callback.</summary>
    public Task<MarketSnapshot> LoadAsync(CancellationToken cancellationToken) =>
        _source.LoadAsync(CapacityPerSymbol, cancellationToken);

    /// <summary>
    /// Applies a loaded history and starts ticking, continuing from the prices it
    /// ended at. Call it on the UI thread: a mutation's apply callback does exactly that.
    /// </summary>
    public void Apply(MarketSnapshot snapshot)
    {
        History.Load(snapshot.History);
        _source.Resume(snapshot.Last);
        _debt = 0;
        _stamp = -1;     // the next Advance starts the clock, so loading is not a "gap"
        Live = true;
    }

    /// <summary>Generates every tick the clock owes since the previous frame.</summary>
    public void Advance()
    {
        var now = _clock.GetTimestamp();
        if (_stamp < 0)
        {
            _stamp = now;
            return;
        }

        var ms = _clock.GetElapsedTime(_stamp, now).TotalMilliseconds;
        _stamp = now;

        if (ms <= 0)
        {
            return;
        }

        IntervalMs = ms;
        if (!Live)
        {
            return;
        }

        if (ms > MaxFrameMs)
        {
            // The loop slept (hidden window, another tab, a busy machine). Count what
            // did not happen and resume at frame cadence instead of replaying it.
            Skipped += (long)(ms * TicksPerSecond / 1000.0);
            ms = AssumedFrameMs;
            _debt = 0;
        }

        _debt += ms * TicksPerSecond / 1000.0;
        var due = (int)_debt;
        _debt -= due;

        if (due > MaxTicksPerFrame)
        {
            // The cap drops ticks, so the debt has to drop them too: leaving them in
            // would replay them on the next frame and count them as skipped twice.
            Skipped += due - MaxTicksPerFrame;
            due = MaxTicksPerFrame;
            _debt = 0;
        }

        for (var tick = 0; tick < due; tick++)
        {
            for (var symbol = 0; symbol < History.SymbolCount; symbol++)
            {
                History.Push(symbol, _source.Next(symbol));
            }

            Ticks++;
        }

        var instant = due * 1000.0 / ms;
        _rate = _rate is 0 ? instant : (_rate * 0.9) + (instant * 0.1);
    }
}
