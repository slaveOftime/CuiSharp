namespace CuiSharp;

/// <summary>
/// Reduces a long series to something a chart can draw in real time.
/// <para>
/// A chart is a few hundred pixels wide, so points beyond roughly two per pixel
/// cannot be seen — but they still cost geometry. This collapses each bucket of
/// consecutive samples into its minimum and its maximum, in the order they
/// occurred, which keeps spikes and gaps that plain averaging would erase.
/// That is what financial and monitoring charts do.
/// </para>
/// <para>
/// Copy-based and allocation-free: pass a destination span and reuse it every
/// frame. See <see cref="RequiredCapacity"/>.
/// </para>
/// </summary>
public static class SeriesDecimator
{
    /// <summary>Points written at most for a given bucket count.</summary>
    public static int RequiredCapacity(int buckets) => buckets * 2;

    /// <summary>
    /// Decimates <paramref name="source"/> into <paramref name="destination"/>.
    /// Returns the number of points written, which is <c>0</c>, <c>source.Length</c>
    /// when it already fits, or an even number up to <c>buckets * 2</c>.
    /// </summary>
    /// <param name="source">Raw samples, oldest first.</param>
    /// <param name="destination">
    /// Output buffer. Must hold at least <see cref="RequiredCapacity"/> points, or
    /// <c>source.Length</c> when the source is shorter.
    /// </param>
    /// <param name="buckets">
    /// Roughly the number of pixels the chart is wide. Two points are emitted per
    /// bucket at most, so pass <c>width / 2</c> to end up with about one point per
    /// pixel.
    /// </param>
    public static int Decimate(ReadOnlySpan<float> source, Span<float> destination, int buckets)
    {
        if (buckets < 1) buckets = 1;
        if (source.IsEmpty) return 0;

        // Already sparse enough: hand it back untouched.
        if (source.Length <= buckets * 2)
        {
            if (destination.Length < source.Length)
                throw new ArgumentException("destination too small for the source", nameof(destination));

            source.CopyTo(destination);
            return source.Length;
        }

        int capacity = RequiredCapacity(buckets);
        if (destination.Length < capacity)
            throw new ArgumentException($"destination needs {capacity} points", nameof(destination));

        int written = 0;
        for (int bucket = 0; bucket < buckets; bucket++)
        {
            int start = (int)((long)source.Length * bucket / buckets);
            int end = (int)((long)source.Length * (bucket + 1) / buckets);
            if (end <= start) continue;

            int minIndex = start, maxIndex = start;
            float min = source[start], max = source[start];
            for (int i = start + 1; i < end; i++)
            {
                float v = source[i];
                if (v < min) { min = v; minIndex = i; }
                if (v > max) { max = v; maxIndex = i; }
            }

            // Emit in the order they occurred so the line does not zig-zag.
            if (minIndex <= maxIndex)
            {
                destination[written++] = min;
                if (maxIndex != minIndex) destination[written++] = max;
            }
            else
            {
                destination[written++] = max;
                destination[written++] = min;
            }
        }

        return written;
    }

    /// <summary>
    /// Convenience overload for callers that would rather not size a buffer.
    /// Allocates; prefer the span overload inside a frame.
    /// </summary>
    public static float[] Decimate(ReadOnlySpan<float> source, int buckets)
    {
        var result = new float[ReadyCapacity(source, buckets)];
        int n = Decimate(source, result, buckets);
        if (n == result.Length) return result;

        var trimmed = new float[n];
        Array.Copy(result, trimmed, n);
        return trimmed;
    }

    private static int ReadyCapacity(ReadOnlySpan<float> source, int buckets) =>
        Math.Min(source.Length, RequiredCapacity(Math.Max(1, buckets)));
}
