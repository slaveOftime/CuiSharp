using CuiSharp;
using CuiSharp.App.Services;
using CuiSharp.Hosting;

namespace CuiSharp.App.Pages;

/// <summary>
/// A live market page: eight symbols, a simulated 50 Hz feed, and an async history
/// load. The whole screen is rebuilt from <see cref="MarketFeed"/> every frame.
/// <para>
/// Two lines do the async work, and neither of them polls: the mutation's apply
/// callback loads the history on the UI thread before the next frame, and
/// <c>WantsFrames</c> keeps the loop awake while the feed is live. Everything the
/// page hands to CUI is a field allocated once, so a frame allocates nothing.
/// </para>
/// </summary>
public sealed class MarketsPage(MarketFeed feed) : Component
{
    /// <summary>Samples the chart shows; the chart decimates anything beyond its width.</summary>
    private const int WindowSamples = 1_200;

    /// <summary>Sparkline samples per row.</summary>
    private const int SparkSamples = 120;

    /// <summary>Height of the chart, held even before it has anything to draw.</summary>
    private const float ChartHeight = 240;

    private readonly Mutation<MarketSnapshot> _load = new(feed.LoadAsync, feed.Apply);

    // Everything handed to CUI during a frame is owned here and reused.
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

    // The feed runs while the page is visible, so the host must keep frames coming.
    protected override bool WantsFrames => feed.Live;

    protected override void OnInitialized() => _load.Run();

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
                using (ui.Box(new() { Width = Size.Percent(44) })) SymbolTable(ui);
                using (ui.Box(new() { Width = Size.Fill })) Chart(ui);
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

            // One badge or button per state, so the page always says what it is doing.
            if (_load.Error is { } error)
            {
                ui.Badge(error.Message, new() { Tone = Tone.Danger });
                if (ui.Button("Retry history", new() { Variant = ButtonVariant.Danger })) _load.Run();
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
            // Values are interpolated straight into the frame arena: no string is
            // created for them, so these four metrics cost nothing per frame.
            ui.Metric("Ticks accepted", $"{feed.Ticks / 1000.0:0.0}k", new() { Width = Size.Fill });
            ui.Metric("Feed rate", $"{feed.Rate:0} ticks/s", new() { Width = Size.Fill });
            ui.Metric("Frame interval", $"{feed.IntervalMs:0.0} ms", new() { Width = Size.Fill });
            ui.Metric("Ticks skipped", $"{feed.Skipped}", new() { Width = Size.Fill, LowerIsBetter = true });
        }
    }

    private void SymbolTable(Ui ui)
    {
        // No sort value: the unsorted overload exists so a table that does not sort
        // never has to declare one.
        using (ui.Table("symbols", _columns, new() { Selectable = true, Compact = true, Height = 430 }))
        {
            for (var row = 0; row < PriceFeed.Symbols.Count; row++)
            {
                Symbol symbol = PriceFeed.Symbols[row];
                if (ui.TableRow(_selected == row)) _selected = row;

                float change = feed.History.Change(row);
                var tone = change > 0.0005f ? Tone.Success : change < -0.0005f ? Tone.Danger : Tone.Neutral;

                ui.TableCellText(symbol.Ticker);
                ui.TableCellText($"{feed.History.Last(row):0.00}");

                ui.TableCell();
                ui.Badge($"{change * 100:+0.00;-0.00;0.00}%", new() { Tone = tone, Size = ControlSize.Sm });

                // The newest samples are copied into one reused scratch buffer; the
                // sparkline copies them again into the frame, so the buffer is free
                // to be reused by the next row.
                ui.TableCell();
                int samples = feed.History.CopyTail(row, _spark);
                ui.Sparkline(_spark.AsSpan(0, samples), Size.Fill, 34, Color.For(tone));
            }
        }
    }

    private void Chart(Ui ui)
    {
        Symbol symbol = PriceFeed.Symbols[_selected];

        using (ui.Card(new() { Gap = 10 }))
        {
            using (ui.Row(new() { Gap = 10, Align = Align.Center }))
            {
                ui.Heading(symbol.Name, 4);
                ui.Badge(symbol.Ticker, new() { Tone = Tone.Accent, Size = ControlSize.Sm });
                ui.Spacer();
                ui.Secondary($"{feed.History.Count(_selected):N0} samples buffered, {_windowLength} drawn");
            }

            // A chart with no samples yet still holds its height: the card would otherwise grow
            // by the chart's whole height the moment the history arrived, and the page would jump.
            if (_windowLength is 0)
            {
                using (ui.Box(new()
                {
                    Height = Size.Px(ChartHeight),
                    Width = Size.Fill,
                    Align = Align.Center,
                    Justify = Justify.Center,
                }))
                {
                    if (_load.Error is not null) ui.Muted("The history could not be loaded. Try again above.");
                    else if (_load.IsRunning) ui.Muted("Loading history…");
                    else ui.Muted("Load the history to start the chart.");
                }

                return;
            }

            // Over a long window the chart decimates by itself: anything beyond
            // MaxPoints (default: twice the viewport width) collapses to min/max pairs
            // in the frame arena, so spikes survive the reduction.
            ui.LineChart("prices", _window.AsSpan(0, _windowLength),
                new() { Height = ChartHeight, Area = true, Width = Size.Fill, Format = "%.2f" }, Color.Accent);
        }
    }
}
