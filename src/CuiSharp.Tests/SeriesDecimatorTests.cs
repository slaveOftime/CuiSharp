namespace CuiSharp.Tests;

/// <summary>Decimation keeps the shape of a long series while cutting point count.</summary>
public class SeriesDecimatorTests
{
    [Fact]
    public void ShortSeriesPassesThroughUnchanged()
    {
        float[] source = [1f, 2f, 3f];
        var destination = new float[SeriesDecimator.RequiredCapacity(10)];

        int n = SeriesDecimator.Decimate(source, destination, 10);

        Assert.Equal(3, n);
        Assert.Equal(source, destination[..n]);
    }

    [Fact]
    public void MillionPointsCollapseToTwoPerBucket()
    {
        var source = new float[1_000_000];
        for (int i = 0; i < source.Length; i++) source[i] = MathF.Sin(i * 0.001f);

        var destination = new float[SeriesDecimator.RequiredCapacity(400)];
        int n = SeriesDecimator.Decimate(source, destination, 400);

        Assert.InRange(n, 1, 800);
        Assert.True(n <= 800);
    }

    [Fact]
    public void PreservesSpikesThatAveragingWouldErase()
    {
        // Flat line with one spike in the middle third.
        var source = new float[3000];
        source[1500] = 99f;

        var destination = new float[SeriesDecimator.RequiredCapacity(30)];
        int n = SeriesDecimator.Decimate(source, destination, 30);

        Assert.Contains(99f, destination[..n].ToArray());
    }

    [Fact]
    public void KeepsMinBeforeMaxWhenItOccurredFirst()
    {
        // Each bucket: low then high.
        var source = new float[100];
        for (int i = 0; i < source.Length; i++) source[i] = i % 2 == 0 ? 0f : 10f;

        var destination = new float[SeriesDecimator.RequiredCapacity(5)];
        int n = SeriesDecimator.Decimate(source, destination, 5);

        for (int i = 0; i + 1 < n; i += 2)
            Assert.True(destination[i] <= destination[i + 1]);
    }

    [Fact]
    public void RejectsAnUndersizedDestination()
    {
        var source = new float[10_000];
        var tooSmall = new float[10];

        Assert.Throws<ArgumentException>(() => SeriesDecimator.Decimate(source, tooSmall, 100));
    }

    [Fact]
    public void EmptySourceWritesNothing()
    {
        var destination = new float[8];
        Assert.Equal(0, SeriesDecimator.Decimate(ReadOnlySpan<float>.Empty, destination, 4));
    }
}
