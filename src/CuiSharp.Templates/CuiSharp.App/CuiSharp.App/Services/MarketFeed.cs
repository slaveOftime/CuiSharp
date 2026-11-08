namespace CuiSharp.App.Services;

/// <summary>
/// A live market feed: the symbol walk from <see cref="PriceFeed"/> plus the clock
/// that decides how many ticks each frame is owed, over fixed-size rings.
/// <para>
/// This is simulation logic, not drawing, so it lives here and is tested with a fake
/// clock. The rules that keep a long run healthy:
/// </para>
/// <list type="bullet">
/// <item><b>Fixed memory.</b> The rings are allocated once, at construction, so a
/// feed that runs for a day uses the same bytes as one that just started.</item>
/// <item><b>Ticks follow the clock, not the frame count.</b> A 50 Hz feed produces
/// 50 ticks a second at 60 or 600 fps.</item>
/// <item><b>A stall is counted, not replayed.</b> Ticks the loop could not absorb
/// are added to <see cref="Skipped"/> and dropped, because pushing six minutes of
/// ticks in one frame would stall the window longer than the sleep it recovers from.</item>
/// </list>
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
        long now = _clock.GetTimestamp();
        if (_stamp < 0)
        {
            _stamp = now;
            return;
        }

        double dt = _clock.GetElapsedTime(_stamp, now).TotalMilliseconds;
        _stamp = now;
        if (dt <= 0) return;

        IntervalMs = dt;
        if (!Live) return;

        if (dt > MaxFrameMs)
        {
            // The loop slept (hidden window, another tab, a busy machine). Count what
            // did not happen and resume at frame cadence instead of replaying it.
            Skipped += (long)(dt * TicksPerSecond / 1000.0);
            dt = AssumedFrameMs;
            _debt = 0;
        }

        _debt += dt * TicksPerSecond / 1000.0;
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

        for (var n = 0; n < due; n++)
        {
            for (var s = 0; s < History.SymbolCount; s++) History.Push(s, _source.Next(s));
            Ticks++;
        }

        double instant = due * 1000.0 / dt;
        _rate = _rate is 0 ? instant : _rate * 0.9 + instant * 0.1;
    }
}
