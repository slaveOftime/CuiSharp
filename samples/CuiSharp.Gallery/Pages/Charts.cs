// Page 9 of 28: line, bar, donut charts and gauges.
// Part of the CuiSharp.Gallery sample: Charts
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    private void PageCharts(Ui ui)
    {
        Section(ui, "Custom widget", "A control written by hand: primitives on a canvas, no CUI component involved.");

        using (ui.Card(new Card()))
        using (ui.Row(new Box { Gap = 24, Align = Align.Center }))
        {
            ui.Spark("spark", Months.Revenue, Color.Accent, 220, 60);
            ui.Bars("bars", [0.62f, 0.34f, 0.81f, 0.45f], 160, 84);
        }

        Section(ui, "Line chart", "Hover to inspect values. The crosshair glides between points.");
        using (ui.Card(new Card()))
        {
            ui.LineChart("revenue", _lineSeries, new ChartOpts
            {
                Labels = Months.Names,
                Area = true,
                Smooth = true,
                Height = 240,
                Format = "$%.0fk",
            });
        }

        using (ui.Box(new Box { Dir = Dir.Grid, Columns = 2, MinColumnWidth = 260, Gap = 16 }))
        {
            using (ui.Column(new Box { Gap = 12 }))
            {
                Section(ui, "Bar chart", null);
                using (ui.Card(new Card()))
                {
                    ui.BarChart("visits", _barSeries, new ChartOpts
                    {
                        Labels = Months.Names,
                        Height = 200,
                    });
                }
            }

            using (ui.Column(new Box { Gap = 12 }))
            {
                Section(ui, "Donut chart", null);
                using (ui.Card(new Card { Align = Align.Center }))
                {
                    ui.DonutChart("platforms", Months.Shares, Months.ShareLabels, new ChartOpts
                    {
                        Height = 180,
                        Format = "%.0f%%",
                    });
                }
            }
        }

        Section(ui, "Stacked bars and gauges", null);
        using (ui.Box(new Box { Dir = Dir.Grid, Columns = 2, MinColumnWidth = 260, Gap = 16 }))
        {
            using (ui.Card(new Card()))
            {
                ui.BarChart("runs", _stackedSeries, new ChartOpts
                {
                    Labels = Months.Weeks,
                    Stacked = true,
                    Height = 200,
                });
            }

            using (ui.Card(new Card { Dir = Dir.Row, Gap = 24, Align = Align.Center }))
            {
                ui.Gauge("CPU", 0.42f, 130);
                ui.Gauge("Memory", 0.81f, 130);
            }
        }
    }
}
