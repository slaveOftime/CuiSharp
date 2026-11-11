// Page 10 of 28: the audio/production controls, the radial gauge and a candlestick chart.
// Part of the CuiSharp.Gallery sample: Studio
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    private static readonly Candle[] Prices =
    [
        new(101, 106, 99, 105, 1200), new(105, 109, 103, 104, 980), new(104, 108, 100, 107, 1430),
        new(107, 112, 106, 111, 1610), new(111, 113, 105, 106, 1870), new(106, 110, 102, 109, 1240),
        new(109, 117, 108, 116, 2050), new(116, 119, 111, 112, 1760), new(112, 115, 107, 114, 1390),
        new(114, 121, 113, 120, 2210), new(120, 124, 116, 118, 1900), new(118, 122, 114, 121, 1480),
    ];

    private static readonly string[] PriceLabels =
    [
        "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
    ];

    private static readonly GaugeBand[] CpuBands =
    [
        new(60, Tone.Success), new(85, Tone.Warning), new(100, Tone.Danger),
    ];

    private static readonly float[] MeterPeaks = new float[2];

    private float _gain = 6f, _pan, _mix = 42f, _cpu = 63f;
    private Date _month = new(2026, 11, 1);
    private Date _day = new(2026, 11, 14);

    private void PageStudio(Ui ui)
    {
        Section(ui, "Radial gauge", "Bands colour the arc by range; the needle shows the value and the marker the target.");
        using (ui.Box(new Box { Dir = Dir.Grid, Columns = 2, MinColumnWidth = 220, Gap = 16 }))
        {
            using (ui.Card(new Card()))
            {
                ui.RadialGauge("cpu", _cpu, 0, 100, new RadialGaugeOpts
                {
                    Label = "CPU",
                    Unit = "%",
                    Bands = CpuBands,
                    Target = 70,
                    ShowTarget = true,
                    Size = 180,
                });
            }

            using (ui.Card(new Card()))
            {
                ui.RadialGauge("disk", 74, 0, 100, new RadialGaugeOpts
                {
                    Label = "Disk",
                    Unit = "%",
                    NoNeedle = true,
                    Sweep = 180,
                    Ticks = 4,
                    Size = 180,
                });
            }
        }

        Section(ui, "Knobs, faders and meters", "Drag a knob or a fader; double-click resets it. The meter follows the fader.");
        using (ui.Card(new Card()))
        using (ui.Row(new Box { Gap = 28, Align = Align.End, Wrap = true }))
        {
            ui.Knob("gain", ref _gain, -12, 12, new KnobOpts
            {
                Label = "Gain",
                Unit = "dB",
                Bipolar = true,
                Format = "%+.1f",
            });
            ui.Knob("pan", ref _pan, -1, 1, new KnobOpts
            {
                Label = "Pan",
                Bipolar = true,
                Format = "%+.2f",
            });
            ui.Knob("mix", ref _mix, 0, 100, new KnobOpts
            {
                Label = "Mix",
                Unit = "%",
                Step = 1,
                Reset = 50,
                Size = 64,
            });

            // A stereo peak meter: the fader's own meter, then a standalone one.
            ui.Fader("level", ref _cpu, 0, 100, new FaderOpts
            {
                Label = "Level",
                Length = 180,
                Format = "%.0f",
                Meter = MeterPeaks,
            });
            ui.LevelMeter("peaks", MeterPeaks, new LevelMeterOpts
            {
                Length = 180,
                Scale = true,
                Segmented = true,
            });
        }

        Section(ui, "Candlestick", "Open, high, low and close with volume under it. The wheel zooms, dragging pans.");
        using (ui.Card(new Card()))
        {
            int hovered = ui.Candlestick("prices", Prices, new CandlestickOpts
            {
                Labels = PriceLabels,
                Height = 300,
                Format = "$%.0f",
            });
            if (hovered >= 0) ui.Muted($"Candle {PriceLabels[hovered]}");
        }

        Section(ui, "Month calendar", "Events paint onto the days; the arrows and Today move the month.");
        using (ui.Card(new Card()))
        {
            var result = ui.MonthCalendar("month", ref _month, Events, ref _day, new MonthOpts
            {
                FirstWeekday = 0,
                CellHeight = 92,
            });
            if (result.DayActivated) ui.Muted($"New event on {_day.Year}-{_day.Month}-{_day.Day}");
            else if (result.Event >= 0) ui.Muted($"Event {Events[result.Event].Time:0.#}h selected");
        }
    }

    private static readonly MonthEvent[] Events =
    [
        new("Standup", new Date(2026, 11, 3), Time: 9.5f, Tone: Tone.Info),
        new("Offsite", new Date(2026, 11, 9), new Date(2026, 11, 13), AllDay: true, Tone: Tone.Success),
        new("Review", new Date(2026, 11, 18), Time: 14f, Tone: Tone.Warning),
        new("Holiday", new Date(2026, 11, 24), new Date(2026, 11, 27), AllDay: true, Tone: Tone.Neutral),
    ];
}
