namespace CuiSharp.App.Services;

/// <summary>One listed symbol.</summary>
public sealed record Symbol(string Ticker, string Name, float Start);

/// <summary>A price history loaded from "the server", plus the price each series ended at.</summary>
public sealed record MarketSnapshot(float[][] History, float[] Last);

/// <summary>
/// Simulated REST history loader and zero-allocation random-walk tick source.
/// </summary>
public sealed class PriceFeed
{
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
        for (var i = 0; i < Defaults.Length; i++)
        {
            _price[i] = Defaults[i].Start;
        }
    }

    /// <summary>The symbols this feed quotes.</summary>
    public static IReadOnlyList<Symbol> Symbols => Defaults;

    /// <summary>
    /// Generates historical series on the thread pool without touching UI-thread state.
    /// </summary>
    public async Task<MarketSnapshot> LoadAsync(int pointsPerSymbol, CancellationToken cancellationToken = default)
    {
        await Task.Delay(600, cancellationToken).ConfigureAwait(false);

        // A local generator: stepping the shared one from the pool would race the UI thread.
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

    /// <summary>Continues the walk from the prices a loaded history ended at.</summary>
    public void Resume(ReadOnlySpan<float> last)
    {
        for (var i = 0; i < _price.Length && i < last.Length; i++)
        {
            _price[i] = last[i];
        }
    }

    /// <summary>Advances and returns the next price for <paramref name="symbol"/>.</summary>
    public float Next(int symbol) => _price[symbol] = Step(_price[symbol], _random);

    private static float Step(float price, Random random)
    {
        var next = price * (1 + ((float)random.NextDouble() - 0.5f) * 2 * Volatility);
        return next < 1 ? 1 : next;
    }
}
