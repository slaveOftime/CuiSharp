namespace CuiSharp.Tests;

public class CanvasRuntimeTests
{
    [Fact]
    public void DeferredPaintersUseTheirOwningSurfaceAndFreshUiValues()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var first = CuiSurface.Create(320, 200);
        using var second = CuiSurface.Create(640, 400);
        int firstPaints = 0, secondPaints = 0;
        nint firstHandle = 0, secondHandle = 0;
        nint firstPaintHandle = 0, secondPaintHandle = 0;

        // Build both before painting either. A single "last Ui" static would
        // invoke second's callback while ending first's frame.
        first.BeginFrame();
        firstHandle = first.Ui.Handle;
        first.Ui.Canvas("first", Size.Px(100), Size.Px(40), (paint, rect) =>
        {
            firstPaintHandle = paint.Handle;
            firstPaints++;
            paint.DrawRect(rect, Color.Accent);
        });
        second.BeginFrame();
        secondHandle = second.Ui.Handle;
        second.Ui.Canvas("second", Size.Px(100), Size.Px(40), (paint, rect) =>
        {
            secondPaintHandle = paint.Handle;
            secondPaints++;
            paint.DrawRect(rect, Color.Accent);
        });
        first.EndFrame();
        Assert.Equal(1, firstPaints);
        Assert.Equal(0, secondPaints);
        second.EndFrame();
        Assert.Equal(1, secondPaints);
        Assert.Equal(firstHandle, firstPaintHandle);
        Assert.Equal(secondHandle, secondPaintHandle);
        Assert.Null(UiFrameState.Painting);

        first.BeginFrame();
        first.EndFrame();
        second.BeginFrame();
        second.EndFrame();
        Assert.Equal(1, firstPaints);
        Assert.Equal(1, secondPaints);
    }

    [Fact]
    public void DisposedSurfaceCannotProduceANewUiValue()
    {
        if (!OperatingSystem.IsWindows()) return;
        var surface = CuiSurface.Create(320, 200);
        surface.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { _ = surface.Ui; });
    }
}
