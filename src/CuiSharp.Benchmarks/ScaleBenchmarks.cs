using BenchmarkDotNet.Attributes;

namespace CuiSharp.Benchmarks;

/// <summary>
/// How cost scales with data volume — the question that decides whether an
/// immediate-mode UI can show this screen at all.
/// <para>
/// Two different shapes of "big data":
/// </para>
/// <list type="bullet">
/// <item><b>Rows</b> go through <c>cui_virtual_list</c>, which is expected to be
/// O(visible) and therefore flat in the row count.</item>
/// <item><b>Chart points</b> go through <c>cui_line_chart_ex</c>, which is O(n):
/// every point becomes geometry. That is why a decimator exists.</item>
/// </list>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 8)]
public class ScaleBenchmarks
{
    private CuiSurface _surface = null!;
    private float[] _series = null!;
    private float[] _decimated = null!;

    [Params(1_000, 100_000, 1_000_000)]
    public int Rows;

    [Params(1_000, 20_000, 200_000)]
    public int Points;

    [GlobalSetup]
    public void Setup()
    {
        _surface = CuiSurface.Create(1280, 720);

        // A realistic intraday-ish walk, deterministic so runs are comparable.
        _series = new float[Math.Max(1_000, 200_000)];
        uint state = 12345;
        float v = 100;
        for (int i = 0; i < _series.Length; i++)
        {
            state = state * 1664525u + 1013904223u;
            v += ((state >> 16) & 0xFF) / 255f - 0.5f;
            _series[i] = v;
        }

        _decimated = new float[SeriesDecimator.RequiredCapacity(400)];
    }

    [GlobalCleanup]
    public void Cleanup() => _surface.Dispose();

    /// <summary>
    /// A million rows in a virtual list: only the visible ~30 rows are built, so
    /// this should cost the same as a thousand.
    /// </summary>
    [Benchmark]
    public void VirtualList()
    {
        var ui = _surface.Ui;
        _surface.BeginFrame();
        using (ui.Column(new Box { Pad = 16, Gap = 2 }))
        using (ui.VirtualList("rows", Rows, 24, Size.Px(600), out int first, out int last))
        {
            for (int i = first; i < last; i++)
            {
                ui.Text("row", new TextOpts { Width = Size.Fill, NoWrap = true });
            }
        }
        _surface.EndFrame();
    }

    /// <summary>Every point becomes geometry: linear in <see cref="Points"/>.</summary>
    [Benchmark]
    public void ChartRaw()
    {
        var ui = _surface.Ui;
        var series = new[] { new Series { Values = _series[..Points] } };
        _surface.BeginFrame();
        using (ui.Card(new Card()))
        {
            ui.LineChart("raw", series, new ChartOpts { Height = 300 });
        }
        _surface.EndFrame();
    }

    /// <summary>Same data collapsed to ~800 points first: flat in <see cref="Points"/>.</summary>
    [Benchmark]
    public void ChartDecimated()
    {
        var ui = _surface.Ui;
        int n = SeriesDecimator.Decimate(_series.AsSpan(0, Points), _decimated, 400);
        var series = new[] { new Series { Values = _decimated[..n] } };
        _surface.BeginFrame();
        using (ui.Card(new Card()))
        {
            ui.LineChart("dec", series, new ChartOpts { Height = 300 });
        }
        _surface.EndFrame();
    }

    /// <summary>The decimation itself, with no drawing.</summary>
    [Benchmark]
    public int DecimateOnly() => SeriesDecimator.Decimate(_series.AsSpan(0, Points), _decimated, 400);
}
