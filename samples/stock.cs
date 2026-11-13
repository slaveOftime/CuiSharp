#:package CuiSharp@0.4.1
#:package System.Net.Http.WinHttpHandler@11.0.0

// The properties below are what make `dotnet publish stock.cs -c Release -o .`
// leave one small native executable beside this file, with no .NET on the target.
#:property PublishAot=true
#:property OptimizationPreference=Size
#:property InvariantGlobalization=true
#:property StripSymbols=true
#:property DebugType=none
#:property OutputType=WinExe
#:property ApplicationIcon=stock.ico
#:property EventSourceSupport=false
#:property HttpActivityPropagationSupport=false
#:property MetadataUpdaterSupport=false
#:property UseSystemResourceKeys=true
#:property StackTraceSupport=false
#:property MetricsSupport=false

#pragma warning disable CA1416 // WinHttpHandler is the Windows HTTP stack; this app publishes for Windows.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using CuiSharp;

// ---------------------------------------------------------------------------
// Global Market Cap �?a native CuiSharp dashboard over Tencent + Eastmoney data.
//
//   run      dotnet run stock.cs                       JIT build, window in a second
//   ship     dotnet publish stock.cs -c Release -o .    one stock.exe, no runtime needed
//            (add -p:CuiSharpHeadless=true to publish a build `dotnet cui` can drive)
//
//   iterate  CUISHARP_HEADLESS=1 ./stock.exe            no window; serves the tool's protocol
//            dotnet cui tree                            the controls the last frame drew
//            dotnet cui click Refresh                  click one, print the frame it produced
//            dotnet cui png shot.png                    that frame as a PNG
//            dotnet cui docs read live-debugging        the guide, offline
//            dotnet cui api ChartOpts                   one exact declaration
//
// Below: Dashboard and its sections, then the model, the feeds, the tracker and
// the cache.
// ---------------------------------------------------------------------------

var tracker = new MarketTracker();
tracker.Start();

// Headless screenshots are useful only once the first snapshot exists.
if (Environment.GetEnvironmentVariable("CUISHARP_HEADLESS") is "1" or "true")
    tracker.WaitForFirst(TimeSpan.FromSeconds(25));

var dashboard = new Dashboard(tracker);
return CuiApp.Run(Dashboard.Window, dashboard.Draw);

// ---------------------------------------------------------------------------
// The window. Three bands on a wide screen; one scrolling column on a narrow one.
// ---------------------------------------------------------------------------

internal sealed class Dashboard(MarketTracker tracker)
{
    public static readonly CuiAppOptions Window = new()
    {
        Title = "Global Market Cap",
        Width = 1360,
        Height = 920,
        Theme = Theme.Dark,
        Accent = Accent.Blue,
        Renderer = Renderer.Software,
    };

    private const float WideBreakpoint = 1000f;

    private readonly DashboardState _state = new();
    private readonly ChartPanel _chart = new();

    public void Draw(Ui ui)
    {
        SquareCorners(ui);

        var snapshot = tracker.Current;
        var (width, height) = ui.ViewportSize();

        if (width >= WideBreakpoint) DrawWide(ui, snapshot, width, height);
        else DrawNarrow(ui, snapshot, height);

        // The tracker publishes on a background thread; poll once a second so the
        // window picks the new snapshot up without any cross-thread UI calls.
        ui.RequestWakeup(1f);
    }

    private static void SquareCorners(Ui ui)
    {
        var theme = ui.Style;
        theme.RadiusPanel = 0;
        theme.RadiusControl = 0;
        theme.RadiusSurface = 0;
    }

    private void DrawWide(Ui ui, Snapshot? snapshot, float width, float height)
    {
        var listWidth = Math.Clamp(width * 0.30f, 300, 430);
        var chartHeight = Math.Max(280, height - 340);

        using (ui.Column(new Box { Pad = 12, Gap = 12, Height = Size.Fill }))
        {
            HeaderView.Draw(ui, tracker, _state, wide: true);
            SummaryView.Draw(ui, snapshot, _state, wide: true);

            using (ui.Row(new Box { Gap = 12, Width = Size.Fill, Height = Size.Fill, Align = Align.Stretch }))
            {
                _chart.Draw(ui, snapshot, _state, wide: true, chartHeight);
                RankingPanel.Draw(ui, snapshot, _state, Size.Fill, Size.Px(listWidth));
            }

            FooterView.Draw(ui, snapshot, wide: true);
        }
    }

    private void DrawNarrow(Ui ui, Snapshot? snapshot, float height)
    {
        var chartHeight = Math.Clamp(height * 0.40f, 240, 420);

        using (ui.Scroll(new ScrollOpts { Height = Size.Fill, Pad = 12, Gap = 12 }))
        using (ui.Column(new Box { Gap = 12 }))
        {
            HeaderView.Draw(ui, tracker, _state, wide: false);
            SummaryView.Draw(ui, snapshot, _state, wide: false);
            _chart.Draw(ui, snapshot, _state, wide: false, chartHeight);
            RankingPanel.Draw(ui, snapshot, _state, Size.Px(440), Size.Fill);
            FooterView.Draw(ui, snapshot, wide: false);
        }
    }
}

// ---------------------------------------------------------------------------
// View state: what the controls change and the sections read.
// ---------------------------------------------------------------------------

internal sealed class DashboardState
{
    // Mode index 0: market cap over six months. Index 1 is today's session.
    public const int CapMode = 0;

    public static readonly string[] Modes = ["6 months", "Today"];

    private readonly HashSet<string> _selected = [with(StringComparer.Ordinal)];

    // Fields, not properties: the Number and Segmented controls bind by ref.
    public int Mode;
    public double TopN = 12;

    public int TopCount(int available) => available <= 0 ? 0 : Math.Clamp((int)TopN, 1, available);

    public bool IsSelected(Company company) => _selected.Contains(company.Key);

    public bool AnySelected(ReadOnlySpan<QuoteRow> rows, int count)
    {
        for (var i = 0; i < count; i++)
            if (_selected.Contains(rows[i].Company.Key)) return true;
        return false;
    }

    public void Toggle(Company company)
    {
        if (!_selected.Add(company.Key)) _selected.Remove(company.Key);
    }

    public void Clear() => _selected.Clear();
}

// ---------------------------------------------------------------------------
// Header: eyebrow, title, and the controls that drive the rest of the page.
// ---------------------------------------------------------------------------

internal static class HeaderView
{
    private static readonly string CompaniesLabel = $"{Data.Count} companies · {Data.ExchangeCount} exchanges";

    public static void Draw(Ui ui, MarketTracker tracker, DashboardState state, bool wide)
    {
        if (wide)
        {
            using (ui.Row(new Box { Align = Align.Center, Gap = 12, Wrap = true }))
            {
                DrawTitle(ui);
                ui.Spacer();
                DrawControls(ui, tracker, state);
            }
        }
        else
        {
            using (ui.Column(new Box { Gap = 10 }))
            {
                DrawTitle(ui);

                using (ui.Row(new Box { Align = Align.Center, Gap = 8, Wrap = true }))
                    DrawControls(ui, tracker, state);
            }
        }
    }

    private static void DrawTitle(Ui ui)
    {
        using (ui.Column(new Box { Gap = 2 }))
        {
            using (ui.Row(new Box { Gap = 10, Align = Align.Center }))
            {
                ui.Text("GLOBAL MARKETS", new TextOpts
                {
                    Font = Font.Medium,
                    Size = 10,
                    Color = Color.Accent,
                });

                ui.Badge(CompaniesLabel, new BadgeOpts
                {
                    Tone = Tone.Neutral,
                    Size = ControlSize.Sm,
                });
            }

            ui.Heading("Market cap leaders", 1);
        }
    }

