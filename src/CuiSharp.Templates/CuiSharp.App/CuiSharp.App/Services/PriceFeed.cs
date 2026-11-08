namespace CuiSharp.App.Services;

/// <summary>One listed symbol.</summary>
public sealed record Symbol(string Ticker, string Name, float Start);

/// <summary>A price history loaded from "the server", plus the price each series ended at.</summary>
public sealed record MarketSnapshot(float[][] History, float[] Last);

/// <summary>
/// A stand-in for a websocket plus a REST history call. Its shape is what matters:
/// <list type="bullet">
/// <item><see cref="LoadAsync"/> returns a <see cref="Task"/>. Replacing its delay
/// with an <c>HttpClient</c> call changes nothing else.</item>
/// <item><see cref="Next"/> is the tick generator. A real feed would push into a
/// queue from a socket thread instead, and the page would drain it the same way.</item>
/// </list>
/// Nothing here touches CuiSharp, and nothing allocates per tick.
/// </summary>
public sealed class PriceFeed
{
    /// <summary>Fixed seed, so a run can be compared with the previous one.</summary>
    private const int Seed = 20261108;

    private const float Volatility = 0.0016f;

    private static readonly Symbol[] Defaults =
    [
        new("NVDA", "NVIDIA", 128.40f),
        new("MSFT", "Microsoft", 431.20f),
        new("AAPL", "Apple", 219.75f),
        new("AMD", "AMD", 158.30f),
        new("GOOGL", "Alphabet", 176.90f),
        new("AMZN", "Amazon", 204.15f),
        new("META", "Meta", 592.60f),
        new("TSLA", "Tesla", 249.05f),
    ];

    private readonly Random _random = new(Seed);
    private readonly float[] _price = new float[Defaults.Length];

    public PriceFeed()
    {
        for (var i = 0; i < Defaults.Length; i++) _price[i] = Defaults[i].Start;
    }

    /// <summary>The symbols this feed quotes.</summary>
    public static IReadOnlyList<Symbol> Symbols => Defaults;

    /// <summary>
    /// Pretends to fetch a price history. The work runs on the pool and touches no
    /// shared state; <see cref="Resume"/> applies the last prices on the thread that
    /// owns the feed.
    /// </summary>
    public async Task<MarketSnapshot> LoadAsync(int pointsPerSymbol, CancellationToken cancellationToken = default)
    {
        await Task.Delay(600, cancellationToken).ConfigureAwait(false);   // the "network"

        // A local generator: the shared one belongs to the UI thread, and two
        // threads stepping the same Random is a data race, not a detail.
        var random = new Random(Seed);
        var history = new float[Defaults.Length][];
        var last = new float[Defaults.Length];
        for (var s = 0; s < Defaults.Length; s++)
        {
            var series = new float[pointsPerSymbol];
            var price = Defaults[s].Start;
            for (var i = 0; i < series.Length; i++)
            {
                price = Step(price, random);
                series[i] = price;
            }
            history[s] = series;
            last[s] = price;
        }

        return new MarketSnapshot(history, last);
    }

    /// <summary>
    /// Continues from the prices a history ended at, so the first live tick does not
    /// jump back to the symbol's opening price.
    /// </summary>
    public void Resume(ReadOnlySpan<float> last)
    {
        for (var i = 0; i < _price.Length && i < last.Length; i++) _price[i] = last[i];
    }

    /// <summary>The next tick for one symbol: a small random walk.</summary>
    public float Next(int symbol) => _price[symbol] = Step(_price[symbol], _random);

    // One step of the walk, multiplicative so a price can never cross zero. Cheap on
    // purpose: it runs 50 times a second per symbol, and a real feed can run faster.
    private static float Step(float price, Random random)
    {
        var next = price * (1 + ((float)random.NextDouble() - 0.5f) * 2 * Volatility);
        return next < 1 ? 1 : next;
    }
}
