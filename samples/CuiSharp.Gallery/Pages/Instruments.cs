// Page 29 of 29: the instruments CUI 1.4 added: a dot-matrix chart with annotations, an
// event track lined up under it, readouts and a dot-matrix display.
// Part of the CuiSharp.Gallery sample: Instruments
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    // One value every quarter hour, 06:00 to 06:00, as the C example has it. Built once:
    // a series array rebuilt per frame is the page's own allocation.
    private static readonly float[] Caffeine = BuildCaffeine();
    private static readonly DotSeries[] CaffeineSeries = [new(Caffeine, "Caffeine", accent: true)];

    // The track edits this in place, so it is a field: a new array per frame would throw
    // away every drag. Five cups, in the order they were poured.
    private readonly double[] _cups = [6.5, 8, 11, 12.25, 15.5, 0, 0, 0];
    private int _cupCount = 5;
    private int _cupSelected = -1;

    private static readonly ChartAnnotation[] Thresholds =
    [
        new(AnnotationKind.Y, 0, 100, Label: "LIMIT", Detail: "the line at 23:00", Accent: true),
    ];

    private static float[] BuildCaffeine()
    {
        // Still in you at bedtime: three cups, each taken over half an hour and cleared at
        // 100 mg an hour afterwards. One value every quarter hour from 06:00 to 06:00.
        var cups = new (double At, float Mg)[] { (6.5, 90), (11, 90), (15.5, 120) };
        var values = new float[96];
        for (int i = 0; i < values.Length; i++)
        {
            double hour = 6 + i * 0.25;
            double mg = 0;
            foreach (var (at, dose) in cups)
            {
                double since = hour - at;
                if (since <= 0) continue;
                double left = since < 0.5 ? dose * (since / 0.5) : dose - 100 * (since - 0.5);
                mg += Math.Max(0, left);
            }
            values[i] = (float)mg;
        }
        return values;
    }

    private void PageInstruments(Ui ui)
    {
        Section(ui, "Dot-matrix chart", "Values as dots, lit up to each column's value; " +
            "the pointer or the arrow keys read the value under them.");
        using (ui.Card(new Card()))
        {
            var result = ui.DotChart("caffeine", CaffeineSeries, Caffeine.Length, new DotChartOpts
            {
                Height = 168,
                XMin = 6,
                XMax = 30,
                XUnits = AxisUnits.Hours,
                Unit = "mg",
                Annotations = Thresholds,
            });
            if (result.Hovered >= 0)
                ui.Secondary($"Sample {result.Hovered} at {result.X:0.00}: {result.Value:0} mg");
        }

        Section(ui, "Event track", "Click an empty spot to add a cup, double-click a dot to " +
            "remove it, drag to move one; the keys do the same from the keyboard.");
        using (ui.Card(new Card()))
        {
            var result = ui.EventTrack("cups", _cups, ref _cupCount, 6, 30, ref _cupSelected,
                new EventTrackOpts { Step = 0.25, Units = AxisUnits.Hours });
            ui.Secondary(result.Changed
                ? $"added {result.Added}, removed {result.Removed}, selected {result.Selected}"
                : $"{_cupCount} cups, {(_cupSelected >= 0 ? _cups[_cupSelected].ToString("0.00") : "none selected")}");
        }

        Section(ui, "Readout", "One figure the way an instrument shows it: a label, the value " +
            "in the monospaced font with its unit, and a muted line of context.");
        using (ui.Row(new Box { Gap = 24, Align = Align.End }))
        {
            ui.Readout("STILL IN YOU", "54", new ReadoutOpts { Unit = "MG" });
            ui.Readout("AT BEDTIME", "23:00", new ReadoutOpts { Detail = "six hours from now", Tone = Tone.Neutral });
            ui.Readout("OVER THE LINE", "0", new ReadoutOpts { Unit = "MG", Detail = "under the 50 MG limit", Accent = true });
        }

        Section(ui, "Dot-matrix display", "Text in a 5 by 7 dot font, for clocks, counters and " +
            "status lines; it shrinks to fit a narrow box, or scrolls.");
        using (ui.Row(new Box { Gap = 24, Align = Align.End }))
        {
            ui.DotDisplay("23:00", new DotDisplayOpts { Pitch = 6 });
            ui.DotDisplay("54 MG", new DotDisplayOpts { Accent = true });
            using (ui.Box(new Box { Width = Size.Px(120) }))
                ui.DotDisplay("SCROLLING", new DotDisplayOpts { Marquee = true });
        }
    }
}
