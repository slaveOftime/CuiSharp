namespace CuiSharp.Tests;

/// <summary>Normalization rescales a series against its baseline without touching the source.</summary>
public class ChartNormalizerTests
{
    [Fact]
    public void Index100OfABaseHundredSeriesIsTheIdentity()
    {
        // Rebased to its own first value, a series already starting at 100 is unchanged —
        // the double pass must not smear it with rounding.
        float[] source = [100f, 125f, 150f, 200f];
        var destination = new float[4];

        float[] expected = [100f, 125f, 150f, 200f];

        int n = ChartNormalizer.Normalize(source, destination, NormalizeMode.Index100);

        Assert.Equal(4, n);
        Assert.Equal(expected, destination);
    }

    [Fact]
    public void PercentSubtractsTheBaselineSoZeroMeansUnchanged()
    {
        float[] source = [100f, 125f, 150f, 200f];
        var destination = new float[4];

        float[] expected = [0f, 25f, 50f, 100f];

        ChartNormalizer.Normalize(source, destination, NormalizeMode.Percent);

        Assert.Equal(expected, destination);
    }

    [Fact]
    public void TheBaselineSkipsLeadingZeros()
    {
        float[] source = [0f, 0f, 50f, 25f];
        var destination = new float[4];

        float[] expected = [0f, 0f, 100f, 50f];

        ChartNormalizer.Normalize(source, destination, NormalizeMode.Index100);

        Assert.Equal(expected, destination);
    }

    [Fact]
    public void ANegativeBaselineScalesHonestlyAndInvertsDirection()
    {
        // -200 against -100 is twice the magnitude (200), +50 against -100 is -50:
        // the arithmetic is the documented one even where the semantics need a footnote.
        float[] source = [-100f, -200f, 50f];
        var destination = new float[3];

        float[] expected = [100f, 200f, -50f];

        ChartNormalizer.Normalize(source, destination, NormalizeMode.Index100);

        Assert.Equal(expected, destination);
    }

    [Fact]
    public void AFailedNormalizationIsAnArgumentErrorNotARawSeries()
    {
        var destination = new float[8];

        // A series of zeros has no baseline; passing it through raw would mix unnormalized
        // units into a chart of normalized siblings.
        Assert.Throws<ArgumentException>(() => ChartNormalizer.Normalize([0f, 0f, 0f], destination, NormalizeMode.Index100));
        Assert.Throws<ArgumentException>(() => ChartNormalizer.Normalize([float.NaN, 5f], destination, NormalizeMode.Percent));
        Assert.Throws<ArgumentException>(() => ChartNormalizer.Normalize([float.PositiveInfinity], destination, NormalizeMode.Index100));
    }

    [Fact]
    public void NaNInTheMiddleOfASeriesIsRejectedNotImpliedAsAGap()
    {
        float[] source = [100f, float.NaN, 150f];
        var destination = new float[3];

        Assert.Throws<ArgumentException>(() => ChartNormalizer.Normalize(source, destination, NormalizeMode.Index100));
    }

    [Fact]
    public void AResultBeyondTheFiniteRangeIsRejected()
    {
        // 3.4e38 / 1e-30 * 100 fits a double and overflows a float: the contract is that no
        // nonfinite number reaches the chart, whichever side the range is left from.
        float[] source = [1e-30f, float.MaxValue];
        var destination = new float[2];

        Assert.Throws<ArgumentException>(() => ChartNormalizer.Normalize(source, destination, NormalizeMode.Index100));
    }

    [Fact]
    public void TheSourceSeriesIsNeverMutated()
    {
        float[] source = [100f, 125f, 150f, 200f];
        float[] snapshot = [.. source];

        ChartNormalizer.Normalize(source, new float[4], NormalizeMode.Index100);
        ChartNormalizer.Normalize(source, new float[4], NormalizeMode.Percent);

        Assert.Equal(snapshot, source);
    }

    [Fact]
    public void NoneCopiesEverythingIncludingWhatNormalizationRejects()
    {
        // Opting out must be exactly the old pass-through: a NaN-only-plus-values series
        // copies bit for bit, because None asks for no behavior at all.
        float[] source = [1f, float.NaN, 3f];
        var destination = new float[3];

        int n = ChartNormalizer.Normalize(source, destination, NormalizeMode.None);

        Assert.Equal(3, n);
        Assert.Equal(1f, destination[0]);
        Assert.True(float.IsNaN(destination[1]));
        Assert.Equal(3f, destination[2]);
    }

    [Fact]
    public void TheEmptySeriesWritesNothingInEveryMode()
    {
        float[] destination = [1f, 2f, 3f];
        float[] unchanged = [1f, 2f, 3f];

        foreach (NormalizeMode mode in Enum.GetValues<NormalizeMode>())
            Assert.Equal(0, ChartNormalizer.Normalize(ReadOnlySpan<float>.Empty, destination, mode));

        // "Nothing to draw" must not scribble on the caller's buffer either.
        Assert.Equal(unchanged, destination);
    }

    [Fact]
    public void RejectsAnUndersizedDestination()
    {
        Assert.Throws<ArgumentException>(() => ChartNormalizer.Normalize([1f, 2f, 3f], new float[2], NormalizeMode.None));
        Assert.Throws<ArgumentException>(() => ChartNormalizer.Normalize([1f, 2f, 3f], new float[2], NormalizeMode.Index100));
    }

    [Fact]
    public void FindBaselineReportsTheNumberTheScaleWillUse()
    {
        Assert.Equal(7.5f, ChartNormalizer.FindBaseline([0f, 0f, 7.5f, -3f]));
        Assert.Throws<ArgumentException>(() => ChartNormalizer.FindBaseline([]));
        Assert.Throws<ArgumentException>(() => ChartNormalizer.FindBaseline([0f, 0f]));
    }

    [Fact]
    public void TheScaleIsOneDoublePassNotTwoFloatRounds()
    {
        // 1/3 in float then times 100 in float drifts off (double)1/3*100 rounded once;
        // the exact expected value pins which arithmetic actually ran.
        float[] source = [3f, 1f];
        var destination = new float[2];

        ChartNormalizer.Normalize(source, destination, NormalizeMode.Index100);

        Assert.Equal(100f, destination[0]);
        Assert.Equal((float)((double)1 / 3 * 100), destination[1]);
    }

    [Fact]
    public void AnUnknownModeIsRejectedRatherThanDrawnAsPercent()
    {
        // The enum is int-backed and crosses an option struct, so an unset or future value
        // must fail loudly instead of quietly scaling like Percent.
        var destination = new float[4];
        Assert.Throws<ArgumentOutOfRangeException>(() => ChartNormalizer.Normalize([100f, 125f], destination, (NormalizeMode)42));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChartNormalizer.Normalize(ReadOnlySpan<float>.Empty, destination, (NormalizeMode)42));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChartNormalizer.Normalize([100f, 125f], destination, (NormalizeMode)(-1)));
    }

    [Fact]
    public void NormalizingIntoAReusedBufferAllocatesNothing()
    {
        var source = new float[10_000];
        var destination = new float[source.Length];
        for (int i = 0; i < source.Length; i++) source[i] = 1f + i * 1e-4f;

        ChartNormalizer.Normalize(source, destination, NormalizeMode.Index100); // warm the paths

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int pass = 0; pass < 100; pass++)
            ChartNormalizer.Normalize(source, destination, NormalizeMode.Index100);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }
}