    private static void DrawControls(Ui ui, MarketTracker tracker, DashboardState state)
    {
        var busy = tracker.IsBusy;

        ui.Badge(busy ? "Updating" : "Live · 30s", new BadgeOpts
        {
            Tone = Tone.Danger,
            Dot = !busy,
            Size = ControlSize.Sm,
        });

        if (ui.Button("Refresh", new ButtonOpts
        {
            Icon = Icons.RefreshCw,
            Size = ControlSize.Sm,
            Variant = ButtonVariant.Secondary,
            Loading = busy,
        }))
        {
            tracker.RequestRefresh();
        }

        ui.Text("Top", new TextOpts { Size = 12, Color = Color.TextSecondary });
        ui.Number("topn", ref state.TopN, 5, 30, new NumberOpts
        {
            Width = Size.Px(100),
            Step = 1,
            Decimals = 0,
        });

        using (ui.Box(new Box { Width = Size.Px(196) }))
        {
            ui.Segmented("mode", DashboardState.Modes, ref state.Mode, new SegmentedOpts { FullWidth = true });
        }
    }
}

// ---------------------------------------------------------------------------
// Summary: four numbers over the top N rows.
// ---------------------------------------------------------------------------

internal static class SummaryView
{
    public static void Draw(Ui ui, Snapshot? snapshot, DashboardState state, bool wide)
    {
        if (snapshot is null || snapshot.Rows.Length == 0) return;

        var stats = SummaryStats.Of(snapshot.Rows, state.TopCount(snapshot.Rows.Length));

        if (wide) DrawRow(ui, snapshot, stats);
        else DrawGrid(ui, snapshot, stats);
    }

    private static void DrawRow(Ui ui, Snapshot snapshot, SummaryStats stats)
    {
        using (ui.Card(new Card { PadY = 12, PadX = 0.1f, Dir = Dir.Row, Gap = 0 }))
        {
            ui.Stat("Combined cap", Format.Money(stats.Total),
                $"top {stats.Count} · weighted {Format.Percent(stats.Weighted)} today");
            ui.VLine();
            ui.Stat("Largest company", Format.Money(stats.Leader.CapUsd), LeaderDetail(stats));
            ui.VLine();
            ui.Stat("Today's breadth", $"{stats.Up} up · {stats.Down} down", BreadthDetail(stats));
            ui.VLine();
            ui.Stat("FX snapshot", FxValue(snapshot), FxDetail(snapshot));
        }
    }

    private static void DrawGrid(Ui ui, Snapshot snapshot, SummaryStats stats)
    {
        using (ui.Box(new Box { Dir = Dir.Grid, Columns = 2, MinColumnWidth = 190, Gap = 10 }))
        {
            ui.StatCard("Combined cap", Format.Money(stats.Total),
                $"top {stats.Count} · weighted {Format.Percent(stats.Weighted)} today");
            ui.StatCard("Largest company", Format.Money(stats.Leader.CapUsd), LeaderDetail(stats));
            ui.StatCard("Today's breadth", $"{stats.Up} up · {stats.Down} down", BreadthDetail(stats));
            ui.StatCard("FX snapshot", FxValue(snapshot), FxDetail(snapshot));
        }
    }

    private static string LeaderDetail(SummaryStats stats) =>
        $"{stats.Leader.Company.Name} · {stats.Leader.Company.Market}";

    private static string BreadthDetail(SummaryStats stats) =>
        $"{stats.Count - stats.Up - stats.Down} unchanged in the top {stats.Count}";

    private static string FxValue(Snapshot snapshot) => $"USD/CNY {snapshot.UsdCny:0.00}";

    private static string FxDetail(Snapshot snapshot) => $"HKD {snapshot.UsdHkd:0.000} · JPY {snapshot.UsdJpy:0.0}";
}

/// <summary>The four numbers, summed once per frame over the first <c>Count</c> rows.</summary>
internal readonly record struct SummaryStats(float Total, float Weighted, int Up, int Down, int Count, QuoteRow Leader)
{
    public static SummaryStats Of(ReadOnlySpan<QuoteRow> rows, int count)
    {
        var total = 0f;
        var weighted = 0f;
        var up = 0;
        var down = 0;

        for (var i = 0; i < count; i++)
        {
            total += rows[i].CapUsd;
            weighted += rows[i].CapUsd * rows[i].Pct;

            if (rows[i].Pct > 0.0001f) up++;
            else if (rows[i].Pct < -0.0001f) down++;
        }

        return new SummaryStats(
            Total: total,
            Weighted: total > 0 ? weighted / total : 0,
            Up: up,
            Down: down,
            Count: count,
            Leader: rows[0]);
    }
}

// ---------------------------------------------------------------------------
// Chart card. Keeps its series buffers, so a frame allocates nothing after the
// first one.
// ---------------------------------------------------------------------------

internal sealed class ChartPanel
{
    private readonly Series[] _series = new Series[Data.Count];
    private Series[] _visible = [];

    public void Draw(Ui ui, Snapshot? snapshot, DashboardState state, bool wide, float height)
    {
        using (ui.Card(new Card
        {
            Width = wide ? Size.Fill : Size.Fit,
            Height = wide ? Size.Fill : Size.Fit,
            Pad = 12,
            Gap = 12,
            Dir = Dir.Column,
        }))
        {
            DrawHeading(ui, state.Mode);

            if (snapshot is null)
            {
                DrawLoading(ui, height);
                return;
            }

            var count = state.TopCount(snapshot.Rows.Length);
            var anySelected = state.AnySelected(snapshot.Rows, count);
            var drawn = CollectSeries(snapshot, state, count, anySelected);

            if (drawn == 0) DrawEmpty(ui, snapshot.Rows.Length, height);
            else DrawLines(ui, snapshot, state.Mode, drawn, height);

            DrawCaption(ui, snapshot, state.Mode, anySelected, wide);
        }
    }

    private static void DrawHeading(Ui ui, int mode)
    {
        using (ui.Row(new Box { Align = Align.Center, Gap = 10, Wrap = true }))
        {
            using (ui.Column(new Box { Gap = 3 }))
            {
                ui.Text(mode == DashboardState.CapMode ? "Market capitalization" : "Session performance", new TextOpts
                {
                    Font = Font.Medium,
                    Size = 15,
                });

                ui.Muted(mode == DashboardState.CapMode
                    ? "USD trillions · the last six months"
                    : "Percent from previous close · each market's own session");
            }

            ui.Spacer();

            ui.Badge(mode == DashboardState.CapMode ? "USD · trillions" : "% change", new BadgeOpts
            {
                Tone = Tone.Neutral,
                Size = ControlSize.Sm,
            });
        }
    }

    private static void DrawLoading(Ui ui, float height)
    {
        using (ui.Row(new Box { Height = height, Align = Align.Center, Justify = Justify.Center, Gap = 10 }))
        {
            ui.Spinner(18);
            ui.Muted("Loading global markets...");
        }
    }

    private static void DrawEmpty(Ui ui, int rowCount, float height)
    {
        using (ui.Box(new Box { Height = height, Align = Align.Center, Justify = Justify.Center }))
        {
            ui.EmptyState(rowCount == 0 ? "Waiting for market data" : "No series available", new EmptyOpts
            {
                Description = rowCount == 0
                    ? "The quote feed has not answered yet. It retries every 30 seconds."
                    : "The history feed has not returned these companies yet.",
                Icon = Icons.ChartLine,
            });
        }
    }

