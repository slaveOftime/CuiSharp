namespace CuiSharp.Tests;

/// <summary>
/// The components CUI 1.2 added: a real headless frame for each one, so a wrapper that
/// passes the wrong pointer, count or option field fails here rather than in an app.
/// </summary>
public class NewComponentTests
{
    private const int Width = 900;
    private const int Height = 700;

    private static byte[] Frame(CuiSurface surface, DrawFn draw)
    {
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        surface.Draw(draw, pixels);
        Assert.Empty(surface.Faults);
        return pixels;
    }

    [Fact]
    public void AudioControlsDrawAndReportEdits()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(Width, Height);
        var gain = 3f;
        var pan = -0.5f;
        var level = 55f;
        float[] peaks = [0.4f, 0.7f];

        var pixels = Frame(surface, ui =>
        {
            using (ui.Column(new Box { Width = Size.Fill, Pad = 16, Gap = 12 }))
            {
                ui.Knob("gain", ref gain, -12, 12, new KnobOpts { Label = "Gain", Bipolar = true });
                ui.Knob("pan", ref pan, -1, 1, new KnobOpts { Label = "Pan", Bipolar = true });
                ui.Fader("level", ref level, 0, 100, new FaderOpts { Label = "Level", Meter = peaks });
                ui.LevelMeter("peaks", peaks, new LevelMeterOpts { Scale = true });
                ui.RadialGauge("cpu", 63, 0, 100, new RadialGaugeOpts
                {
                    Label = "CPU",
                    Unit = "%",
                    Bands = [new GaugeBand(60, Tone.Success), new GaugeBand(100, Tone.Danger)],
                    ShowTarget = true,
                    Target = 70,
                });
            }
        });

