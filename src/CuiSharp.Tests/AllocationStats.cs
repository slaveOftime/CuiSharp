namespace CuiSharp.Tests;

/// <summary>
/// Reading a per-frame allocation budget out of a noisy process: tests run in parallel, so one
/// batch can straddle an allocation that has nothing to do with the frame being measured.
/// </summary>
internal static class AllocationStats
{
    /// <summary>The middle value: one noisy batch cannot move it.</summary>
    internal static long Median(this long[] samples)
    {
        var sorted = (long[])samples.Clone();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }
}