    /// <summary>Fills the buffer for the visible rows and returns how many it wrote.</summary>
    private int CollectSeries(Snapshot snapshot, DashboardState state, int count, bool anySelected)
    {
        var drawn = 0;

        for (var i = 0; i < count; i++)
        {
            var row = snapshot.Rows[i];
            var values = state.Mode == DashboardState.CapMode
                ? snapshot.CapSeries.GetValueOrDefault(row.Company.Key)
                : snapshot.PctSeries.GetValueOrDefault(row.Company.Key);
            if (values is null) continue;

            var dimmed = anySelected && !state.IsSelected(row.Company);
            _series[drawn++] = new Series
            {
                Name = row.Company.Ticker,
                Values = values,
                Color = dimmed ? Palette.Dim(row.Company) : Palette.ColorOf(row.Company),
            };
        }

        return drawn;
    }

    private void DrawLines(Ui ui, Snapshot snapshot, int mode, int count, float height)
    {
        if (_visible.Length != count) _visible = new Series[count];
        Array.Copy(_series, _visible, count);

        ui.LineChart("chart", _visible, new ChartOpts
        {
            Height = height,
            Labels = mode == DashboardState.CapMode ? snapshot.DateLabels : null,
            Format = mode == DashboardState.CapMode ? "$%.2fT" : "%.2f%%",
            NoLegend = true,
            Smooth = false,
            Area = false,
        });
    }

    private static void DrawCaption(Ui ui, Snapshot snapshot, int mode, bool anySelected, bool wide)
    {
        var hint = anySelected
            ? "click a highlighted row to clear it"
            : "click a row to highlight its line";

        var left = mode == DashboardState.CapMode
            ? $"{snapshot.FirstDateLabel} - now · {hint}"
            : $"Open to close · {hint}";

        var right = snapshot.Errors == 0
            ? "Quotes: Tencent · History: Eastmoney, Tencent, Sina"
            : $"{snapshot.Errors} feed(s) unavailable";

        if (wide)
        {
            using (ui.Row(new Box { Align = Align.Center, Gap = 10 }))
            {
                ui.Muted(left + " · hover to inspect");
                ui.Spacer();
                ui.Muted(right);
            }
        }
        else
        {
            using (ui.Column(new Box { Gap = 2 }))
            {
                ui.Muted(left);
                ui.Muted(right);
            }
        }
    }
}

// ---------------------------------------------------------------------------
// Ranking: the table on the right, or under the chart when the window is narrow.
// ---------------------------------------------------------------------------

internal static class RankingPanel
{
    public static void Draw(Ui ui, Snapshot? snapshot, DashboardState state, Size height, Size width)
    {
        var rows = snapshot?.Rows ?? [];
        var count = state.TopCount(rows.Length);
        var anySelected = state.AnySelected(rows, count);

        using (ui.Card(new Card
        {
            Width = width,
            Height = height,
            Pad = 12,
            Dir = Dir.Column,
        }))
        {
            DrawHeading(ui, state, anySelected);
            ui.Separator();

            if (count == 0) DrawEmpty(ui);
            else DrawRows(ui, rows, state, count, anySelected);
        }
    }

    private static void DrawHeading(Ui ui, DashboardState state, bool anySelected)
    {
        using (ui.Column(new Box { Gap = 2 }))
        {
            using (ui.Row(new Box { Align = Align.Center, Gap = 8 }))
            {
                ui.Text("Ranking", new TextOpts { Font = Font.Medium, Size = 15 });
                ui.Spacer();

                if (anySelected && ui.Button("Clear", new ButtonOpts
                {
                    Size = ControlSize.Sm,
                    Variant = ButtonVariant.Ghost,
                }))
                {
                    state.Clear();
                }
            }

            ui.Muted(anySelected
                ? "Highlighted companies stay bright in the chart"
                : "Largest companies by USD market cap · click to highlight");
        }
    }

    private static void DrawEmpty(Ui ui)
    {
        using (ui.Box(new Box { Height = Size.Fill, Align = Align.Center, Justify = Justify.Center }))
            ui.EmptyState("No quotes yet", new EmptyOpts { Description = "The market feed is still loading." });
    }

    private static void DrawRows(Ui ui, QuoteRow[] rows, DashboardState state, int count, bool anySelected)
    {
        using (ui.Scroll(new ScrollOpts { Height = Size.Fill, Width = Size.Fill, Pad = 0, Gap = 0 }))
        using (ui.Column(new Box { Gap = 4, Width = Size.Fill }))
        {
            for (var i = 0; i < count; i++)
            {
                var row = rows[i];
                var selected = state.IsSelected(row.Company);

                if (ui.RankRow(i + 1, row, selected, anySelected && !selected))
                    state.Toggle(row.Company);
            }
        }
    }
}

// ---------------------------------------------------------------------------
// Footer: where the numbers come from, and how old they are.
// ---------------------------------------------------------------------------

internal static class FooterView
{
    private const string Sources =
        "Quotes and FX from Tencent Finance · daily history from Eastmoney, Tencent and Sina · " +
        "Japanese minutes from Eastmoney · quotes may be delayed";

    public static void Draw(Ui ui, Snapshot? snapshot, bool wide)
    {
        var updated = UpdatedLabel(snapshot);

        if (wide)
        {
            using (ui.Row(new Box { Align = Align.Center, Gap = 10 }))
            {
                ui.Muted(Sources);
                ui.Spacer();
                if (updated.Length > 0) ui.FooterTime(updated);
            }
        }
        else
        {
            using (ui.Column(new Box { Gap = 3 }))
            {
                ui.Muted(Sources);
                if (updated.Length > 0) ui.FooterTime(updated);
            }
        }
    }

    private static string UpdatedLabel(Snapshot? snapshot) => snapshot switch
    {
        null => "",
        { Cached: true } cached => $"Cached · {cached.UpdatedUtc.ToLocalTime():MMM d, HH:mm}",
        var live => $"Updated {live.UpdatedUtc.ToLocalTime():HH:mm:ss}",
    };
}

// ---------------------------------------------------------------------------
// The pieces the sections share. Extensions so a section reads as a list of
// things it draws, not as calls into a helper class.
// ---------------------------------------------------------------------------

internal static class UiExtensions
{
    public static void Stat(this Ui ui, string label, string value, string detail)
    {
        using (ui.Column(new Box { Width = Size.Fill, PadX = 14, Gap = 2 }))
            ui.StatText(label, value, detail);
    }

    public static void StatCard(this Ui ui, string label, string value, string detail)
    {
        using (ui.Card(new Card { MinWidth = 190, PadX = 14, PadY = 10 }))
        using (ui.Column(new Box { Gap = 2 }))
            ui.StatText(label, value, detail);
    }

    public static void FooterTime(this Ui ui, string text)
    {
        ui.Text(text, new TextOpts
        {
            Size = 11,
            Tabular = true,
            Color = Color.TextMuted,
        });
    }

    public static void VLine(this Ui ui)
    {
        using (ui.Box(new Box
        {
            Width = Size.Px(1),
            Height = Size.Px(42),
            Bg = Color.Border,
        })) { }
    }

