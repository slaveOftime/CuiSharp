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
        Assert.Same(second.Ui.FrameState, UiFrameState.Drawing);
        Assert.Equal(1, firstPaints);
        Assert.Equal(0, secondPaints);
        second.EndFrame();
        Assert.Equal(1, secondPaints);
        Assert.Equal(firstHandle, firstPaintHandle);
        Assert.Equal(secondHandle, secondPaintHandle);
        Assert.Null(UiFrameState.Painting);
        Assert.Null(UiFrameState.Drawing);

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

    [Fact]
    public void ScopedCanvasesWithTheSameLocalIdsOnlyTakeTheirOwnWheel()
    {
        using var surface = CuiSurface.Create(320, 240);
        var boxes = new RectF[2];
        var takes = new bool[2];
        var deltas = new (float X, float Y)[2];

        void DrawPlot(Ui ui, int key)
        {
            using var scope = ui.Id(key);
            uint wheelId = ui.MakeId("plot-wheel");
            var canvas = ui.Canvas("plot", Size.Px(200), Size.Px(80), (paint, box) =>
            {
                boxes[key] = box;
                paint.WheelRegion(wheelId, box);
                paint.DrawRect(box, Color.Surface);
            });
            if (canvas.Hovered) ui.Cursor = Cursor.Move;
            takes[key] = ui.TryTakeWheel("plot-wheel", out deltas[key]);
            Assert.False(ui.TryTakeWheel(wheelId, out _));
        }

        void DrawFrame()
        {
            surface.BeginFrame();
            DrawPlot(surface.Ui, 0);
            DrawPlot(surface.Ui, 1);
            surface.EndFrame();
        }

        DrawFrame();
        Assert.Equal(new[] { false, false }, takes);
        for (int target = 0; target < boxes.Length; target++)
        {
            var box = boxes[target];
            surface.MouseMove(box.X + box.W / 2, box.Y + box.H / 2);
            surface.Scroll(3f, -1f);
            DrawFrame();

            Assert.True(takes[target]);
            Assert.False(takes[1 - target]);
            Assert.Equal((-1f, 3f), deltas[target]);
            Assert.Equal(Cursor.Move, surface.Ui.Cursor);
        }
    }

    [Fact]
    public void AComponentInputCanvasTakesFocusAndLosesEligibilityWhenUnfocused()
    {
        using var surface = CuiSurface.Create(320, 200);
        surface.Focus(true);
        bool focusRingVisible = false;
        uint editorId = 0;

        void Draw(Ui ui, bool requestFocus)
        {
            ui.Button("Before");
            using var scope = ui.Id(42);
            editorId = ui.MakeId("editor");
            uint paintId = editorId;
            ui.Canvas("editor", Size.Px(180), Size.Px(80), (paint, box) =>
            {
                focusRingVisible = paint.IsFocusVisible(paintId);
                paint.DrawRect(box, Color.Surface);
            }, new CanvasOpts { ComponentInput = true });
            if (requestFocus) ui.RequestComponentFocus("editor");
        }

        // RequestComponentFocus takes effect when the frame ends.
        surface.BeginFrame();
        Draw(surface.Ui, requestFocus: true);
        surface.EndFrame();

        Assert.True(surface.Ui.IsWindowFocused);
        Assert.Equal(editorId, surface.Ui.FocusedId);
        Assert.True(surface.Ui.ComponentHasInput(editorId));

        // Losing window focus revokes component-input eligibility even while FocusedId stays set.
        surface.Focus(false);
        Assert.False(surface.Ui.IsWindowFocused);
        Assert.Equal(editorId, surface.Ui.FocusedId);
        Assert.False(surface.Ui.ComponentHasInput(editorId));

        // Tabbing into the canvas with window focus shows the keyboard focus ring to its painter.
        surface.Focus(true);
        surface.KeyDown(Key.Tab, Mods.Shift);
        surface.KeyUp(Key.Tab, Mods.Shift);
        surface.BeginFrame();
        Draw(surface.Ui, requestFocus: false);
        surface.EndFrame();

        surface.KeyDown(Key.Tab);
        surface.KeyUp(Key.Tab);
        surface.BeginFrame();
        Draw(surface.Ui, requestFocus: false);
        surface.EndFrame();
        Assert.True(focusRingVisible);
    }

    [Fact]
    public void SignaturePointsReadsDrawnStrokesAndClearResetsThem()
    {
        using var surface = CuiSurface.Create(400, 240);
        int pointsCount = -1, strokesCount = -1;

        void Draw(Ui ui, bool clear = false)
        {
            ui.SignaturePad("sig", new SignatureOpts { Width = Size.Px(300), Height = 160 });
            if (clear) ui.SignatureClear("sig");
            pointsCount = ui.SignaturePoints("sig", out strokesCount);
        }

        // Frame 1 lays out the signature pad.
        surface.BeginFrame();
        Draw(surface.Ui);
        surface.EndFrame();
        Assert.Equal(0, pointsCount);
        Assert.Equal(0, strokesCount);

        // Stroke 1: press, drag, release inside the pad.
        surface.MouseMove(40, 40);
        surface.MouseDown();
        surface.BeginFrame();
        Draw(surface.Ui);
        surface.EndFrame();

        surface.MouseMove(90, 70);
        surface.BeginFrame();
        Draw(surface.Ui);
        surface.EndFrame();

        surface.MouseUp();
        surface.BeginFrame();
        Draw(surface.Ui);
        surface.EndFrame();

        // Stroke 2: a second stroke so strokeEnds has two entries.
        surface.MouseMove(120, 50);
        surface.MouseDown();
        surface.BeginFrame();
        Draw(surface.Ui);
        surface.EndFrame();

        surface.MouseMove(170, 95);
        surface.BeginFrame();
        Draw(surface.Ui);
        surface.EndFrame();

        surface.MouseUp();
        surface.BeginFrame();
        Draw(surface.Ui);
        surface.EndFrame();

        Assert.Equal(2, strokesCount);
        Assert.True(pointsCount >= 4);

        Span<(float X, float Y)> pts = stackalloc (float X, float Y)[16];
        Span<int> ends = stackalloc int[4];
        surface.BeginFrame();
        int copied = surface.Ui.SignaturePoints("sig", out int copiedStrokes, pts, ends);
        surface.EndFrame();

        Assert.Equal(pointsCount, copied);
        Assert.Equal(2, copiedStrokes);
        Assert.True(ends[0] >= 2 && ends[0] < copied);
        Assert.Equal(copied, ends[1]);
        Assert.True(pts[0].X > 0 && pts[0].Y > 0);

        // Clearing wipes both the points and the strokes.
        surface.BeginFrame();
        Draw(surface.Ui, clear: true);
        surface.EndFrame();
        Assert.Equal(0, pointsCount);
        Assert.Equal(0, strokesCount);
    }
}
