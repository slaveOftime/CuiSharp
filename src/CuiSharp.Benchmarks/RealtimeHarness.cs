using System.Diagnostics;

namespace CuiSharp.Benchmarks;

/// <summary>
/// Latency-oriented harnesses for the case BenchmarkDotNet is a poor fit for:
/// a screen that must keep up a frame rate while the data changes underneath it,
/// the way a stock ticker or a monitoring dashboard does.
/// <para>
/// BenchmarkDotNet reports throughput. These report the frame-time distribution
/// and the fraction of frames that miss a budget, which is what a user feels.
/// </para>
/// </summary>
internal static class RealtimeHarness
{
    private const int Width = 1280;
    private const int Height = 720;
    private const int VisiblePoints = 600;   // the window on screen, like an intraday chart
    private const int History = 1_000_000;   // a million samples behind it

    /// <summary>
    /// Frames-per-second run: a rolling window chart that receives a new sample
    /// every frame, drawn from a one-million-point history.
    /// </summary>
    /// <param name="frames">Measured frames, after warm-up.</param>
    public static int RunFps(int frames = 600)
    {
        using var surface = CuiSurface.Create(Width, Height);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];

        float[] history = MakeWalk(History);
        float[] window = new float[VisiblePoints];
        float[] decimated = new float[SeriesDecimator.RequiredCapacity(VisiblePoints / 2)];

        // Rolling view: the last VisiblePoints samples, shifted by one per frame.
        Array.Copy(history, History - VisiblePoints, window, 0, VisiblePoints);

        // One reusable buffer: CUI is told how many points are live via Count, so
        // no slice is allocated per frame.
        var series = new[] { new Series { Values = decimated } };
        int historyCursor = History;

        void DrawOnce(bool raster, bool decimate)
        {
            if (decimate)
            {
                series[0].Count = SeriesDecimator.Decimate(window, decimated, VisiblePoints / 2);
            }
            else
            {
                series[0].Values = window;
                series[0].Count = window.Length;
            }

            var ui = surface.Ui;
            surface.BeginFrame(1f / 60f);
            using (ui.Column(new Box { Pad = 16, Gap = 8 }))
            {
                ui.Heading("Live", 3);
                using (ui.Card(new Card()))
                {
                    ui.LineChart("live", series, new ChartOpts { Height = 420, Smooth = false });
                }
                ui.Text($"last {window[^1]:F2}", new TextOpts { Tabular = true });
            }
            surface.EndFrame();

            if (raster) surface.Render(pixels);
        }

        // Warm-up: JIT, font atlas, arena growth.
        for (int i = 0; i < 120; i++) DrawOnce(raster: true, decimate: false);

        var timing = new double[frames];
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = new Stopwatch();
        long lastTicks = Stopwatch.GetTimestamp();

        for (int i = 0; i < frames; i++)
        {
            // A new tick arrives every frame, the way a feed would deliver it.
            Array.Copy(window, 1, window, 0, window.Length - 1);
            window[^1] = history[historyCursor % History];
            historyCursor++;

            long start = Stopwatch.GetTimestamp();
            DrawOnce(raster: true, decimate: true);
            timing[i] = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            lastTicks = start;
        }

        long allocatedPerFrame = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / Math.Max(1, frames);

        // Build-only figures tell you what the CPU does when the GPU has the raster.
        var buildOnly = new double[frames];
        for (int i = 0; i < frames; i++)
        {
            long start = Stopwatch.GetTimestamp();
            DrawOnce(raster: false, decimate: true);
            buildOnly[i] = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        }

        Console.WriteLine();
        Console.WriteLine($"  real-time chart, {Width}x{Height}, {VisiblePoints} point window over a {History:N0} sample feed");
        Console.WriteLine($"  {frames} frames, one new sample per frame, decimated to ~{VisiblePoints / 2 * 2} points");
        Console.WriteLine();
        Report("build + software raster", timing);
        Report("build only (GPU would render)", buildOnly);
        Console.WriteLine($"  managed allocation per frame: {allocatedPerFrame} B");
        Console.WriteLine();
        Console.WriteLine("  budget check (build + raster):");
        foreach (float target in new[] { 144f, 120f, 60f, 30f })
        {
            double budgetMs = 1000.0 / target;
            int met = timing.Count(t => t <= budgetMs);
            Console.WriteLine($"    {target,4:0} fps ({budgetMs,6:0.00} ms): {100.0 * met / timing.Length,5:0.0}% of frames fit");
        }

