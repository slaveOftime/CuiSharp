using CuiSharp.Testing;

namespace CuiSharp.Tests;

/// <summary>
/// Reading a per-frame allocation budget out of a noisy process: tests run in parallel, so one
/// batch can straddle an allocation that has nothing to do with the frame being measured.
/// </summary>
internal static class AllocationStats
{
    /// <summary>
    /// Requires ten consecutive frames that allocate nothing, after settling.
    /// </summary>
    /// <remarks>
    /// A first use anywhere in the process — a formatter cache, a font probe, the UTF-8 arena
    /// growing a chunk — is charged to whichever draw reaches it first, and that draw is not
    /// necessarily the page's own cost. The settle loop separates those from a per-frame cost: a
    /// page that allocates every frame never settles and fails, and any frame that allocates after
    /// settling still fails. See docs/PLAN.md for the allocation this was first seen as.
    /// </remarks>
    internal static void AssertSteadyStateAllocatesNothing(this TestApp app)
    {
        int quiet = 0;
        for (int i = 0; i < 120 && quiet < 2; i++)
        {
            app.Frame();
            quiet = app.Surface.LastFrame.AllocatedBytes == 0 ? quiet + 1 : 0;
        }
        Assert.True(quiet == 2, "the frames never settled: two consecutive frames allocated nothing at most after 120");
        for (int i = 0; i < 10; i++)
        {
            app.Frame();
            Assert.Equal(0, app.Surface.LastFrame.AllocatedBytes);
        }
    }

    /// <summary>The middle value: one noisy batch cannot move it.</summary>
    internal static long Median(this long[] samples)
    {
        var sorted = (long[])samples.Clone();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }
}
