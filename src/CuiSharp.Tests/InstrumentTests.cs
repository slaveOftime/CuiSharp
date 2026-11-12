namespace CuiSharp.Tests;

/// <summary>
/// The instruments CUI 1.4 added. Each draws a real headless frame, so a wrapper that passes the
/// wrong pointer, count or option field fails here rather than in an app, and what the component
/// reports back is checked against what was passed in.
/// </summary>
public class InstrumentTests
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
    public void TheInstrumentsDrawAndReportNothingWhenNobodyTouchedThem()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(Width, Height);
        // Out of order on purpose: the track keeps its events sorted, so this is also the
        // input to the ordering test below.
        var times = new[] { 12.25, 8.0, 11.0, 0, 0, 0, 0, 0 };
        var count = 3;
        var selected = -1;

        var pixels = Frame(surface, ui =>
        {
            var chart = ui.DotChart("mg", [new DotSeries([10f, 40f, 90f, 130f], "Caffeine", accent: true)], 4,
                new DotChartOpts { XMin = 6, XMax = 30, Unit = "mg", XUnits = AxisUnits.Hours });
            Assert.Equal(-1, chart.Hovered);
            Assert.False(chart.Clicked);

            var track = ui.EventTrack("cups", times, ref count, 6, 30, ref selected, new EventTrackOpts { Step = 0.25 });
            Assert.False(track.Changed);
            Assert.Equal(-1, selected);

            ui.Readout("STILL IN YOU AT 23:00", "54", new ReadoutOpts { Unit = "MG", Detail = "under the line by 00:10" });
            ui.DotDisplay("23:00", new DotDisplayOpts { Pitch = 5 });
        });

        Assert.Contains(pixels, b => b != 0);
    }

    [Fact]
    public void TheEventTrackSortsAndClampsTheEventsItWasGiven()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(Width, Height);
        // 40 is off the end of the 6..30 axis and 12.25 comes before 8. Native does both
        // before it draws anything, and reports neither as a change, so the wrapper has to
        // copy the array back anyway or the caller keeps a copy native no longer agrees with.
        var times = new[] { 12.25, 8.0, 40.0, 0, 0 };
        var count = 3;
        var selected = -1;

        Frame(surface, ui => ui.EventTrack("cups", times, ref count, 6, 30, ref selected));

        Assert.Equal(3, count);
        Assert.Equal([8.0, 12.25, 30.0], times[..count]);
    }

    [Fact]
    public void TheDotChartSpreadsTheSamplesOverTheSampleCountItWasGiven()
    {
        if (!OperatingSystem.IsWindows()) return;

        // The number of samples is the call's: half as many over the same axis spreads them
        // twice as far apart, so the two frames cannot match. A wrapper that derived the
        // count from the series instead would draw both identically. A series shorter than
        // the count is the shape an unfinished feed has, and it draws without a fault: the
        // wrapper pads it, which the arena being zeroed makes invisible to a pixel test.
        byte[] four = Render(ui => ui.DotChart("spread", [new DotSeries([5f, 90f])], 4));
        byte[] eight = Render(ui => ui.DotChart("spread", [new DotSeries([5f, 90f])], 8));

        Assert.NotEqual(four, eight);
    }

    /// <summary>
    /// One frame on a surface that has already drawn a chart, so the layout, the theme and the
    /// arena have all been through a frame before this one.
    /// </summary>
    private static byte[] Render(DrawFn draw)
    {
        using var surface = CuiSurface.Create(Width, Height);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        surface.Draw(ui => ui.LineChart("loud", Enumerable.Repeat(99f, 16).ToArray(), new ChartOpts { Height = 200 }), pixels);
        surface.Draw(draw, pixels);
        Assert.Empty(surface.Faults);
        return pixels;
    }

    [Fact]
    public void TheChartAnnotationsCountThemselves()
    {
        // The array and its count were two fields a caller had to keep in step; CUI 1.4's
        // annotations made that worth fixing, so the count is the array's length.
        var opts = new DotChartOpts
        {
            Annotations =
            [
                new ChartAnnotation(AnnotationKind.X, 12, 0, Label: "NOW"),
                new ChartAnnotation(AnnotationKind.Y, 0, 100, Label: "LIMIT", Accent: true),
            ],
        };

        Assert.Equal(2, opts.ToNative().AnnotationCount);
        Assert.Null(typeof(DotChartOpts).GetField("AnnotationCount"));
    }
}