    public static bool RankRow(this Ui ui, int rank, QuoteRow row, bool selected, bool dimmed)
    {
        var company = row.Company;

        using (ui.Card(new Card
        {
            Id = "row:" + company.Ticker,
            Hoverable = true,
            Selected = selected,
            Width = Size.Fill,
            PadX = 10,
            PadY = 5,
            Dir = Dir.Row,
            Gap = 9,
            Align = Align.Center,
        }, out var clicked))
        {
            ui.Text(rank.ToString(CultureInfo.InvariantCulture), new TextOpts
            {
                Size = 11,
                Tabular = true,
                Align = TextAlign.Right,
                Color = Color.TextMuted,
                Width = Size.Px(18),
            });

            using (ui.Box(new Box
            {
                Width = Size.Px(8),
                Height = Size.Px(8),
                Radius = 4,
                Bg = dimmed ? Palette.Dim(company) : Palette.ColorOf(company),
            })) { }

            using (ui.Column(new Box { Width = Size.Fill, MinWidth = 0, Gap = 1 }))
            {
                ui.Text(company.Ticker, new TextOpts
                {
                    Font = Font.Medium,
                    Size = 13,
                    NoWrap = true,
                    Color = dimmed ? Color.TextSecondary : Color.Foreground,
                });

                ui.Text($"{company.Name} · {company.Market}", new TextOpts
                {
                    Size = 11,
                    NoWrap = true,
                    Color = dimmed ? Color.TextMuted : Color.TextSecondary,
                });
            }

            using (ui.Column(new Box { Width = Size.Px(92), Gap = 1, Align = Align.End }))
            {
                ui.Text(Format.Money(row.CapUsd), new TextOpts
                {
                    Size = 13,
                    Font = Font.Medium,
                    Tabular = true,
                    Align = TextAlign.Right,
                    Color = dimmed ? Color.TextSecondary : Color.Foreground,
                });

                ui.Text(Format.Percent(row.Pct), new TextOpts
                {
                    Size = 11,
                    Tabular = true,
                    Align = TextAlign.Right,
                    Color = dimmed ? Color.TextMuted : Format.PercentColor(row.Pct),
                });
            }

            return clicked;
        }
    }

    private static void StatText(this Ui ui, string label, string value, string detail)
    {
        ui.Text(label, new TextOpts { Size = 11, Color = Color.TextMuted });

        ui.Text(value, new TextOpts
        {
            Font = Font.Medium,
            Size = 21,
            Tabular = true,
        });

        ui.Text(detail, new TextOpts { Size = 11, Color = Color.TextSecondary, NoWrap = true });
    }
}

// ---------------------------------------------------------------------------
// Numbers and colours.
// ---------------------------------------------------------------------------

internal static class Format
{
    public static string Money(float usd) => "$" + Compact(usd);

    public static string Compact(float usd) => usd >= 1e12f
        ? Invariant(usd / 1e12f, "0.00") + "T"
        : Invariant(usd / 1e9f, "0.##") + "B";

    public static string Percent(float pct) => (pct >= 0 ? "+" : "") + Invariant(pct, "0.00") + "%";

    // Chinese market convention: red is up, green is down.
    public static Color PercentColor(float pct) =>
        pct > 0.0001f ? Color.Danger
        : pct < -0.0001f ? Color.Success
        : Color.TextMuted;

    // The numbers read the same in every culture: a decimal comma would break the columns.
    private static string Invariant(float value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
}

internal static class Palette
{
    private static readonly Dictionary<string, Color> Base = Build();

    private static Dictionary<string, Color> Build()
    {
        var colors = new Dictionary<string, Color>(Data.Count, StringComparer.Ordinal);

        for (var i = 0; i < Data.Count; i++)
        {
            var hue = (22f + i * 137.50776f) % 360f;
            colors[Data.Companies[i].Key] = Color.Oklch(0.73f, 0.155f, hue);
        }

        return colors;
    }

    public static Color ColorOf(Company company) => Base[company.Key];

    /// <summary>The same hue, pulled back toward the surface, for unselected lines.</summary>
    public static Color Dim(Company company) => Color.Mix(Base[company.Key], Color.Background, 0.62f);
}

// ---------------------------------------------------------------------------
// Model.
// ---------------------------------------------------------------------------

/// <summary>One quote symbol: where its quote, daily closes and minutes come from.</summary>
internal sealed record Company(
    string Key,        // Tencent quote key, e.g. usNVDA
    string Ticker,     // display ticker
    string Name,
    string Market,
    string Currency,
    string EastSecId,  // Eastmoney secid
    MinuteKind Minute);

internal enum MinuteKind
{
    TencentUs,
    TencentHk,
    TencentCn,
    Eastmoney,
}

internal static class Data
{
    public static readonly Company[] Companies =
    [
        new("usNVDA", "NVDA", "NVIDIA", "NASDAQ", "USD", "105.NVDA", MinuteKind.TencentUs),
        new("usMSFT", "MSFT", "Microsoft", "NASDAQ", "USD", "105.MSFT", MinuteKind.TencentUs),
        new("usAAPL", "AAPL", "Apple", "NASDAQ", "USD", "105.AAPL", MinuteKind.TencentUs),
        new("usGOOGL", "GOOGL", "Alphabet", "NASDAQ", "USD", "105.GOOGL", MinuteKind.TencentUs),
        new("usAMZN", "AMZN", "Amazon", "NASDAQ", "USD", "105.AMZN", MinuteKind.TencentUs),
        new("usMETA", "META", "Meta Platforms", "NASDAQ", "USD", "105.META", MinuteKind.TencentUs),
        new("usAVGO", "AVGO", "Broadcom", "NASDAQ", "USD", "105.AVGO", MinuteKind.TencentUs),
        new("usTSLA", "TSLA", "Tesla", "NASDAQ", "USD", "105.TSLA", MinuteKind.TencentUs),
        new("usBRK.B", "BRK.B", "Berkshire Hathaway", "NYSE", "USD", "106.BRK_B", MinuteKind.TencentUs),
        new("usLLY", "LLY", "Eli Lilly", "NYSE", "USD", "106.LLY", MinuteKind.TencentUs),
        new("usJPM", "JPM", "JPMorgan Chase", "NYSE", "USD", "106.JPM", MinuteKind.TencentUs),
        new("usV", "V", "Visa", "NYSE", "USD", "106.V", MinuteKind.TencentUs),
        new("usMA", "MA", "Mastercard", "NYSE", "USD", "106.MA", MinuteKind.TencentUs),
        new("usWMT", "WMT", "Walmart", "NYSE", "USD", "106.WMT", MinuteKind.TencentUs),
        new("usJNJ", "JNJ", "Johnson & Johnson", "NYSE", "USD", "106.JNJ", MinuteKind.TencentUs),
        new("usPG", "PG", "Procter & Gamble", "NYSE", "USD", "106.PG", MinuteKind.TencentUs),
        new("usXOM", "XOM", "Exxon Mobil", "NYSE", "USD", "106.XOM", MinuteKind.TencentUs),
        new("usUNH", "UNH", "UnitedHealth", "NYSE", "USD", "106.UNH", MinuteKind.TencentUs),
        new("usHD", "HD", "Home Depot", "NYSE", "USD", "106.HD", MinuteKind.TencentUs),
        new("usCOST", "COST", "Costco", "NASDAQ", "USD", "105.COST", MinuteKind.TencentUs),
        new("usORCL", "ORCL", "Oracle", "NYSE", "USD", "106.ORCL", MinuteKind.TencentUs),
        new("usNFLX", "NFLX", "Netflix", "NASDAQ", "USD", "105.NFLX", MinuteKind.TencentUs),
        new("usAMD", "AMD", "AMD", "NASDAQ", "USD", "105.AMD", MinuteKind.TencentUs),
        new("usTSM", "TSM", "TSMC", "NYSE", "USD", "106.TSM", MinuteKind.TencentUs),

        new("hk00700", "0700", "Tencent", "HKEX", "HKD", "116.00700", MinuteKind.TencentHk),
        new("hk09988", "9988", "Alibaba", "HKEX", "HKD", "116.09988", MinuteKind.TencentHk),
        new("hk03690", "3690", "Meituan", "HKEX", "HKD", "116.03690", MinuteKind.TencentHk),

        new("sh600519", "600519", "Kweichow Moutai", "SSE", "CNY", "1.600519", MinuteKind.TencentCn),
        new("sh601398", "601398", "ICBC", "SSE", "CNY", "1.601398", MinuteKind.TencentCn),
        new("sz300750", "300750", "CATL", "SZSE", "CNY", "0.300750", MinuteKind.TencentCn),

        new("jp7203", "7203", "Toyota", "TSE", "JPY", "176.7203", MinuteKind.Eastmoney),
        new("jp6758", "6758", "Sony", "TSE", "JPY", "176.6758", MinuteKind.Eastmoney),
        new("jp9984", "9984", "SoftBank Group", "TSE", "JPY", "176.9984", MinuteKind.Eastmoney),
    ];