        Assert.Contains(pixels, b => b != 0);
        // Nothing was dragged, so the values the caller passed come back unchanged.
        Assert.Equal(3f, gain);
        Assert.Equal(-0.5f, pan);
        Assert.Equal(55f, level);
    }

    [Fact]
    public void AShortLabelArrayIsRejectedBeforeTheCall()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(Width, Height);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];

        // Native reads `count` labels, so the wrapper refuses a shorter array rather than
        // letting the chart walk off the end of it.
        var error = Assert.Throws<ArgumentException>(() => surface.Draw(ui =>
        {
            ui.Candlestick("bad",
            [
                new Candle(1, 2, 0.5f, 1.5f, 10),
                new Candle(1, 2, 0.5f, 1.5f, 10),
            ], new CandlestickOpts { Labels = ["only one"] });
        }, pixels));

        Assert.Contains("Labels", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CandlestickMonthCalendarAndTourDraw()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(Width, Height);
        var month = new Date(2026, 11, 1);
        var day = new Date(2026, 11, 14);
        var step = 0;
        uint target = 0;

        var pixels = Frame(surface, ui =>
        {
            using (ui.Column(new Box { Width = Size.Fill, Pad = 16, Gap = 12 }))
            {
                ui.Candlestick("prices",
                [
                    new Candle(101, 106, 99, 105, 1200),
                    new Candle(105, 109, 103, 104, 980),
                    new Candle(104, 108, 100, 107, 1430),
                ], new CandlestickOpts { Height = 160, Labels = ["Jan", "Feb", "Mar"] });

                var monthResult = ui.MonthCalendar("month", ref month, [
                    new MonthEvent("Standup", new Date(2026, 11, 3), Time: 9.5f),
                    new MonthEvent("Offsite", new Date(2026, 11, 9), new Date(2026, 11, 13), AllDay: true),
                ], ref day, new MonthOpts { CellHeight = 60 });

                Assert.False(monthResult.DayActivated);
                Assert.Equal(-1, monthResult.Event);

                if (ui.Button("Start")) { }
                target = ui.LastId;
                // The tour writes the step through the ref parameter; its result is the action
                // the user took, not the step, so assigning it would end the tour at once.
                _ = ui.Tour("tour", [new TourStep(target, "Title", "Body")], ref step, new TourOpts { Width = 260 });
            }
        });

        Assert.Contains(pixels, b => b != 0);
        Assert.Equal(2026, month.Year);
        Assert.Equal(11, month.Month);
        Assert.NotEqual(0u, target);
        Assert.True(step >= -1);
    }

    [Fact]
    public void TerminalKeepsItsScrollbackAndCostsNothingPerFrame()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var terminal = new Terminal(maxLines: 40, maxChars: 4096);
        Assert.Equal(1, terminal.LineCount);

        terminal.WriteLine("first");
        terminal.WriteLine("\x1b[32mgreen\x1b[0m line");
        Assert.Equal(3, terminal.LineCount);

        using var surface = CuiSurface.Create(Width, Height);
        DrawFn draw = ui =>
        {
            using (ui.Column(new Box { Width = Size.Fill, Pad = 16 }))
            {
                var result = ui.Terminal("log", terminal, new TerminalOpts { Height = 200 });
                Assert.Null(result.Command);
                Assert.False(result.Interrupt);
            }
        };

        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        surface.Draw(draw, pixels);
        Assert.Empty(surface.Faults);
        Assert.Contains(pixels, b => b != 0);

        // The scrollback lives in the terminal, so drawing it again allocates nothing. The
        // first frames are not steady state: they fill caches (glyphs, line metrics) once.
        for (var i = 0; i < 5; i++) surface.Draw(draw, pixels);

        // Batches with a median, like the frame-lifecycle check: a batch that straddles a
        // one-off says nothing about the frame, a real per-frame allocation moves every batch.
        var perFrame = new long[5];
        for (var batch = 0; batch < perFrame.Length; batch++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10; i++) surface.Draw(draw, pixels);
            perFrame[batch] = (GC.GetAllocatedBytesForCurrentThread() - before) / 10;
        }

        Assert.True(perFrame.Median() == 0,
            $"a steady frame allocated {perFrame.Median()} B (recorder on: {WidgetRecorder.Enabled})");

        terminal.Clear();
        Assert.Equal(1, terminal.LineCount);
    }

    [Fact]
    public void ImageViewerCropperAndEmojiPickerDraw()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(Width, Height);
        var view = new ImageView { Zoom = 0, Cx = 0, Cy = 0 };
        var crop = new RectF(0, 0, 0.5f, 0.5f);
        var recent = "🙂 🚀";
        var tone = 0;

        var pixels = Frame(surface, ui =>
        {
            using (ui.Column(new Box { Width = Size.Fill, Pad = 16, Gap = 12 }))
            {
                ui.ImageViewer("viewer", 0, 0, 0, ref view, new ImageViewerOpts { Height = 160 });
                ui.Cropper("crop", 0, 0, 0, ref crop, new CropperOpts { Height = 160, Aspect = 1 });
                ui.Emoji("🙂", 24);
                var picked = ui.EmojiPicker("emoji", ref recent, ref tone, new EmojiPickerOpts { Height = 180 });
                Assert.True(picked is null or { Length: > 0 });
            }
        });

        Assert.Contains(pixels, b => b != 0);
        Assert.Equal("🙂 🚀", recent);
        Assert.Equal(0, tone);
    }

    [Fact]
    public void FileBrowserAndPickerWriteThePathBack()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(Width, Height);
        var path = Path.GetTempPath();
        var picked = "";
        var open = false;

        var pixels = Frame(surface, ui =>
        {
            using (ui.Column(new Box { Width = Size.Fill, Pad = 16, Gap = 12 }))
            {
                var browsed = ui.FileBrowser("files", ref path, o: new FileBrowserOpts { Height = 200 });
                Assert.False(browsed.Error);
                Assert.Null(browsed.Picked);

                ui.FilePicker("picker", ref open, ref picked, o: new FilePickerOpts { Title = "Pick" });
                Assert.False(open);
            }
        });

        Assert.Contains(pixels, b => b != 0);
        // The rented buffers come back as the same paths the caller passed.
        Assert.Equal(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        Assert.Equal("", picked);
        Assert.False(open);
    }
}
