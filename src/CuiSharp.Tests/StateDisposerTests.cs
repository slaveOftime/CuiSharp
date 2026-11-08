using System.Runtime.InteropServices;

namespace CuiSharp.Tests;

/// <summary>
/// Per-id state with a disposer: CUI calls the managed callback when it throws the state
/// away, which is the only way a widget can release what that state owns.
/// </summary>
public class StateDisposerTests
{
    [Fact]
    public void TheDisposerRunsWhenTheContextGoesAway()
    {
        if (!OperatingSystem.IsWindows()) return;

        int freed = 0;
        var surface = CuiSurface.Create(320, 200);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        int counterBefore = -1;

        surface.Draw(ui =>
        {
            Span<int> state = ui.State<int>("owned", () => freed++);
            counterBefore = state[0];
            state[0]++;
        }, pixels);

        Assert.Equal(0, counterBefore);
        Assert.Equal(0, freed);

        // The value persists to the next frame, and the callback does not run yet.
        surface.Draw(ui => ui.State<int>("owned", () => freed++)[0]++, pixels);
        Assert.Equal(0, freed);

        // Releasing the context frees the state, so the callback runs exactly once.
        surface.Dispose();
        Assert.Equal(1, freed);
    }

    [Fact]
    public void TwoIdsKeepSeparateStateAndDisposers()
    {
        if (!OperatingSystem.IsWindows()) return;

        int freedA = 0, freedB = 0;
        var surface = CuiSurface.Create(320, 200);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];

        surface.Draw(ui =>
        {
            ui.State<int>("a", () => freedA++)[0] = 7;
            ui.State<int>("b", () => freedB++)[0] = 9;
        }, pixels);

        var values = new int[2];
        surface.Draw(ui =>
        {
            values[0] = ui.State<int>("a", () => freedA++)[0];
            values[1] = ui.State<int>("b", () => freedB++)[0];
        }, pixels);

        Assert.Equal(7, values[0]);
        Assert.Equal(9, values[1]);
        Assert.Equal(0, freedA);
        Assert.Equal(0, freedB);

        surface.Dispose();
        Assert.Equal(1, freedA);
        Assert.Equal(1, freedB);
    }
}
