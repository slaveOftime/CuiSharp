namespace CuiSharp.Tests;

/// <summary>
/// The hand-written surface around the generated wrappers: drawing primitives, measurement,
/// the disabled gate, the log and hex facades, the diff and curve helpers, and windowless
/// input. Each one is exercised in a real frame.
/// </summary>
public class HandWrittenApiTests
{
    private const int Width = 720;
    private const int Height = 520;

    [Fact]
    public void DrawingPrimitivesMeasureAndDisabledDraw()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(Width, Height);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        TextMetrics metrics = default;
        bool wasDisabled = false;

        surface.Draw(ui =>
        {
            ui.Canvas("canvas", Size.Fill, Size.Px(200), (c, box) =>
            {
                c.DrawRect(box, Color.SurfaceMuted, 8);
                c.DrawRectGradient(box, Color.Accent, Color.Surface, 8);
                c.DrawShadow(box, Color.Rgba(0x00000040), blur: 8);
                c.DrawLine(box.X, box.Y, box.X + box.W, box.Y + box.H, 2, Color.Accent);
                (float X, float Y)[] line = [(box.X, box.Y), (box.X + 40, box.Y + 20), (box.X + 80, box.Y)];
                (float X, float Y)[] triangle = [(box.X + 10, box.Y + 10), (box.X + 60, box.Y + 10), (box.X + 35, box.Y + 50)];
                c.DrawPolyline(line, 2, Color.Success);
                c.DrawPolygon(triangle, Color.Warning);
                c.DrawCircle(box.X + 100, box.Y + 40, 18, Color.Info);
                c.DrawArc(box.X + 140, box.Y + 40, 18, 0, 3.14f, 3, Color.Danger);
                c.DrawText("drawn", box.X + 160, box.Y + 10, Color.Foreground, 14);
                c.DrawIcon(Icons.Check, new RectF(box.X + 160, box.Y + 30, 16, 16), Color.Success);
                using (c.Clip(new RectF(box.X, box.Y, box.W / 2, box.H)))
                {
                    c.DrawRect(new RectF(box.X, box.Y, box.W, 8), Color.Accent);
                }
            });

            metrics = ui.MeasureText("Hello", 16);
            using (ui.Disabled(true))
            {
                wasDisabled = ui.IsDisabled;
                ui.Button("Disabled");
            }

            Assert.True(ui.Time > 0);
            Assert.True(ui.DeltaTime >= 0);
            Assert.True(ui.FrameCount >= 0);
            Assert.True(ui.DpiScale > 0);
            Assert.Equal(0, ui.MousePosition.X);
            Assert.False(ui.WantsMouse);
        }, pixels);