        _ = lastTicks;
        return 0;
    }

    /// <summary>
    /// One-shot scaling table: what a single frame costs as the series grows, raw
    /// versus decimated. Covers 1 M points, which BenchmarkDotNet would spend many
    /// minutes on.
    /// </summary>
    public static int RunScaling()
    {
        using var surface = CuiSurface.Create(Width, Height);
        float[] history = MakeWalk(1_000_000);
        var decimated = new float[SeriesDecimator.RequiredCapacity(400)];
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];

        Console.WriteLine();
        Console.WriteLine($"  chart frame cost vs series length ({Width}x{Height}, software raster)");
        Console.WriteLine();
        Console.WriteLine($"  {"points",12}  {"raw build",11}  {"raw + raster",13}  {"decimated",11}  {"decimate only",14}");
        Console.WriteLine($"  {new string('-', 12)}  {new string('-', 11)}  {new string('-', 13)}  {new string('-', 11)}  {new string('-', 14)}");

        foreach (int points in new[] { 1_000, 10_000, 100_000, 1_000_000 })
        {
            var raw = new[] { new Series { Values = history[..points] } };

            // Warm the fonts and arena at this size.
            DrawChart(surface, raw, pixels, raster: true);
            DrawChart(surface, raw, pixels, raster: true);

            double rawBuild = Measure(() => DrawChart(surface, raw, pixels, raster: false));
            double rawRaster = Measure(() => DrawChart(surface, raw, pixels, raster: true));

            int n = SeriesDecimator.Decimate(history.AsSpan(0, points), decimated, 400);
            var dec = new[] { new Series { Values = decimated[..n] } };
            DrawChart(surface, dec, pixels, raster: true);
            double decBuild = Measure(() => DrawChart(surface, dec, pixels, raster: false));
            double decOnly = Measure(() => SeriesDecimator.Decimate(history.AsSpan(0, points), decimated, 400));

            Console.WriteLine(
                $"  {points,12:N0}  {rawBuild,9:0.000} ms  {rawRaster,11:0.000} ms  {decBuild,9:0.000} ms  {decOnly,12:0.000} ms");
        }

        Console.WriteLine();
        Console.WriteLine($"  decimated output is {SeriesDecimator.RequiredCapacity(400)} points regardless of input size,");
        Console.WriteLine("  so the right column is flat and the middle column grows with the data.");
        Console.WriteLine();
        return 0;
    }

    private static void DrawChart(CuiSurface surface, Series[] series, byte[] pixels, bool raster)
    {
        surface.BeginFrame(1f / 60f);
        var ui = surface.Ui;
        using (ui.Card(new Card()))
        {
            ui.LineChart("scale", series, new ChartOpts { Height = 420 });
        }
        surface.EndFrame();
        if (raster) surface.Render(pixels);
    }

    private static double Measure(Action action, int repetitions = 5)
    {
        double best = double.MaxValue;
        for (int i = 0; i < repetitions; i++)
        {
            long start = Stopwatch.GetTimestamp();
            action();
            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            if (ms < best) best = ms;
        }

        return best;
    }

    private static void Report(string label, double[] samples)
    {
        Array.Sort(samples);
        double p50 = Percentile(samples, 0.50);
        double p95 = Percentile(samples, 0.95);
        double p99 = Percentile(samples, 0.99);
        double mean = samples.Average();
        double fps = 1000.0 / mean;

        Console.WriteLine($"  {label,-30} mean {mean,7:0.00} ms  p50 {p50,7:0.00}  p95 {p95,7:0.00}  p99 {p99,7:0.00}   ~{fps,6:0} fps");
    }

    private static double Percentile(double[] sorted, double q)
    {
        if (sorted.Length == 0) return 0;
        int index = (int)Math.Round(q * (sorted.Length - 1));
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static float[] MakeWalk(int count)
    {
        var values = new float[count];
        uint state = 987654321;
        float v = 100;
        for (int i = 0; i < count; i++)
        {
            state = state * 1664525u + 1013904223u;
            v += ((state >> 16) & 0xFF) / 255f - 0.5f;
            values[i] = v;
        }

        return values;
    }
}
