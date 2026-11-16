using CuiSharp.Testing;

namespace CuiSharp.Tests;

public class CreativeControlTests
{
    [Fact]
    public void AVoiceNoteKeepsItsWaveformWhilePlaybackChanges()
    {
        float[] levels = [0.1f, 0.8f, CuiVoice.Silent, 0.4f];
        var note = new VoiceNoteState(levels, Duration: 4, Speed: 1);
        uint id = 0;
        using var app = TestApp.Start(ui =>
        {
            id = ui.MakeId("note");
            ui.VoiceNote("note", ref note);
        }, 500, 200);

        Assert.Same(levels, note.Levels);
        Click(app, app.Surface.Ui.RectOf(id ^ 0x9A1Bu));
        Assert.True(note.Playing);
        Assert.Same(levels, note.Levels);
        Click(app, app.Surface.Ui.RectOf(id ^ 0x5BEEu));
        Assert.Equal(1.5f, note.Speed);
        Assert.Same(levels, note.Levels);
        app.Frame(2);
        Assert.Same(levels, note.Levels);
        Assert.Equal([0.1f, 0.8f, CuiVoice.Silent, 0.4f], levels);
        float[] replacement = [0.9f, 0.2f];
        note.Levels = replacement;
        app.Frame();
        Assert.Same(replacement, note.Levels);
        note.Levels = null;
        app.Frame();
        Assert.Null(note.Levels);
        Assert.True(note.Playing);
        Assert.Empty(app.Faults);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShuffleRecolorsOnlyWhenTheCallerSuppliesASeed(bool seeded)
    {
        Color[] colors = [Color.Rgb(0x123456), Color.Rgb(0xABCDEF)];
        Color[] original = (Color[])colors.Clone();
        bool[] locked = [true, false];
        uint seed = 7, id = 0;
        WellEvents events = WellEvents.None;
        using var app = TestApp.Start(ui =>
        {
            id = ui.MakeId("palette");
            events |= seeded
                ? ui.PaletteStrip("palette", ref seed, colors, locked)
                : ui.PaletteStrip("palette", colors, locked);
        }, 500, 200);

        Click(app, app.Surface.Ui.RectOf(id ^ 0x7001u));
        Assert.NotEqual(WellEvents.None, events & WellEvents.Action);
        Assert.Equal(original[0], colors[0]);
        Assert.Equal([true, false], locked);
        if (seeded)
        {
            Assert.NotEqual(7u, seed);
            Assert.NotEqual(original[1], colors[1]);
            Assert.NotEqual(WellEvents.None, events & WellEvents.Changed);
        }
        else
        {
            Assert.Equal(original, colors);
            Assert.Equal(WellEvents.None, events & WellEvents.Changed);
        }
        Assert.Empty(app.Faults);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void PaletteBuffersCannotDisagreeAboutTheirLength(int colorCount, int lockCount)
    {
        using var surface = CuiSurface.Create(400, 200);
        surface.BeginFrame();
        Color[] colors = new Color[colorCount];
        bool[] locked = new bool[lockCount];
        uint seed = 7;
        Assert.Throws<ArgumentException>(() => surface.Ui.PaletteStrip("bad", colors, locked));
        Assert.Throws<ArgumentException>(() => surface.Ui.PaletteStrip("bad", ref seed, colors, locked));
        surface.Ui.Text("still balanced");
        surface.EndFrame();
        Assert.Empty(surface.Faults);
    }

    [Fact]
    public void StepLabelsCoverTheRangeNativeNormalizesToTwoStops()
    {
        using var surface = CuiSurface.Create(400, 200);
        surface.BeginFrame();
        int value = 1;
        Assert.Throws<ArgumentException>(() => surface.Ui.StepSlider("bad", ref value, 1, 1,
            new StepSliderOpts { Labels = ["one"] }));
        surface.Ui.Text("still balanced");
        surface.EndFrame();
        Assert.Empty(surface.Faults);
    }

    [Theory]
    [InlineData(int.MaxValue, int.MaxValue)]
    [InlineData(int.MinValue, int.MaxValue)]
    [InlineData(int.MinValue, -1)]
    public void AStepRangeMustFitTheNativeStopCountEvenWithoutLabels(int min, int max)
    {
        using var surface = CuiSurface.Create(400, 200);
        surface.BeginFrame();
        int value = min;
        Assert.Throws<ArgumentOutOfRangeException>(() => surface.Ui.StepSlider("bad", ref value, min, max));
        surface.Ui.Text("still balanced");
        surface.EndFrame();
        Assert.Empty(surface.Faults);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    public void AReversedStepRangeUsesTheSameTwoStopsAsCui(int min, int max)
    {
        int value = min;
        using var app = TestApp.Start(ui => ui.StepSlider("step", ref value, min, max,
            new StepSliderOpts { Labels = ["first", "second"] }), 400, 200);
        Assert.Equal(min, value);
        Assert.Empty(app.Faults);
    }

    [Fact]
    public void SelectionEscapeClearsTheCallerFlagsAndReportsTheClear()
    {
        bool[] selected = [true, false, true];
        int result = -1;
        using var app = TestApp.Start(ui =>
        {
            ui.Text("anchor");
            int action = ui.SelectionBar("selection", selected, [new SelectionAction(Icons.Copy, "Copy")]);
            if (action != -1) result = action;
        }, 500, 250);
        app.Key(Key.Escape);
        Assert.Equal(-2, result);
        Assert.Equal([false, false, false], selected);
        app.Frame(2);
        Assert.Equal([false, false, false], selected);
        Assert.Empty(app.Faults);
    }

    [Fact]
    public void CanvasToggleWritesBackTheFlagNativeFlipped()
    {
        var toggles = new[] { new CanvasToggle(Icons.Grid3x3, "Grid") };
        var view = new NodeView(0, 0, 1);
        CanvasEvents events = CanvasEvents.None;
        uint railId = 0;
        using var app = TestApp.Start(ui =>
        {
            using (ui.Box(new Box { Id = "canvas", Width = Size.Fill, Height = 400 })) { }
            railId = ui.MakeId("controls");
            events |= ui.CanvasControls("controls", ref view, toggles);
        }, 500, 450);
        app.Frame(2);
        var rail = app.Surface.Ui.RectOf(railId);
        // The sole custom toggle is the rail's bottom button, below zoom and fit.
        Click(app, new RectF(rail.X, rail.Y + rail.H - 40, rail.W, 32));
        Assert.True(toggles[0].On);
        Assert.NotEqual(CanvasEvents.None, events & CanvasEvents.Toggled);
        app.Frame(2);
        Assert.True(toggles[0].On);
        Assert.Empty(app.Faults);
    }

    [Fact]
    public void RelatedOptionArraysRejectShortInputsBeforeDrawing()
    {
        using var surface = CuiSurface.Create(400, 200);
        surface.BeginFrame();
        int picked = 0;
        float progress = 0;
        bool muted = false;
        var before = surface.Ui.LastId;
        Assert.Throws<ArgumentException>(() => surface.Ui.ToolbarRadio("radio", [Icons.Copy, Icons.Plus], ref picked,
            new ToolbarRadioOpts { Labels = ["one"] }));
        Assert.Throws<ArgumentException>(() => surface.Ui.VoiceWave("voice", [0.1f, 0.8f],
            new VoiceWaveOpts { Owners = [0] }));
        Assert.Throws<ArgumentException>(() => surface.Ui.VoiceWave("voice", [0.1f, 0.8f], ref progress,
            new VoiceWaveOpts { Owners = [0] }));
        Assert.Throws<ArgumentException>(() => surface.Ui.VoiceSession("session", VoiceState.Listening, [0.1f, 0.8f],
            new VoiceSessionOpts { Owners = [0] }));
        Assert.Throws<ArgumentException>(() => surface.Ui.VoiceSession("session", VoiceState.Listening, [0.1f, 0.8f], ref muted,
            new VoiceSessionOpts { Owners = [0] }));
        Assert.Throws<ArgumentException>(() => surface.Ui.Sankey("flow", ["A", "B"], [],
            new SankeyOpts { NodeColors = [Color.Accent] }));
        Assert.Equal(before, surface.Ui.LastId);
        surface.Ui.Text("still balanced");
        surface.EndFrame();
        Assert.Empty(surface.Faults);
    }

    [Fact]
    public void AGradientReportsTheColorEvenWhenTheValueDidNotChange()
    {
        float value = 1;
        Color color = default;
        WellEvents result = WellEvents.None;
        var options = new GradientSliderOpts
        {
            Stops = [new GradientStop(0, Color.Rgb(0x123456)), new GradientStop(1, Color.Rgb(0xABCDEF))],
        };
        using var app = TestApp.Start(ui => result = ui.GradientSlider("gradient", ref value, 0, 1, ref color, options), 400, 200);
        Assert.Equal(WellEvents.None, result);
        Assert.Equal(Color.Rgb(0xABCDEF), color);
        Assert.Equal(1f, value);
        Assert.Empty(app.Faults);
    }

    [Fact]
    public void ResamplingKeepsPeaksAndSilenceIncludingEmptyAndUpsampledClips()
    {
        float[] output = new float[3];
        Assert.Equal(3, CuiVoice.Resample([0.2f, 0.8f, CuiVoice.Silent, CuiVoice.Silent, 0.1f, 0.7f], output));
        Assert.Equal([0.8f, CuiVoice.Silent, 0.7f], output);
        Assert.Equal(3, CuiVoice.Resample([], output));
        Assert.Equal([CuiVoice.Silent, CuiVoice.Silent, CuiVoice.Silent], output);
        Assert.Equal(3, CuiVoice.Resample([0.4f], output));
        Assert.Equal([0.4f, 0.4f, 0.4f], output);
        Assert.Equal(0, CuiVoice.Resample([0.5f], []));
    }

    [Fact]
    public void ResamplingRejectsOverlappingBuffersBeforeChangingEither()
    {
        float[] levels = [0.1f, 0.9f, 0.3f, 0.7f];
        Assert.Throws<ArgumentException>(() => CuiVoice.Resample(levels, levels.AsSpan(1, 2)));
        Assert.Equal([0.1f, 0.9f, 0.3f, 0.7f], levels);
    }

    private static void Click(TestApp app, RectF rect)
    {
        Assert.True(rect.W > 0 && rect.H > 0, "The native control must have a laid-out rectangle.");
        app.MoveTo(rect.X + rect.W / 2, rect.Y + rect.H / 2).Press().Release();
    }
}