    public static int Count => Companies.Length;

    /// <summary>NASDAQ, NYSE, HKEX, SSE, SZSE, TSE �?counted, not hard-coded.</summary>
    public static readonly int ExchangeCount =
        Companies.Select(static company => company.Market).Distinct(StringComparer.Ordinal).Count();
}

internal sealed record QuoteRow(Company Company, float Price, float Prev, float Pct, float CapUsd);

internal sealed class Snapshot
{
    public DateTime UpdatedUtc { get; init; }
    public QuoteRow[] Rows { get; init; } = [];
    public Dictionary<string, float[]> CapSeries { get; init; } = [with(StringComparer.Ordinal)];
    public Dictionary<string, float[]> PctSeries { get; init; } = [with(StringComparer.Ordinal)];
    public string[] DateLabels { get; init; } = [];
    public float UsdHkd { get; init; }
    public float UsdJpy { get; init; }
    public float UsdCny { get; init; }
    public int Errors { get; set; }
    public bool Cached { get; init; }

    public string FirstDateLabel => DateLabels.Length > 0 ? DateLabels[0] : "";
}

// ---------------------------------------------------------------------------
// Transport. One client, eight requests at a time, two attempts each.
//
// WinHttpHandler borrows the HTTP/TLS stack Windows already ships instead of
// linking the managed one. That is most of the difference between a 1 MB
// CuiSharp binary and this one: the managed stack adds about 3.5 MB, the
// Windows one about 1.5 MB. The app therefore publishes for Windows.
// ---------------------------------------------------------------------------

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The client lives for the process lifetime; the OS reclaims it at exit.")]
internal sealed class HttpText
{
    private readonly HttpClient _http = Create();
    private readonly SemaphoreSlim _gate = new(8);

    public async Task<string?> GetAsync(string url, bool gbk = false)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await _gate.WaitAsync().ConfigureAwait(false);

            try
            {
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    await Task.Delay(250).ConfigureAwait(false);
                    continue;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                return gbk ? Gbk.DecodeLossy(bytes) : Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                if (attempt == 0) await Task.Delay(250).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        return null;
    }

    private static HttpClient Create()
    {
        var handler = new WinHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };

        var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0 Safari/537.36");

        return http;
    }
}

/// <summary>
/// Tencent answers in GBK, but only its ASCII fields matter here: the company
/// names come from <see cref="Data.Companies"/>. Replace each double-byte
/// character with one placeholder while consuming its trail byte, because a GBK
/// trail byte can itself be '~' and would otherwise split a field in two.
/// Skipping the CodePages table keeps about 750 KB out of the published binary.
/// </summary>
internal static class Gbk
{
    public static string DecodeLossy(byte[] bytes)
    {
        var chars = new char[bytes.Length];
        var length = 0;

        for (var i = 0; i < bytes.Length; i++)
        {
            var value = bytes[i];

            if (value >= 0x81 && i + 1 < bytes.Length)
            {
                chars[length++] = '?';
                i++;
            }
            else
            {
                chars[length++] = value < 0x80 ? (char)value : '?';
            }
        }

        return new string(chars, 0, length);
    }
}

/// <summary>Small parsers shared by the feeds.</summary>
internal static class Wire
{
    /// <summary>Tencent answers one `v_&lt;key&gt;="field~field~..."` line per symbol.</summary>
    public static IEnumerable<(string Key, string Payload)> Tencent(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("v_", StringComparison.Ordinal)) continue;

            var equals = line.IndexOf('=');
            if (equals <= 2) continue;

            var key = line[2..equals].Trim();

            var open = line.IndexOf('"', equals);
            var close = line.LastIndexOf('"');
            if (open < 0 || close <= open) continue;

