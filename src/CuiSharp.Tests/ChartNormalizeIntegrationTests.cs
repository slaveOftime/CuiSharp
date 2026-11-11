namespace CuiSharp.Tests;

/// <summary>
/// Normalization inside the real chart path. Pixel identity is the claim: a normalized
/// render must be indistinguishable from drawing the same numbers normalized by hand,
/// and the wrapper's validation must fail the frame on samples decimation would hide.
/// </summary>
public class ChartNormalizeIntegrationTests
{
    private static bool NeedsNative => OperatingSystem.IsWindows();

    [Fact]
    public void NormalizedSeriesDrawsPixelIdenticalToTheSameSeriesNormalizedByHand()
    {
        if (!NeedsNative) return;

        // [200,250,300,400] rebased to its first value is [100,125,150,200] exactly;
        // the two charts occupy equal boxes in one frame, so identical pixels is the
        // strongest statement that Index100 introduced no arithmetic or placement drift.
        float[] handNormalized = [100f, 125f, 150f, 200f];
        float[] source = [200f, 250f, 300f, 400f];

        DrawSameSlotTwice(
            ui => ui.LineChart("twin", handNormalized, Chart(NormalizeMode.None)),
            ui => ui.LineChart("twin", source, Chart(NormalizeMode.Index100)));
    }

    [Fact]
    public void PercentOnAFixedRangeDrawsPixelIdenticalToItsHandNormalizedTwin()
    {
        if (!NeedsNative) return;

        // The same fixed y range is interpreted in normalized units on both charts,
        // because Min/Max reach native untouched: that is the documented contract, and
        // pixel identity over a clamped range is what proves it.
        float[] handNormalized = [0f, 25f, 50f, 100f];
        float[] source = [200f, 250f, 300f, 400f];

        DrawSameSlotTwice(
            ui => ui.LineChart("twin", handNormalized, new ChartOpts
            {
                Height = 200,
                Width = Size.Px(280),
                Min = -20f,
                Max = 120f,
            }),
            ui => ui.LineChart("twin", source, new ChartOpts
            {
                Height = 200,
                Width = Size.Px(280),
                Min = -20f,
                Max = 120f,
                Normalize = NormalizeMode.Percent,
            }));
    }

    [Fact]
    public void EachSeriesScalesAgainstItsOwnBaseline()
    {
        if (!NeedsNative) return;

        // A $0.06T cap and a $5.1T cap on one chart: rebased independently, both are
        // [100,150], so the normalized pair must draw exactly like the hand-normalized
        // pair - same shapes, same palette, one y axis.
        var source = new Series[]
        {
            new() { Values = [100f, 150f] },
            new() { Values = [1000f, 1500f] },
        };
        var handNormalized = new Series[]
        {
            new() { Values = [100f, 150f] },
            new() { Values = [100f, 150f] },
        };

        DrawSameSlotTwice(
            ui => ui.LineChart("twin", handNormalized, Chart(NormalizeMode.None)),
            ui => ui.LineChart("twin", source, Chart(NormalizeMode.Index100)));
    }

    [Fact]
    public void TheBaselineComesFromTheCountWindowNotTheWholeArray()
    {
        if (!NeedsNative) return;
        using var surface = CuiSurface.Create(640, 240);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];

        // The window Count keeps is [0,0,0]; the nonzero values live beyond it. A baseline
        // read off the whole array would normalize happily - which would draw numbers the
        // Count window never contained - so the frame must fail instead.
        var series = new Series[] { new() { Values = [0f, 0f, 0f, 100f, 125f], Count = 3 } };

