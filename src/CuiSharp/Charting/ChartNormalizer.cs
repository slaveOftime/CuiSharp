namespace CuiSharp;

/// <summary>
/// How a chart scales each series against its own baseline, so series of very different
/// magnitudes can be compared by shape. A baseline is the first finite nonzero sample of
/// the series; see <see cref="ChartNormalizer"/> for the exact rules.
/// </summary>
public enum NormalizeMode
{
    /// <summary>No scaling: the samples are drawn as they are.</summary>
    None,

    /// <summary>Rebase to 100 at the baseline, like a total-return index: 100 is "unchanged".</summary>
    Index100,

    /// <summary>Percent change from the baseline: 0 is "unchanged".</summary>
    Percent,
}

/// <summary>
/// Rescales a series against its baseline so several series share a comparable y range.
/// <para>
/// The baseline is the first finite nonzero sample. A negative baseline is allowed and
/// inverts the scale (-200 against -100 is Index100 200, not 50). The math is done in
/// <c>double</c> and rounded to <c>float</c> once, so scaling does not compound rounding.
/// </para>
/// <para>
/// A requested normalization rejects samples it cannot scale: NaN or infinity anywhere in
/// the series, a series with no finite nonzero baseline (all zero, say), and results beyond
/// the finite <c>float</c> range all throw <see cref="System.ArgumentException"/> rather
/// than draw a line that silently lies. NaN is not a gap marker; the renderer has no gap
/// concept.
/// </para>
/// <para>
/// Copy-based and allocation-free like <see cref="SeriesDecimator"/>: the source is never
/// mutated, results go into a caller-owned destination, and reuse keeps a frame at 0 B.
/// The empty series is not an error — it means "nothing to draw" and returns 0.
/// <see cref="NormalizeMode.None"/> keeps the old pass-through behavior, validation
/// included, so opting out opts out of everything.
/// </para>
/// </summary>
public static class ChartNormalizer
{
    /// <summary>
    /// The baseline a normalization would use: the first nonzero sample of
    /// <paramref name="values"/>.
    /// </summary>
    /// <param name="values">The window to scale, already clipped to the points that will draw.</param>
    /// <exception cref="System.ArgumentException">
    /// The span is empty, holds a nonfinite value at or before the baseline, or holds no
    /// nonzero value at all.
    /// </exception>
    public static float FindBaseline(ReadOnlySpan<float> values)
    {
        foreach (float v in values)
        {
            if (!float.IsFinite(v))
                throw new ArgumentException(
                    "a normalized series must not contain NaN or infinity; NaN is not a gap marker", nameof(values));
            if (v != 0f)
                return v;
        }

        throw values.IsEmpty
            ? new ArgumentException("an empty series has no baseline", nameof(values))
            : new ArgumentException("a normalized series needs a finite nonzero baseline; a series of zeros has none", nameof(values));
    }

    /// <summary>
    /// Writes <paramref name="values"/> scaled against their baseline into
    /// <paramref name="destination"/> and returns the number of points written.
    /// </summary>
    /// <param name="values">
    /// The samples to scale, already clipped to the points that will draw. Never mutated.
    /// </param>
    /// <param name="destination">
    /// Output with room for one point per sample. Contents are unspecified if a throw
    /// interrupts the pass.
    /// </param>
    /// <param name="mode">How to scale. <see cref="NormalizeMode.None"/> is a plain copy.</param>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="destination"/> is too small, <paramref name="mode"/> is not a
    /// <see cref="NormalizeMode"/> value, or <paramref name="mode"/> is not
    /// <see cref="NormalizeMode.None"/> and the series has no finite nonzero baseline, holds
    /// NaN or infinity, or produces a result beyond the finite <c>float</c> range.
    /// </exception>
    public static int Normalize(ReadOnlySpan<float> values, Span<float> destination, NormalizeMode mode)
    {
        if (mode is not (NormalizeMode.None or NormalizeMode.Index100 or NormalizeMode.Percent))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "not a NormalizeMode value");
        if (values.IsEmpty) return 0;
        if (destination.Length < values.Length)
            throw new ArgumentException("destination needs one point per source value", nameof(destination));

        if (mode == NormalizeMode.None)
        {
            // Opting out opts out of everything, validation included: this is exactly what
            // an unnormalized chart received before normalization existed.
            values.CopyTo(destination);
            return values.Length;
        }

        float baseline = ValidateAndFindBaseline(values);
        values.CopyTo(destination);
        Scale(destination[..values.Length], baseline, mode);
        return values.Length;
    }

    /// <summary>
    /// Throws unless <paramref name="mode"/> is a real <see cref="NormalizeMode"/> value.
    /// The chart path checks once per call, before any empty shortcut, so an unset or
    /// unknown mode cannot take a fast path around validation.
    /// </summary>
    internal static void ValidateMode(NormalizeMode mode) =>
        _ = mode is NormalizeMode.None or NormalizeMode.Index100 or NormalizeMode.Percent
            ? 0
            : throw new ArgumentOutOfRangeException(nameof(mode), mode, "not a NormalizeMode value");

    /// <summary>
    /// Validates a whole series for normalization and returns the baseline it would use:
    /// the first nonzero sample. The whole span is scanned because a chart may decimate
    /// a sample away, and a value decimation would hide is still a value the caller's
    /// normalization was asked for.
    /// </summary>
    internal static float ValidateAndFindBaseline(ReadOnlySpan<float> values)
    {
        float baseline = 0f;
        bool found = false;
        foreach (float v in values)
        {
            if (!float.IsFinite(v))
                throw new ArgumentException(
                    "a normalized series must not contain NaN or infinity; NaN is not a gap marker", nameof(values));
            if (!found && v != 0f)
            {
                baseline = v;
                found = true;
            }
        }

        if (!found)
            throw new ArgumentException("a normalized series needs a finite nonzero baseline; a series of zeros has none", nameof(values));
        return baseline;
    }

    /// <summary>
    /// Rescales already-retained samples in place against a validated baseline. The samples
    /// are the ones a chart will actually draw - typically decimated output - so the scale
    /// runs over a few hundred points whatever the source length was.
    /// </summary>
    internal static void Scale(Span<float> points, float baseline, NormalizeMode mode)
    {
        double b = baseline;
        for (int i = 0; i < points.Length; i++)
        {
            // double throughout: a float chain would round at the divide and again at the
            // multiply, and Index100 would fail to be the identity it promises at b = 100.
            double ratio = (double)points[i] / b;
            double result = mode == NormalizeMode.Index100 ? ratio * 100.0 : (ratio - 1.0) * 100.0;
            if (!double.IsFinite(result) || result > float.MaxValue || result < float.MinValue)
                throw new ArgumentException(
                    $"scaling {points[i]} against baseline {b:F6} leaves the finite range");

            points[i] = (float)result;
        }
    }
}