            yield return (key, line[(open + 1)..close]);
        }
    }

    public static float ToFloat(string text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0f;

    public static float ToFloat(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? ToFloat(value.GetString() ?? "") : 0f;
}

// ---------------------------------------------------------------------------
// Feeds: one class per endpoint family, each knowing only its own URLs and shape.
// ---------------------------------------------------------------------------

internal sealed class QuoteFeed(HttpText http)
{
    private static readonly string Symbols = string.Join(",", Data.Companies.Select(static company => company.Key));

    public async Task<Dictionary<string, RawQuote>> ReadAsync()
    {
        var quotes = new Dictionary<string, RawQuote>(StringComparer.Ordinal);

        var text = await http.GetAsync("https://qt.gtimg.cn/q=" + Symbols, gbk: true).ConfigureAwait(false);
        if (text is null) return quotes;

        foreach (var (key, payload) in Wire.Tencent(text))
        {
            // Tencent fields: [3] price, [4] previous close, [32] percent, [44]/[45] market cap.
            var fields = payload.Split('~');
            if (fields.Length <= 45) continue;

            var cap = Wire.ToFloat(fields[45]);
            if (cap <= 0) cap = Wire.ToFloat(fields[44]);

            quotes[key] = new RawQuote(
                Price: Wire.ToFloat(fields[3]),
                Prev: Wire.ToFloat(fields[4]),
                Pct: Wire.ToFloat(fields[32]),
                CapLocal: cap);
        }

        return quotes;
    }

    public async Task<Dictionary<string, float>> ReadFxAsync()
    {
        var rates = new Dictionary<string, float>(StringComparer.Ordinal);

        var text = await http.GetAsync("https://qt.gtimg.cn/q=whUSDHKD,whUSDJPY,whHKDCNY", gbk: true)
            .ConfigureAwait(false);
        if (text is null) return rates;

        foreach (var (key, payload) in Wire.Tencent(text))
        {
            var fields = payload.Split('~');
            if (fields.Length <= 4) continue;

            var rate = Wire.ToFloat(fields[3]);
            if (rate <= 0) continue;

            var symbol = key.StartsWith("wh", StringComparison.Ordinal) ? key[2..] : key;
            rates[symbol] = rate;
        }

        return rates;
    }
}

internal sealed class MinuteFeed(HttpText http)
{
    public async Task<float[]?> ReadAsync(Company company)
    {
        var eastmoney = company.Minute == MinuteKind.Eastmoney;

        var text = await http.GetAsync(UrlFor(company), gbk: !eastmoney).ConfigureAwait(false);
        if (text is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(text);
            var prices = eastmoney
                ? ParseEastmoney(doc.RootElement)
                : ParseTencent(doc.RootElement, company.Key);

            return prices.Count >= 2 ? [.. prices] : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string UrlFor(Company company) => company.Minute switch
    {
        MinuteKind.TencentUs => "https://web.ifzq.gtimg.cn/appstock/app/usMinute/query?code=" + company.Key,
        MinuteKind.TencentHk or MinuteKind.TencentCn =>
            "https://web.ifzq.gtimg.cn/appstock/app/minute/query?code=" + company.Key,
        _ => "https://push2his.eastmoney.com/api/qt/stock/trends2/get?secid=" + company.EastSecId
             + "&fields1=f1,f2,f3,f4,f5,f6,f7,f8&fields2=f51,f52,f53,f54,f55,f56,f57,f58&ndays=1&iscr=0",
    };

    /// <summary>Eastmoney: "hh:mm,price,..." per element under data.trends.</summary>
    private static List<float> ParseEastmoney(JsonElement root)
    {
        var prices = new List<float>(400);

        if (!root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("trends", out var trends)
            || trends.ValueKind != JsonValueKind.Array) return prices;

        foreach (var element in trends.EnumerateArray())
        {
            var line = element.GetString();
            if (line is null) continue;

            var fields = line.Split(',');
            if (fields.Length < 3) continue;

            var price = Wire.ToFloat(fields[2]);
            if (price > 0) prices.Add(price);
        }

        return prices;
    }

    /// <summary>Tencent: "hh:mm price" per element under data.&lt;key&gt;.data.data.</summary>
    private static List<float> ParseTencent(JsonElement root, string key)
    {
        var prices = new List<float>(400);

        if (!root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty(key, out var node)
            || node.ValueKind != JsonValueKind.Object
            || !node.TryGetProperty("data", out var payload)
            || !payload.TryGetProperty("data", out var lines)
            || lines.ValueKind != JsonValueKind.Array) return prices;

        foreach (var element in lines.EnumerateArray())
        {
            var line = element.GetString();
            if (line is null) continue;

            var fields = line.Split(' ');
            if (fields.Length < 2) continue;

            var price = Wire.ToFloat(fields[1]);
            if (price > 0) prices.Add(price);
        }

        return prices;
    }
}

internal sealed class DailyFeed(HttpText http)
{
    private const int MaxPoints = 132;

    public async Task<List<HistoryPoint>?> ReadAsync(Company company)
    {
        var points = await EastmoneyAsync(company).ConfigureAwait(false);
        if (points is not null) return points;

        // Eastmoney throttles bursts from one address. Tencent and Sina carry the
        // same daily closes for the other markets, so the six-month view survives
        // an Eastmoney outage; Japanese companies have no second source here.
        return company.Market switch
        {
            "NASDAQ" or "NYSE" => await SinaAsync(company).ConfigureAwait(false),
            "HKEX" or "SSE" or "SZSE" => await TencentAsync(company).ConfigureAwait(false),
            _ => null,
        };
    }

    private async Task<List<HistoryPoint>?> EastmoneyAsync(Company company)
    {
        var url = "https://push2his.eastmoney.com/api/qt/stock/kline/get?secid=" + company.EastSecId
            + "&klt=101&fqt=1&lmt=132&end=20500101"
            + "&fields1=f1,f2,f3,f4,f5,f6&fields2=f51,f52,f53,f54,f55,f56,f57,f58";

        var text = await http.GetAsync(url).ConfigureAwait(false);
        if (text is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(text);

            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("klines", out var klines)
                || klines.ValueKind != JsonValueKind.Array) return null;

            var points = new List<HistoryPoint>(klines.GetArrayLength());

            foreach (var element in klines.EnumerateArray())
            {
                var line = element.GetString();
                if (line is null) continue;

                var fields = line.Split(',');
                if (fields.Length < 3) continue;

                var close = Wire.ToFloat(fields[2]);
                if (close > 0 && fields[0].Length == 10) points.Add(new HistoryPoint(fields[0], close));
            }

            return Trim(points);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<List<HistoryPoint>?> TencentAsync(Company company)
    {
        var end = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var start = DateTime.UtcNow.AddDays(-200).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var url = "https://web.ifzq.gtimg.cn/appstock/app/fqkline/get?param=" + company.Key
            + ",day," + start + "," + end + ",320,qfq";

        var text = await http.GetAsync(url).ConfigureAwait(false);
        if (text is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(text);

            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty(company.Key, out var node)
                || node.ValueKind != JsonValueKind.Object) return null;

            if (!node.TryGetProperty("qfqday", out var rows)
                && !node.TryGetProperty("day", out rows)) return null;
            if (rows.ValueKind != JsonValueKind.Array) return null;

            var points = new List<HistoryPoint>(rows.GetArrayLength());

            foreach (var element in rows.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() < 3) continue;

                var date = element[0].GetString();
                var close = Wire.ToFloat(element[2]);

                if (date is { Length: 10 } && close > 0) points.Add(new HistoryPoint(date, close));
            }

            return Trim(points);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<List<HistoryPoint>?> SinaAsync(Company company)
    {
        var symbol = Uri.EscapeDataString(company.Key[2..]);

        var url =
            "https://stock.finance.sina.com.cn/usstock/api/jsonp.php/var%20history=/US_MinKService.getDailyK?symbol="
            + symbol;

        var text = await http.GetAsync(url).ConfigureAwait(false);
        if (text is null) return null;

        // The answer is a JSONP call: keep the array between the first '[' and
        // the last ']'.
        var open = text.IndexOf('[');
        var close = text.LastIndexOf(']');
        if (open < 0 || close <= open) return null;

        try
        {
            using var doc = JsonDocument.Parse(text[open..(close + 1)]);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            var points = new List<HistoryPoint>(doc.RootElement.GetArrayLength());

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !element.TryGetProperty("d", out var day)
                    || !element.TryGetProperty("c", out var value)) continue;

                var date = day.GetString();
                var price = Wire.ToFloat(value);

                if (date is { Length: 10 } && price > 0) points.Add(new HistoryPoint(date, price));
            }

            return Trim(points);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<HistoryPoint>? Trim(List<HistoryPoint> points)
    {
        if (points.Count < 2) return null;
        if (points.Count > MaxPoints) points.RemoveRange(0, points.Count - MaxPoints);
        return points;
    }
}

// ---------------------------------------------------------------------------
// What the tracker keeps between refreshes.
// ---------------------------------------------------------------------------

/// <summary>USD/HKD, USD/JPY and HKD/CNY, with last-resort defaults.</summary>
internal sealed class FxRates
{
    public float UsdHkd { get; private set; } = 7.80f;
    public float UsdJpy { get; private set; } = 152.0f;
    public float HkdCny { get; private set; } = 0.85f;

    public float UsdCny => UsdHkd * HkdCny;

    public void Update(IReadOnlyDictionary<string, float> rates)
    {
        if (rates.TryGetValue("USDHKD", out var hkd) && hkd > 0) UsdHkd = hkd;
        if (rates.TryGetValue("USDJPY", out var jpy) && jpy > 0) UsdJpy = jpy;
        if (rates.TryGetValue("HKDCNY", out var cny) && cny > 0) HkdCny = cny;
    }

    public float PerUsd(string currency) => currency switch
    {
        "USD" => 1f,
        "HKD" => UsdHkd,
        "CNY" => UsdCny,
        "JPY" => UsdJpy,
        _ => 1f,
    };
}

/// <summary>Daily closes per company, refreshed every ten minutes.</summary>
internal sealed class HistoryBook
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, List<HistoryPoint>> _points = [with(StringComparer.Ordinal)];
    private readonly Dictionary<string, DateTime> _fetchedAt = [with(StringComparer.Ordinal)];

    public bool IsStale(Company company, DateTime now) =>
        !_points.ContainsKey(company.Key)
        || !_fetchedAt.TryGetValue(company.Key, out var at)
        || now - at > MaxAge;

    public void Store(Company company, List<HistoryPoint>? points, DateTime now)
    {
        if (points is not { Count: >= 2 }) return;

        _points[company.Key] = points;
        _fetchedAt[company.Key] = now;
    }

    public List<HistoryPoint>? Get(string key) => _points.GetValueOrDefault(key);
}

/// <summary>Intraday prices per company, replaced on every refresh.</summary>
internal sealed class MinuteBook
{
    private readonly Dictionary<string, float[]> _prices = [with(StringComparer.Ordinal)];

    public void Store(Company company, float[]? prices)
    {
        if (prices is { Length: >= 2 }) _prices[company.Key] = prices;
    }

    public float[]? Get(string key) => _prices.GetValueOrDefault(key);
}

// ---------------------------------------------------------------------------
// The background loop: one immutable snapshot at a time, published by
// assignment, so the UI thread never locks and never blocks.
// ---------------------------------------------------------------------------

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The tracker lives for the process lifetime; the OS reclaims it at exit.")]
internal sealed class MarketTracker
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(30);

    private readonly HttpText _http = new();
    private readonly QuoteFeed _quotes;
    private readonly MinuteFeed _minutes;
    private readonly DailyFeed _daily;
    private readonly FxRates _fx = new();
    private readonly HistoryBook _history = new();
    private readonly MinuteBook _minuteBook = new();
    private readonly ManualResetEventSlim _first = new();
    private readonly SemaphoreSlim _wake = new(0);

    private volatile Snapshot? _snapshot;
    private volatile bool _busy;

    public MarketTracker()
    {
        _quotes = new QuoteFeed(_http);
        _minutes = new MinuteFeed(_http);
        _daily = new DailyFeed(_http);

        // Draw the last session's numbers immediately; the loop below replaces
        // them with live data as soon as its first build finishes.
        _snapshot = Cache.Load();
    }

    public Snapshot? Current => _snapshot;
    public bool IsBusy => _busy;

    public void Start()
    {
        var thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "market-tracker",
        };

        thread.Start();
    }

    public void RequestRefresh()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    public void WaitForFirst(TimeSpan timeout) => _first.Wait(timeout);

    private void Loop()
    {
        while (true)
        {
            Refresh();
            _wake.Wait(RefreshEvery);
        }
    }

    private void Refresh()
    {
        var previous = _snapshot;

        try
        {
            _busy = true;

            var next = Build(previous);
            _snapshot = next;

            if (next.Errors == 0) Cache.Save(next);
        }
        catch (Exception error)
        {
            _snapshot = Fallback(previous);
            Log.Write("build failed: " + error.Message);
        }
        finally
        {
            _busy = false;
            _first.Set();
        }
    }

    private static Snapshot Fallback(Snapshot? previous)
    {
        if (previous is null)
            return new Snapshot { UpdatedUtc = DateTime.UtcNow, Errors = 1 };

        previous.Errors++;
        return previous;
    }

    private Snapshot Build(Snapshot? previous)
    {
        // Quotes, FX and minutes do not depend on each other, so they go out together.
        var quotesTask = _quotes.ReadAsync();
        var ratesTask = _quotes.ReadFxAsync();

        var minuteTasks = new Task<float[]?>[Data.Count];
        for (var i = 0; i < Data.Count; i++) minuteTasks[i] = _minutes.ReadAsync(Data.Companies[i]);

        var now = DateTime.UtcNow;
        Company[] stale = [.. Data.Companies.Where(company => _history.IsStale(company, now))];

        var historyTasks = new Task<List<HistoryPoint>?>[stale.Length];
        for (var i = 0; i < stale.Length; i++) historyTasks[i] = _daily.ReadAsync(stale[i]);

        Wait(minuteTasks, "minutes");
        Wait(historyTasks, "history");

        var quotes = quotesTask.GetAwaiter().GetResult();
        var rates = ratesTask.GetAwaiter().GetResult();

        if (quotes.Count == 0 && previous is not null)
        {
            Log.Write("quotes unavailable; keeping the previous snapshot");
            previous.Errors++;
            return previous;
        }

        _fx.Update(rates);

        for (var i = 0; i < Data.Count; i++)
            _minuteBook.Store(Data.Companies[i], Result(minuteTasks[i]));

        for (var i = 0; i < stale.Length; i++)
            _history.Store(stale[i], Result(historyTasks[i]), now);

        var errors = (quotes.Count == 0 ? 1 : 0) + (rates.Count == 0 ? 1 : 0);

        var snapshot = SnapshotFactory.Create(quotes, _fx, _history, _minuteBook, errors);
        ReportMissingHistory(snapshot);
        return snapshot;
    }

    private static void Wait(Task[] tasks, string label)
    {
        try
        {
            Task.WaitAll(tasks);
        }
        catch (Exception error)
        {
            Log.Write($"{label}: {error.Message}");
        }
    }

    private static T? Result<T>(Task<T?> task) where T : class =>
        task.Status == TaskStatus.RanToCompletion ? task.Result : null;

    private static void ReportMissingHistory(Snapshot snapshot)
    {
        if (snapshot.CapSeries.Count >= snapshot.Rows.Length) return;

        var missing = string.Join(",", snapshot.Rows
            .Where(row => !snapshot.CapSeries.ContainsKey(row.Company.Key))
            .Select(row => row.Company.Ticker));

        Log.Write("missing history for: " + missing);
    }
}

// ---------------------------------------------------------------------------
// Assembly. Pure: no I/O, no clock beyond "now" �?feed it the books and it
// returns the next snapshot.
// ---------------------------------------------------------------------------

internal static class SnapshotFactory
{
    private const int MinuteGrid = 241;

    public static Snapshot Create(
        IReadOnlyDictionary<string, RawQuote> quotes,
        FxRates fx,
        HistoryBook history,
        MinuteBook minutes,
        int errors)
    {
        var rows = BuildRows(quotes, fx);
        var dates = CollectDates(rows, history);

        return new Snapshot
        {
            UpdatedUtc = DateTime.UtcNow,
            Rows = rows,
            DateLabels = BuildLabels(dates),
            CapSeries = BuildCapSeries(rows, history, dates),
            PctSeries = BuildPctSeries(rows, minutes),
            UsdHkd = fx.UsdHkd,
            UsdJpy = fx.UsdJpy,
            UsdCny = fx.UsdCny,
            Errors = errors,
        };
    }

    /// <summary>One row per company with a usable quote, converted to USD, largest first.</summary>
    private static QuoteRow[] BuildRows(IReadOnlyDictionary<string, RawQuote> quotes, FxRates fx)
    {
        var rows = new List<QuoteRow>(Data.Count);

        foreach (var company in Data.Companies)
        {
            if (!quotes.TryGetValue(company.Key, out var quote)) continue;
            if (quote is not { Price: > 0, CapLocal: > 0 }) continue;

            var perUsd = fx.PerUsd(company.Currency);
            if (perUsd <= 0) continue;

            rows.Add(new QuoteRow(
                company,
                quote.Price,
                quote.Prev,
                quote.Prev > 0 ? (quote.Price / quote.Prev - 1f) * 100f : quote.Pct,
                quote.CapLocal * 1e8f / perUsd));
        }

        rows.Sort(static (a, b) => b.CapUsd.CompareTo(a.CapUsd));
        return [.. rows];
    }

    /// <summary>Union of the trading dates the rows have, so every series shares one axis.</summary>
    private static SortedSet<string> CollectDates(QuoteRow[] rows, HistoryBook history)
    {
        var dates = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (history.Get(row.Company.Key) is not { } points) continue;
            foreach (var point in points) dates.Add(point.Date);
        }

        return dates;
    }

    private static string[] BuildLabels(SortedSet<string> dates)
    {
        var labels = new string[dates.Count + 1];
        var i = 0;

        foreach (var date in dates) labels[i++] = Label(date);
        labels[i] = "Now";

        return labels;
    }

    private static string Label(string date) =>
        DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToString("MMM d", CultureInfo.InvariantCulture)
            : date;

    /// <summary>Six months of market cap in USD trillions, forward filled, ending at "now".</summary>
    private static Dictionary<string, float[]> BuildCapSeries(
        QuoteRow[] rows, HistoryBook history, SortedSet<string> dates)
    {
        var series = new Dictionary<string, float[]>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (history.Get(row.Company.Key) is not { Count: >= 2 } points) continue;

            var byDate = new Dictionary<string, float>(points.Count, StringComparer.Ordinal);
            foreach (var point in points) byDate[point.Date] = point.Close;

            var last = points[0].Close;
            var sharesInTrillions = row.CapUsd / row.Price / 1e12f;
            var values = new float[dates.Count + 1];

            var i = 0;
            foreach (var date in dates)
            {
                if (byDate.TryGetValue(date, out var close)) last = close;
                values[i++] = last * sharesInTrillions;
            }

            values[^1] = row.CapUsd / 1e12f;
            series[row.Company.Key] = values;
        }

        return series;
    }

    /// <summary>Percent from previous close over each market's own session, resampled to one grid.</summary>
    private static Dictionary<string, float[]> BuildPctSeries(QuoteRow[] rows, MinuteBook minutes)
    {
        var series = new Dictionary<string, float[]>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var values = new float[MinuteGrid];

            if (minutes.Get(row.Company.Key) is { Length: >= 2 } prices)
            {
                var prevClose = row.Prev > 0 ? row.Prev : prices[0];
                if (prevClose <= 0) prevClose = prices[0];

                for (var k = 0; k < MinuteGrid; k++)
                {
                    var index = (int)MathF.Round(k / (float)(MinuteGrid - 1) * (prices.Length - 1));
                    values[k] = (prices[index] / prevClose - 1f) * 100f;
                }
            }
            else
            {
                Array.Fill(values, row.Pct);
            }

            series[row.Company.Key] = values;
        }

        return series;
    }
}

