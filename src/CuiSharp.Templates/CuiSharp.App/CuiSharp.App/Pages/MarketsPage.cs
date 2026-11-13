using CuiSharp;
using CuiSharp.App.Services;
using CuiSharp.Hosting;

namespace CuiSharp.App.Pages;

/// <summary>
/// Live market dashboard: eight symbols at 50 Hz, async history load via <see cref="Mutation{T}"/>,
/// and reusable scratch buffers so every steady-state frame allocates 0 B.
/// </summary>
public sealed class MarketsPage(MarketFeed feed) : Component
{
    private const int WindowSamples = 1_200;
    private const int SparkSamples = 120;
    private const float ChartHeight = 240;

    private readonly Mutation<MarketSnapshot> _load = new(feed.LoadAsync, feed.Apply);

    private readonly float[] _window = new float[WindowSamples];
    private readonly float[] _spark = new float[SparkSamples];
    private readonly TableColumn[] _columns =
    [
        new("Symbol", 88),
        new("Last", 96, TextAlign.Right),
        new("Change", 96, TextAlign.Right),
        new("Trend", Size.Fill),
    ];

    private int _windowLength;
    private int _selected;

    public override string Title => "Markets";

    protected override bool WantsFrames => feed.Live;

    protected override void OnInitialized()
    {
        if (!feed.Live)
        {
            _load.Run();
        }
    }

    protected override void Draw(Ui ui)
    {
        feed.Advance();
        _windowLength = feed.History.CopyTail(_selected, _window);

        using (ui.Column(new() { Gap = 14 }))
        {
            Header(ui);
            Meters(ui);

            using (ui.Row(new() { Gap = 14, Width = Size.Fill, Align = Align.Start }))
            {
                using (ui.Box(new() { Width = Size.Percent(44) }))
                {
                    SymbolTable(ui);
                }

                using (ui.Box(new() { Width = Size.Fill }))
                {
                    Chart(ui);
                }
            }
        }
    }

    private void Header(Ui ui)
    {
        using (ui.Row(new() { Gap = 10, Align = Align.Center }))
        {
            ui.Heading("Markets", 2);
            ui.Secondary("Simulated feed, 50 ticks a second per symbol");
            ui.Spacer();

            if (_load.Error is { } error)
            {
                ui.Badge(error.Message, new() { Tone = Tone.Danger });

                if (ui.Button("Retry history", new() { Variant = ButtonVariant.Danger }))
                {
                    _load.Run();
                }
            }
            else if (_load.IsRunning)
            {
                ui.Button("Loading history", new() { Loading = true });
            }
            else if (feed.Live)
            {
                ui.Badge("live", new() { Tone = Tone.Success, Dot = true });
            }
            else if (ui.Button("Load history", new() { Variant = ButtonVariant.Primary }))
            {
                _load.Run();
            }
        }
    }

    private void Meters(Ui ui)
    {
        using (ui.Box(new() { Dir = Dir.Grid, Columns = 4, MinColumnWidth = 160, Gap = 12 }))
        {
            ui.Metric("Ticks accepted", $"{feed.Ticks / 1000.0:0.0}k", new() { Width = Size.Fill });
            ui.Metric("Feed rate", $"{feed.Rate:0} ticks/s", new() { Width = Size.Fill });
            ui.Metric("Frame interval", $"{feed.IntervalMs:0.0} ms", new() { Width = Size.Fill });
            ui.Metric("Ticks skipped", $"{feed.Skipped}", new() { Width = Size.Fill, LowerIsBetter = true });
        }
    }

    private void SymbolTable(Ui ui)
    {
        using (var table = ui.Table("symbols", _columns, new() { Selectable = true, Compact = true, Height = 430 }))
        {
            if (!table)
            {
                return;
            }

            for (var row = 0; row < PriceFeed.Symbols.Count; row++)
            {
                var symbol = PriceFeed.Symbols[row];
                if (ui.TableRow(_selected == row))
                {
                    _selected = row;
                }

                var change = feed.History.Change(row);
                var tone = change > 0.0005f ? Tone.Success : change < -0.0005f ? Tone.Danger : Tone.Neutral;

                ui.TableCellText(symbol.Ticker);
                ui.TableCellText($"{feed.History.Last(row):0.00}");

                ui.TableCell();
                ui.Badge($"{change * 100:+0.00;-0.00;0.00}%", new() { Tone = tone, Size = ControlSize.Sm });

                // Sparkline copies the span into the frame arena, so _spark is reused across rows.
                ui.TableCell();
                var samples = feed.History.CopyTail(row, _spark);
                ui.Sparkline(_spark.AsSpan(0, samples), Size.Fill, 34, Color.For(tone));
            }
        }
    }

    private void Chart(Ui ui)
    {
        var symbol = PriceFeed.Symbols[_selected];

        using (ui.Card(new() { Gap = 10 }))
        {
            using (ui.Row(new() { Gap = 10, Align = Align.Center }))
            {
                ui.Heading(symbol.Name, 4);
                ui.Badge(symbol.Ticker, new() { Tone = Tone.Accent, Size = ControlSize.Sm });
                ui.Spacer();
                ui.Secondary($"{feed.History.Count(_selected):N0} samples buffered, {_windowLength} drawn");
            }

            // Reserve ChartHeight while loading so the card does not jump when history arrives.
            if (_windowLength is 0)
            {
                Placeholder(ui);
                return;
            }

            // LineChart decimates spans longer than MaxPoints into min/max pairs in the frame arena.
            ui.LineChart("prices", _window.AsSpan(0, _windowLength),
                new() { Height = ChartHeight, Area = true, Width = Size.Fill, Format = "%.2f" }, Color.Accent);
        }
    }

    /// <summary>The loading, failed and never-loaded states, all holding the chart's height.</summary>
    private void Placeholder(Ui ui)
    {
        using (ui.Box(new()
        {
            Height = Size.Px(ChartHeight),
            Width = Size.Fill,
            Align = Align.Center,
            Justify = Justify.Center,
        }))
        {
            if (_load.Error is not null)
            {
                ui.Muted("The history could not be loaded. Try again above.");
            }
            else if (_load.IsRunning)
            {
                ui.Muted("Loading history…");
            }
            else
            {
                ui.Muted("Load the history to start the chart.");
            }
        }
    }
}