        surface.BeginFrame();
        var outer = Assert.Throws<ArgumentException>(() =>
            surface.Ui.LineChart("window", series, new ChartOpts { Normalize = NormalizeMode.Index100 }));
        surface.EndFrame();
        surface.Render(pixels);
        Assert.Contains("baseline", outer.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACountWindowDrawsItsOwnNormalizedWindow()
    {
        if (!NeedsNative) return;

        // [0,100,125] against its baseline 100 is [0,100,125]: three points, identity.
        // The twin draws those three raw; a whole-array (four-point) normalization could
        // not match it pixel for pixel.
        var source = new Series[] { new() { Values = [0f, 100f, 125f, 150f], Count = 3 } };
        var handNormalized = new Series[] { new() { Values = [0f, 100f, 125f] } };

        DrawSameSlotTwice(
            ui => ui.LineChart("twin", handNormalized, Chart(NormalizeMode.None)),
            ui => ui.LineChart("twin", source, Chart(NormalizeMode.Index100)));
    }

    [Fact]
    public void ANaNNTheDecimatorWouldHaveHiddenStillFailsTheFrame()
    {
        if (!NeedsNative) return;
        using var surface = CuiSurface.Create(320, 200);

        // One NaN among a thousand samples, with MaxPoints 8: the min/max pairs drop it
        // and the chart would draw a clean line over a hole. Validation sees the source,
        // not the decimated residue, so the hole cannot be laundered by decimation.
        var feed = new float[1000];
        for (int i = 0; i < feed.Length; i++) feed[i] = 100f + i * 0.1f;
        feed[500] = float.NaN;

        surface.BeginFrame();
        var outer = Assert.Throws<ArgumentException>(() => surface.Ui.LineChart("feed", feed, new ChartOpts
        {
            Height = 120,
            MaxPoints = 8,
            Normalize = NormalizeMode.Index100,
        }));
        surface.EndFrame();
        Assert.Contains("gap", outer.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AResultBeyondTheFloatRangeFailsTheFrame()
    {
        if (!NeedsNative) return;
        using var surface = CuiSurface.Create(320, 200);

        float[] source = [1e-30f, float.MaxValue];
        surface.BeginFrame();
        Assert.Throws<ArgumentException>(() => surface.Ui.LineChart("overflow", source, new ChartOpts
        {
            Height = 120,
            Normalize = NormalizeMode.Index100,
        }));
        surface.EndFrame();
    }

    [Fact]
    public void AnUnknownModeFailsTheFrameEvenOnAnEmptySeries()
    {
        if (!NeedsNative) return;
        using var surface = CuiSurface.Create(320, 200);

        // The enum is int-backed and arrives through an option struct; an unset or future
        // value must not take the "draws nothing" shortcut nor any other mode's math.
        surface.BeginFrame();
        Assert.Throws<ArgumentOutOfRangeException>(() => surface.Ui.LineChart("empty",
            ReadOnlySpan<float>.Empty, new ChartOpts { Normalize = (NormalizeMode)42 }));
        surface.EndFrame();

        surface.BeginFrame();
        Assert.Throws<ArgumentOutOfRangeException>(() => surface.Ui.LineChart("series",
            new Series[] { new() { Values = [1f, 2f] } }, new ChartOpts { Normalize = (NormalizeMode)(-1) }));
        surface.EndFrame();
    }

    [Fact]
    public void NormalizingALiveFeedStillAllocatesNothingPerFrame()
    {
        if (!NeedsNative) return;
        using var surface = CuiSurface.Create(320, 200);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];

        // The same million-sample shape the unnormalized 0 B benchmark draws, normalized:
        // validation runs over the source and the scale over the few hundred retained
        // points, both in the arena, so the steady state must stay 0 B.
        var feed = new float[1_000_000];
        for (int i = 0; i < feed.Length; i++) feed[i] = 1f + MathF.Sin(i * 0.001f);
        var series = new Series[] { new() { Values = feed } };

        void Frame()
        {
            surface.BeginFrame();
            surface.Ui.LineChart("feed", series, new ChartOpts { Height = 120, Normalize = NormalizeMode.Index100 });
            surface.EndFrame();
            surface.Render(pixels);
        }

        for (int i = 0; i < 10; i++) Frame();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++) Frame();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void FiveSamplesAtAnOddMaxPointsOfFiveDrawTheirTrueValues()
    {
        if (!NeedsNative) return;

        // count == MaxPoints takes the pass-through path, which once reserved only
        // RequiredCapacity(maxPoints / 2) = 4 floats and then copied 5, scribbling the
        // arena neighbor and reading garbage as the fifth value. A pass-through must
        // draw exactly the samples it was given, budget or no budget.
        float[] samples = [100f, 140f, 120f, 180f, 160f];

        DrawSameSlotTwice(
            ui => ui.LineChart("twin", samples, new ChartOpts { Height = 160, Width = Size.Px(280), MaxPoints = 5 }),
            ui => ui.LineChart("twin", samples, new ChartOpts { Height = 160, Width = Size.Px(280), MaxPoints = 100 }));
    }

    [Fact]
    public void FiveSeriesSamplesAtAnOddMaxPointsOfFiveDrawTheirTrueValues()
    {
        if (!NeedsNative) return;

        // The Series[] path shared the reservation bug; the same parity claim through
        // the multi-series marshalling the app writes most.
        var samples = new Series[] { new() { Values = [100f, 140f, 120f, 180f, 160f] } };

        DrawSameSlotTwice(
            ui => ui.LineChart("twin", samples, new ChartOpts { Height = 160, Width = Size.Px(280), MaxPoints = 5 }),
            ui => ui.LineChart("twin", samples, new ChartOpts { Height = 160, Width = Size.Px(280), MaxPoints = 100 }));
    }

    [Fact]
    public void ADecimatedFeedDrawsIdenticallyToTheSameFeedRebasedByHand()
    {
        if (!NeedsNative) return;

        // Three thousand samples into a 64-point budget: the decimator keeps a min/max
        // pair per bucket and the scale then runs over those retained points only, while
        // the twin hands over samples that are already in index units. The multipliers
        // are dyadic - 25m/64 is exact in binary - so the hand rebasing cannot land one
        // rounding step away from the framework's double-intermediate scale; any
        // disagreement can only come from normalizing the wrong samples.
        const int Samples = 3000;
        const int MaxPoints = 64;

        var source = new float[Samples];
        for (int i = 0; i < Samples; i++)
            source[i] = 256f + (i * 37 % 256) - (i % 5) * 24;
        var untouched = (float[])source.Clone();

        // The baseline is source[0] = 256, so the rebased series is v / 256 * 100.
        var rebased = new float[Samples];
        for (int i = 0; i < Samples; i++)
            rebased[i] = source[i] / 256f * 100f;

        // Two twins that were the same numbers would make pixel identity meaningless,
        // and a budget above the count would quietly test the pass-through instead.
        Assert.NotEqual(source[1], rebased[1]);
        Assert.True(SeriesDecimator.Decimate(source, MaxPoints / 2).Length < Samples,
            "this sample count and budget are not a decimated pair");

        byte[] pixels = DrawSameSlotTwice(
            ui => ui.LineChart("twin", rebased,
                new ChartOpts { Height = 160, Width = Size.Px(280), MaxPoints = MaxPoints }),
            ui => ui.LineChart("twin", source,
                new ChartOpts { Height = 160, Width = Size.Px(280), MaxPoints = MaxPoints, Normalize = NormalizeMode.Index100 }));

        // Two empty buffers are identical too; the parity has to be about drawn ink.
        Assert.True(HasInk(pixels), "the decimated chart drew nothing, so parity proves nothing");

        // The caller's array is a live feed the app owns: scaling happens on arena copies.
        Assert.Equal(untouched, source);
    }

    [Fact]
    public void ADonutRefusesANormalizeItWouldSilentlyIgnore()
    {
        if (!NeedsNative) return;
        using var surface = CuiSurface.Create(320, 200);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];

        // Slices are the values; rebasing them against a baseline is meaningless, so the
        // shared option must fail at the call rather than draw an ignored-mode donut.
        surface.BeginFrame();
        var outer = Assert.Throws<ArgumentException>(() => surface.Ui.DonutChart(
            "share", [3f, 2f, 1f], null, new ChartOpts { Normalize = NormalizeMode.Index100 }));
        surface.EndFrame();
        Assert.Contains("LineChart or BarChart", outer.Message, StringComparison.Ordinal);

        // None keeps drawing the donut it always drew.
        surface.BeginFrame();
        surface.Ui.DonutChart("share", [3f, 2f, 1f]);
        surface.EndFrame();
        surface.Render(pixels);
    }

    private static ChartOpts Chart(NormalizeMode mode) =>
        new() { Height = 200, Width = Size.Px(280), Normalize = mode };

    /// <summary>
    /// Draws one chart id in one surface slot across two runs of equal length and
    /// requires the settled frames to agree byte for byte. Same surface, same id, same
    /// rect and same animation state means exact equality is the honest bar: any
    /// difference at all can only come from the values that reached the plot.
    /// </summary>
    /// <summary>Draws both charts and returns the first run's pixels, so a caller can
    /// check that what it compared was actually a drawing.</summary>
    private static byte[] DrawSameSlotTwice(Action<Ui> first, Action<Ui> second)
    {
        using var surface = CuiSurface.Create(480, 260);
        var pa = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        var pb = new byte[surface.PixelWidth * surface.PixelHeight * 4];

        void Frame(Action<Ui> draw, byte[] into)
        {
            surface.BeginFrame();
            using (surface.Ui.Column(new Box { Pad = 8 }))
                draw(surface.Ui);
            surface.EndFrame();
            surface.Render(into);
        }

        // 12 frames each: the intro spring is at rest long before the sampled frame,
        // so only the values can make the two buffers disagree.
        for (int i = 0; i < 12; i++) Frame(first, pa);
        for (int i = 0; i < 12; i++) Frame(second, pb);

        int count = 0;
        for (int i = 0; i < pa.Length; i++)
            if (pa[i] != pb[i]) count++;
        Assert.True(count == 0, $"{count} of {pa.Length} bytes differ between raw and normalized renders");
        return pa;
    }

    /// <summary>Whether any byte differs from the top-left pixel, i.e. something was drawn.</summary>
    private static bool HasInk(byte[] pixels)
    {
        for (int i = 4; i < pixels.Length; i++)
            if (pixels[i] != pixels[i % 4]) return true;
        return false;
    }
}