// ---------------------------------------------------------------------------
// Cache: the last good snapshot, so the first frame has numbers to draw instead
// of a spinner. Written after every successful build, read once at startup and
// replaced by the tracker's first live build a few seconds later.
// ---------------------------------------------------------------------------

internal static class Cache
{
    private const int Version = 1;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GlobalMarketCap",
        "snapshot.json");

    private static readonly Dictionary<string, Company> ByKey =
        Data.Companies.ToDictionary(static company => company.Key, StringComparer.Ordinal);

    public static Snapshot? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllBytes(FilePath));
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("v", out var version) || version.GetInt32() != Version) return null;
            if (!root.TryGetProperty("updated", out var updated)) return null;
            if (!root.TryGetProperty("rows", out var rowArray)
                || rowArray.ValueKind != JsonValueKind.Array) return null;

            var labels = ReadStrings(root, "labels");
            if (labels.Length == 0) return null;

            var capSeries = new Dictionary<string, float[]>(StringComparer.Ordinal);
            var pctSeries = new Dictionary<string, float[]>(StringComparer.Ordinal);
            var rows = new List<QuoteRow>(Data.Count);

            foreach (var element in rowArray.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;
                if (!element.TryGetProperty("k", out var keyElement)) continue;

                var key = keyElement.GetString();
                if (key is null || !ByKey.TryGetValue(key, out var company)) continue;

                rows.Add(new QuoteRow(
                    company,
                    Number(element, "p"),
                    Number(element, "prev"),
                    Number(element, "pct"),
                    Number(element, "cap")));

                var caps = ReadFloats(element, "caps");
                if (caps.Length > 0) capSeries[key] = caps;

                var pcts = ReadFloats(element, "pcts");
                if (pcts.Length > 0) pctSeries[key] = pcts;
            }

            if (rows.Count == 0) return null;
            rows.Sort(static (a, b) => b.CapUsd.CompareTo(a.CapUsd));

            return new Snapshot
            {
                UpdatedUtc = DateTimeOffset.FromUnixTimeSeconds(updated.GetInt64()).UtcDateTime,
                Rows = [.. rows],
                CapSeries = capSeries,
                PctSeries = pctSeries,
                DateLabels = labels,
                UsdHkd = Number(root, "hkd"),
                UsdJpy = Number(root, "jpy"),
                UsdCny = Number(root, "cny"),
                Cached = true,
            };
        }
        catch (Exception error)
        {
            Log.Write("cache load: " + error.Message);
            return null;
        }
    }

    public static void Save(Snapshot snapshot)
    {
        if (snapshot.Rows.Length == 0) return;

        try
        {
            var folder = Path.GetDirectoryName(FilePath);
            if (folder is not null) Directory.CreateDirectory(folder);

            var temporary = FilePath + ".tmp";

            using (var stream = File.Create(temporary))
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("v", Version);
                writer.WriteNumber("updated", new DateTimeOffset(snapshot.UpdatedUtc).ToUnixTimeSeconds());
                writer.WriteNumber("hkd", snapshot.UsdHkd);
                writer.WriteNumber("jpy", snapshot.UsdJpy);
                writer.WriteNumber("cny", snapshot.UsdCny);

                writer.WriteStartArray("labels");
                foreach (var label in snapshot.DateLabels) writer.WriteStringValue(label);
                writer.WriteEndArray();

                writer.WriteStartArray("rows");
                foreach (var row in snapshot.Rows)
                {
                    writer.WriteStartObject();
                    writer.WriteString("k", row.Company.Key);
                    writer.WriteNumber("p", row.Price);
                    writer.WriteNumber("prev", row.Prev);
                    writer.WriteNumber("pct", row.Pct);
                    writer.WriteNumber("cap", row.CapUsd);

                    if (snapshot.CapSeries.TryGetValue(row.Company.Key, out var caps))
                        WriteFloats(writer, "caps", caps);
                    if (snapshot.PctSeries.TryGetValue(row.Company.Key, out var pcts))
                        WriteFloats(writer, "pcts", pcts);

                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                writer.WriteEndObject();
            }

            // Replace in one step so a crash mid-write cannot leave a torn file.
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception error)
        {
            Log.Write("cache save: " + error.Message);
        }
    }

    private static void WriteFloats(Utf8JsonWriter writer, string name, float[] values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values) writer.WriteNumberValue(value);
        writer.WriteEndArray();
    }

    private static float[] ReadFloats(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return [];

        var values = new float[array.GetArrayLength()];
        var i = 0;

        foreach (var element in array.EnumerateArray()) values[i++] = element.GetSingle();

        return values;
    }

    private static string[] ReadStrings(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return [];

        var values = new string[array.GetArrayLength()];
        var i = 0;

        foreach (var element in array.EnumerateArray()) values[i++] = element.GetString() ?? "";

        return values;
    }

    private static float Number(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetSingle()
            : 0f;
}

// ---------------------------------------------------------------------------
// Wire types and the one log sink.
// ---------------------------------------------------------------------------

internal readonly record struct HistoryPoint(string Date, float Close);

internal readonly record struct RawQuote(float Price, float Prev, float Pct, float CapLocal);

internal static class Log
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("TRACK_DEBUG") is "1";

    public static void Write(string message)
    {
        if (Enabled) Console.Error.WriteLine("[track] " + message);
    }
}
