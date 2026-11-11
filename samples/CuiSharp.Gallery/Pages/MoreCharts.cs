// Page 24 of 28: heatmap, calendar heatmap, treemap, radar, funnel, split bar,
// waffle, sankey, bar list, activity rings, scatter, radial gauges and candles.
// Part of the CuiSharp.Gallery sample: More charts
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    private static readonly string[] WeekdayLabels = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    // Nulls are label gaps, not missing rows; the option array is a plain string[].
    private static readonly string[] HourLabels = BuildHourLabels();

    private static string[] BuildHourLabels()
    {
        var labels = new string[24];
        for (int h = 0; h < 24; h += 3) labels[h] = $"{h}h";
        return labels;
    }

    private static readonly VizItem[] BundleModules =
    [
        new("Layout", 55), new("Text", 42), new("Draw", 35), new("Controls", 31), new("Charts", 23),
        new("Overlays", 19), new("Platform", 16), new("Theme", 9), new("Icons", 7), new("Grid", 6),
    ];

    private static readonly string[] RadarAxes = ["Speed", "Size", "Polish", "Docs", "Platforms", "Tests"];

    private static readonly float[] Radar10 = [82, 64, 90, 58, 75, 88];
    private static readonly float[] Radar11 = [70, 85, 72, 80, 60, 79];

    private static readonly VizItem[] FunnelStages =
    [
        new("Visited", 12840), new("Signed up", 4210), new("Activated", 2380), new("Subscribed", 860),
    ];

    private static readonly VizItem[] TimeParts =
    [
        new("Layout", 38), new("Text", 24), new("Paint", 21), new("Input", 9), new("Other", 8),
    ];

    private static readonly VizItem[] PlatformParts =
    [
        new("Windows", 46), new("macOS", 31), new("Linux", 23),
    ];

    private static readonly string[] SankeyNodes =
    ["Search", "Social", "Referral", "Sign up", "Bounced", "Trial", "Paid", "Churned"];

    private static readonly SankeyLink[] SankeyLinks =
    [
        new(0, 3, 420), new(1, 3, 260), new(2, 3, 180), new(0, 4, 300), new(1, 4, 220),
        new(3, 5, 860), new(5, 6, 580), new(5, 7, 280),
    ];

    private static readonly VizItem[] RingGoals =
    [
        new("Move", 0.82f), new("Exercise", 1.24f), new("Stand", 0.58f),
    ];

    private static readonly GaugeBand[] LoadBands =
    [
        new(60, Tone.Success), new(85, Tone.Warning), new(100, Tone.Danger),
    ];

    private readonly VizItem[] _barPages =
    [
        new("/docs", 4820), new("/", 3920), new("/gallery", 2410),
        new("/blog/fast-ui-in-c", 1830), new("/download", 1210), new("/pricing", 640),
    ];

    private readonly float[] _traffic = new float[7 * 24];
    private readonly float[] _activityDays = new float[364];
    private readonly Vec2[] _scatterSoftware = new Vec2[40];
    private readonly Vec2[] _scatterOpenGL = new Vec2[40];
    private readonly float[] _sizesSoftware = new float[40];
    private readonly float[] _sizesOpenGL = new float[40];
    private readonly Candle[] _candles = new Candle[2000];
    private bool _vizSeeded;

    private void PageViz(Ui ui)
    {
        if (!_vizSeeded) SeedViz(ui);

        using (ui.Row(new Box { Gap = 24, Align = Align.Start, Width = Size.Fill }))
        {
            using (ui.Column(new Box { Width = Size.Grow(1), Gap = 10 }))
            {
                Section(ui, "Heatmap", "Requests by weekday and hour.");
                using (ui.Card(new Card { Pad = 18, Width = Size.Fill }))
                {
                    ui.Heatmap("traffic", _traffic, 7, 24, new HeatmapOpts
                    {
                        RowLabels = WeekdayLabels,
                        ColLabels = HourLabels,
                        Format = "%.0f req",
                    });
                }
            }
        }

        Section(ui, "Calendar heatmap", "A year of activity, one square a day.");
        using (ui.Card(new Card { Pad = 18 }))
        {
            Date start = ui.DateToday(out Date today0) ? today0.AddDays(-363) : new Date(2025, 11, 17);
            ui.CalendarHeatmap("activity", start, _activityDays, new CalendarHeatmapOpts { Unit = "contributions" });
        }

        using (ui.Row(new Box { Gap = 24, Align = Align.Start, Width = Size.Fill }))
        {
            using (ui.Column(new Box { Width = Size.Grow(1.3f), Gap = 10 }))
            {
                Section(ui, "Treemap", "Bundle size by module.");
                ui.Treemap("bundle", BundleModules, new TreemapOpts { Height = 280, Format = "%.0f KB" });
            }
            using (ui.Column(new Box { Width = Size.Grow(1), Gap = 10, Align = Align.Center }))
            {
                Section(ui, "Radar", "Two releases across six qualities.");
                Series[] radar =
                [
                    new("1.0", Radar10),
                    new("1.1", Radar11),
                ];
                ui.RadarChart("radar", radar, RadarAxes, new RadarOpts { Size = 220, Max = 100 });
            }
        }
        using (ui.Row(new Box { Gap = 24, Align = Align.Start, Width = Size.Fill }))
        {
            using (ui.Column(new Box { Width = Size.Grow(1), Gap = 10 }))
            {
                Section(ui, "Funnel", "Sign up conversion.");
                ui.Funnel("funnel", FunnelStages);
            }
            using (ui.Column(new Box { Width = Size.Grow(1), Gap = 10 }))
            {
                Section(ui, "Split bar", "Where the time goes.");
                ui.SplitBar("split", TimeParts, new SplitBarOpts { Format = "%.0f ms" });
                ui.Gap(18);
                Section(ui, "Waffle", "Platforms of the last 100 installs.");
                ui.Waffle("waffle", PlatformParts, new WaffleOpts { Cell = 14 });
            }
        }

        Section(ui, "Sankey", "Where sign ups come from and where they end up.");
        using (ui.Card(new Card { Pad = 18, Width = Size.Fill }))
        {
            ui.Sankey("flow", SankeyNodes, SankeyLinks, new SankeyOpts { Height = 280 });
        }

        using (ui.Row(new Box { Gap = 24, Align = Align.Start, Width = Size.Fill }))
        {
            using (ui.Column(new Box { Width = Size.Grow(1.4f), Gap = 10 }))
            {
                Section(ui, "Bar list", "Ranked, keyed by label: press Shuffle and the rows slide to their new rank.");
                ui.BarList("pages", _barPages);
                if (ui.Button("Shuffle", new ButtonOpts
                {
                    Size = ControlSize.Sm,
                    Icon = Icons.Shuffle,
                    Variant = ButtonVariant.Outline,
                }))
                {
                    for (int k = 0; k < _barPages.Length; k++)
                    {
                        _shuffleSeed = (_shuffleSeed * 1103515245 + 12345) & 0x7fffffff;
                        _barPages[k] = new VizItem(_barPages[k].Label, 500 + (_shuffleSeed % 5000));
                    }
                }
            }
            using (ui.Column(new Box { Width = Size.Grow(1), Gap = 10, Align = Align.Center }))
            {
                Section(ui, "Activity rings", "Past one, a ring keeps winding.");
                ui.ActivityRings("rings", RingGoals, new RingsOpts { Size = 170, Center = "82%", Caption = "of goal" });
            }
        }

        Section(ui, "Scatter", "Frame time against node count, sized by draw calls.");
        using (ui.Card(new Card { Pad = 18, Width = Size.Fill }))
        {
            ScatterSeries[] scatter =
            [
                new("Software", _scatterSoftware, _sizesSoftware),
                new("OpenGL", _scatterOpenGL, _sizesOpenGL),
            ];
            ui.ScatterChart("scatter", scatter, new ScatterOpts
            {
                Height = 300,
                XLabel = "Nodes",
                YLabel = "Frame time (ms)",
                YFormat = "%.2f",
            });
        }
        using (ui.Card(new Card
        {
            Pad = 18,
            Width = Size.Fill,
            Dir = Dir.Row,
            Wrap = true,
            Gap = 24,
            Align = Align.Center,
        }))
        {
            ui.RadialGauge("cpu", 64, 0, 100, new RadialGaugeOpts
            {
                Unit = "%",
                Label = "CPU",
                Bands = LoadBands,
                Size = 180,
            });
            ui.RadialGauge("temp", 71, 20, 100, new RadialGaugeOpts
            {
                Unit = "C",
                Label = "GPU temperature",
                ShowTarget = true,
                Target = 85,
                Size = 180,
            });
            ui.RadialGauge("mem", 0.62f * 32, 0, 32, new RadialGaugeOpts
            {
                Format = "%.1f",
                Unit = "GB",
                Label = "Memory",
                NoNeedle = true,
                Ticks = -1,
                Size = 150,
            });
        }
        using (ui.Card(new Card { Pad = 18, Width = Size.Fill, Gap = 10 }))
        {
            ui.Label("Price, 2,000 periods");
            ui.Candlestick("price", _candles, new CandlestickOpts { Height = 280 });
        }
    }

    private int _shuffleSeed = 1;

    private void SeedViz(Ui ui)
    {
        for (int d = 0; d < 7; d++)
        {
            for (int h = 0; h < 24; h++)
            {
                float work = d < 5 ? 1.0f : 0.45f;
                float day = MathF.Exp(-MathF.Pow((h - 13.5f) / 4.2f, 2)) * 90 * work
                    + MathF.Exp(-MathF.Pow((h - 20.5f) / 2.0f, 2)) * 35;
                _traffic[(d * 24) + h] = day + ((d * 31) + (h * 17)) % 11;
            }
        }

        int seed = 7;
        int todayNumber = ui.DateToday(out Date today) ? today.DayNumber : 739645;
        for (int k = 0; k < 364; k++)
        {
            seed = ((seed * 1103515245) + 12345) & int.MaxValue;
            int weekday = (k + ((((todayNumber - 363) % 7) + 10) % 7)) % 7;
            float wave = MathF.Sin(k / 40.0f);
            float baseValue = (weekday >= 5 ? 0.6f : 3.2f) * (0.6f + (0.4f * wave * wave));
            float r = ((seed >> 16) % 1000) / 1000.0f;
            _activityDays[k] = r < 0.18f ? 0 : MathF.Floor(baseValue * r * 4);
        }

        for (int k = 0; k < 40; k++)
        {
            float n = 200 + (k * 95.0f);
            _scatterSoftware[k] = new Vec2(n, 0.08f + (n * 0.00006f) + ((k * 37) % 13 * 0.004f));
            _scatterOpenGL[k] = new Vec2((n * 0.9f) + 150, 0.15f + (n * 0.00011f) + ((k * 53) % 17 * 0.006f));
            _sizesSoftware[k] = 10 + ((k * 7) % 40);
            _sizesOpenGL[k] = 20 + ((k * 11) % 50);
        }

        float price = 64;
        for (int k = 0; k < 2000; k++)
        {
            float o = price;
            float c = o * (1 + (MathF.Sin(k * 0.37f) * 0.012f) + (MathF.Cos(k * 0.043f) * 0.008f));
            _candles[k] = new Candle(
                o,
                MathF.Max(o, c) * 1.006f,
                MathF.Min(o, c) * 0.994f,
                c,
                2000 + ((k * 7919) % 900) * 10);
            price = c;
        }

        _vizSeeded = true;
    }
}