        Assert.Empty(surface.Faults);
        Assert.Contains(pixels, b => b != 0);
        Assert.True(metrics.Width > 0);
        Assert.True(metrics.Height > 0);
        Assert.True(metrics.LineHeight > 0);
        Assert.True(wasDisabled);
    }

    [Fact]
    public void ColorMathsAndKeyNamesResolve()
    {
        var oklch = Color.Oklch(0.6f, 0.12f, 250);
        Assert.NotEqual(0, oklch.A);

        var mid = Color.Mix(new Color(0, 0, 0), new Color(255, 255, 255), 0.5f);
        Assert.InRange(mid.R, 95, 160);

        var faded = new Color(10, 20, 30, 255).WithAlpha(0.5f);
        Assert.InRange(faded.A, 100, 155);

        Assert.Equal("A", Ui.KeyName(Key.A));
        Assert.False(string.IsNullOrEmpty(Ui.AccentName(Accent.Violet)));
    }

    [Fact]
    public void CurveGradientAndDiffHelpersEvaluate()
    {
        var curve = CuiMath.EvaluateCurve(
        [
            new CurveKey(0, 0),
            new CurveKey(1, 1),
        ], 0.5f);
        Assert.InRange(curve, 0.3f, 0.7f);

        var gradient = CuiMath.EvaluateGradient(
        [
            new GradientStop(0, new Color(0, 0, 0, 255)),
            new GradientStop(1, new Color(255, 255, 255, 255)),
        ], 0.5f);
        Assert.InRange(gradient.R, 90, 180);

        var (hunks, stats) = CuiMath.DiffLines("a\nb\nc\n", "a\nB\nc\n");
        Assert.Equal(3, stats.OldLines);
        Assert.Equal(3, stats.NewLines);
        Assert.True(stats.Hunks >= 1, $"hunks={stats.Hunks}");
        Assert.NotEmpty(hunks);

        var unchanged = CuiMath.DiffLines("same\n", "same\n");
        Assert.Empty(unchanged.Hunks);

        Assert.True(CuiMath.PathMatches("photo.png", "*.png;*.jpg"));
        Assert.False(CuiMath.PathMatches("notes.txt", "*.png;*.jpg"));

        var words = CuiMath.DiffWords("the quick fox", "the slow fox");
        Assert.NotNull(words);
        Assert.NotEmpty(words!.Value.Old);
    }

    [Fact]
    public void LogBufferKeepsLinesAndDrawsItsView()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var log = new LogBuffer(maxLines: 50, maxChars: 4096);
        log.Now = 12.5;
        log.AppendLine("first");
        log.AppendLine("careful", LogLevel.Warn);
        Assert.Equal(2, log.LineCount);
        Assert.Equal(2UL, log.TotalLines);
        Assert.Contains("first", log.Text(), StringComparison.Ordinal);
        Assert.Contains("careful", log.Text(), StringComparison.Ordinal);

        using var surface = CuiSurface.Create(Width, Height);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        surface.Draw(ui =>
        {
            using (ui.Column(new Box { Width = Size.Fill, Pad = 12 }))
            {
                ui.LogView("log", log, new LogViewOpts { Height = 200, Timestamps = true });
            }
        }, pixels);

        Assert.Empty(surface.Faults);
        Assert.Contains(pixels, b => b != 0);

        log.Clear();
        Assert.Equal(0, log.LineCount);
    }

    [Fact]
    public void EditableHexViewWritesIntoTheCallersSpan()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(Width, Height);
        var bytes = new byte[64];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)i;
        var selection = new HexSelection(0, 0, 0);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];

        HexResult result = default;
        surface.Draw(ui =>
        {
            ui.HexView("hex", bytes, ref selection, new HexViewOpts { Height = 200 });
            result = ui.HexView("hex", bytes, ref selection, new HexViewOpts { Height = 200 });
        }, pixels);

        Assert.Empty(surface.Faults);
        Assert.Contains(pixels, b => b != 0);
        // Nothing was typed, so the bytes and the selection come back as they went in.
        Assert.Equal((byte)0, bytes[0]);
        Assert.Equal((byte)63, bytes[63]);
        Assert.False(result.Edited);
        Assert.Equal(0UL, selection.Length);
    }

    [Fact]
    public void SurfaceInputDrivesAClickAndTyping()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(400, 300);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        int clicks = 0;
        var text = "";

        // The button fills the top strip, so the click lands on it whatever the metrics.
        void Draw(Ui ui)
        {
            using (ui.Column(new Box { Width = Size.Fill, Gap = 8 }))
            {
                if (ui.Button("Press me", new ButtonOpts { Width = Size.Fill })) clicks++;
                ui.Input("name", ref text, new InputOpts { AutoFocus = true });
            }
        }

        surface.Draw(Draw, pixels);
        Assert.Equal(0, clicks);

        // Typing goes to the focused input (AutoFocus), and the frame after delivery
        // consumes it: the same rule the platform backend follows.
        surface.Type("hi");
        surface.Draw(Draw, pixels);
        Assert.Equal("hi", text);

        // A real click is press, frame, release, frame: the widget reports it on the
        // release, once the pointer is inside.
        surface.MouseMove(200, 20);
        surface.Draw(Draw, pixels);
        surface.MouseDown();
        surface.Draw(Draw, pixels);
        surface.MouseUp();
        surface.Draw(Draw, pixels);
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void DirectoryListingReturnsEntriesAndAMissingFolderIsEmpty()
    {
        if (!OperatingSystem.IsWindows()) return;

        // A directory this test owns, so the result cannot race with other programs.
        string folder = Path.Combine(Path.GetTempPath(), "cuisharp-fs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "inner"));
        File.WriteAllText(Path.Combine(folder, "b.txt"), "hello");
        File.WriteAllText(Path.Combine(folder, ".hidden"), "x");
        try
        {
            var entries = CuiFiles.ListDirectory(folder);
            Assert.Equal(2, entries.Length);
            Assert.Equal("inner", entries[0].Name);
            Assert.True(entries[0].Dir);
            Assert.Equal("b.txt", entries[1].Name);
            Assert.False(entries[1].Dir);
            Assert.Equal(5UL, entries[1].Size);
            Assert.True(entries[1].Modified > 0);

            // Hidden entries only when asked for.
            Assert.Equal(3, CuiFiles.ListDirectory(folder, includeHidden: true).Length);

            // A path that is not there is an empty list, not a crash.
            Assert.Empty(CuiFiles.ListDirectory(Path.Combine(folder, "missing")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
