#:package CuiSharp@*

// The properties below are what make `dotnet publish stock.cs -c Release -o .`
// leave one small native executable beside this file, with no .NET on the target.
// The size profile, including the diagnostics below, comes from CuiSharp.Size.targets.
#:property PublishAot=true
#:property OptimizationPreference=Size
#:property InvariantGlobalization=true
#:property StripSymbols=true
#:property DebugType=none
#:property OutputType=WinExe
#:property ApplicationIcon=stock.ico

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CuiSharp;
using CuiSharp.Hosting;

// ---------------------------------------------------------------------------
// Global Market Cap — a native CuiSharp market tracker over two switchable feeds:
// Eastmoney (东方财富, reachable from mainland China) and TradingView + Yahoo Finance.
// The footer switch picks one, and the choice is saved beside the cached snapshot.
//
//   run      dotnet run stock.cs                        opens in MiniView by default
//   ship     dotnet publish stock.cs -c Release -o .    one native executable, no runtime needed
//            (add -p:CuiSharpHeadless=true to publish a build `dotnet cui` can drive)
//
//   iterate  CUISHARP_HEADLESS=1 ./stock               no window; serves the tool's protocol
//            dotnet cui tree                            the controls the last frame drew
//            dotnet cui click Refresh                   click one, print the frame it produced
//            dotnet cui png shot.png                    that frame as a PNG
//            dotnet cui docs read live-debugging        the guide, offline
//            dotnet cui api ChartOpts                   one exact declaration
//            dotnet cui session --script steps.txt      many clicks against one app instance
//
// Architecture from top to bottom:
//   1. Dashboard window & responsive layout (MiniView default, Narrow, Wide)
//   2. MiniView, HeaderView, SummaryView, ChartPanel, RankingPanel, FooterView
//   3. Reusable UI extensions (`extension(Ui ui)`) & formatting helpers
//   4. Domain model & C# 15 union types (`MarketView`, `ChartHorizon`)
//   5. MarketService (`Mutation<MarketView>`), strongly-typed HTTP + JSON & Cache
// ---------------------------------------------------------------------------

using var service = new MarketService();

// Headless screenshots and tree dumps are most useful once the first live snapshot arrives.
if (Environment.GetEnvironmentVariable("CUISHARP_HEADLESS") is "1" or "true")
    await service.WarmUpAsync();

var dashboard = new Dashboard(service);
dashboard.Options.WindowCreated += dashboard.OnWindowCreated;

return CuiApp.Run(dashboard.Options, dashboard.Draw);

// ---------------------------------------------------------------------------
// 1. The window. Opens in MiniView by default; widens into Narrow or Wide
//    dashboard layouts when resized or inspected at desktop viewports.
// ---------------------------------------------------------------------------

internal sealed class Dashboard(MarketService service)
{
    private const float MiniBreakpoint = 660f;
    private const float WideBreakpoint = 1000f;

    private readonly DashboardState _state = new(service.SelectedSource);
    private readonly ChartPanel _chart = new();
    private CuiWindow? _window;

    public CuiAppOptions Options { get; } = new()
    {
        Title = "Global Market Cap",
        Width = 420,
        Height = 560,
        MinWidth = 340,
        MinHeight = 400,
        Theme = Theme.Dark,
        Accent = Accent.Blue,
        Renderer = Renderer.Software,
    };

    public void OnWindowCreated(CuiWindow window) => _window = window;

    public void Draw(Ui ui)
    {
        ApplyStyle(ui);

        if (ui.KeyPressed(Key.Escape)) _window?.RequestClose();

        // The footer binds the switch by ref, so the choice arrives one frame after the click.
        if (_state.SourceIndex != service.SelectedSource)
            service.SelectSource(_state.SourceIndex);

        var (width, height) = ui.ViewportSize();
        var mini = width < MiniBreakpoint;
        var visibleCount = mini ? MiniView.DefaultRows : _state.TopCount(Catalog.Count);

        service.EnsureFresh(visibleCount);

        var view = service.Current;
        if (view is MarketSnapshot) ui.MarkReady();

        if (mini) MiniView.Draw(ui, service, view, _state);
        else if (width >= WideBreakpoint) DrawWide(ui, view, width, height);
        else DrawNarrow(ui, view, height);

        // Sleep when idle; wake once every 30 seconds to trigger the next background refresh.
        if (!service.IsBusy) ui.RequestWakeup(MarketService.RefreshSeconds);
    }

    private static void ApplyStyle(Ui ui)
    {
        var style = ui.Style;
        style.RadiusSurface = 10;
        style.RadiusControl = 6;
        style.RadiusPanel = 8;
    }

    private void DrawWide(Ui ui, MarketView view, float width, float height)
    {
        var listWidth = Math.Clamp(width * 0.31f, 310f, 430f);
        var chartHeight = Math.Max(260f, height - 352f);

        using (ui.Column(new Box { Pad = 14, Gap = 12, Height = Size.Fill }))
        {
            HeaderView.Draw(ui, service, _state, wide: true);
            SummaryView.Draw(ui, view, _state, wide: true);

            using (ui.Row(new Box { Gap = 12, Width = Size.Fill, Height = Size.Fill, Align = Align.Stretch }))
            {
                _chart.Draw(ui, view, _state, wide: true, chartHeight);
                RankingPanel.Draw(ui, view, _state, Size.Fill, Size.Px(listWidth));
            }

            FooterView.Draw(ui, service, _state, view, wide: true);
        }
    }

    private void DrawNarrow(Ui ui, MarketView view, float height)
    {
        var chartHeight = Math.Clamp(height * 0.40f, 240f, 400f);

        using (ui.Scroll(new ScrollOpts { Height = Size.Fill, Pad = 14, Gap = 12 }))
        using (ui.Column(new Box { Gap = 12 }))
        {
            HeaderView.Draw(ui, service, _state, wide: false);
            SummaryView.Draw(ui, view, _state, wide: false);
            _chart.Draw(ui, view, _state, wide: false, chartHeight);
            RankingPanel.Draw(ui, view, _state, Size.Px(440), Size.Fill);
            FooterView.Draw(ui, service, _state, view, wide: false);
        }
    }
}

internal sealed class DashboardState(int sourceIndex)
{
    public const int DefaultTopCount = 12;

    public static readonly string[] ModeLabels = ["6 months", "Today"];
    public static readonly string[] CompactModeLabels = ["6M", "1D"];

    private readonly HashSet<string> _selected = [with(StringComparer.Ordinal)];

    // Public fields so CuiSharp's Number and Segmented controls can bind by ref.
    public int ModeIndex;
    public double TopN = DefaultTopCount;
    public int SourceIndex = sourceIndex;

    public ChartHorizon Horizon => ModeIndex == 0
        ? SixMonthHorizon.Instance
        : IntradayHorizon.Instance;

    public int TopCount(int available) =>
        available <= 0 ? 0 : Math.Clamp((int)TopN, 1, available);

    public bool IsSelected(string ticker) => _selected.Contains(ticker);

    public bool AnySelected(ReadOnlySpan<QuoteRow> rows, int count)
    {
        var limit = Math.Min(count, rows.Length);
        for (var i = 0; i < limit; i++)
            if (_selected.Contains(rows[i].Ticker)) return true;
        return false;
    }

    public void Toggle(string ticker)
    {
        if (!_selected.Add(ticker)) _selected.Remove(ticker);
    }

    public void Clear() => _selected.Clear();
}

// ---------------------------------------------------------------------------
// 2. Views: MiniView (default compact window) and the full Dashboard panels.
// ---------------------------------------------------------------------------

internal static class MiniView
{
    public const int DefaultRows = 7;

    public static void Draw(Ui ui, MarketService service, MarketView view, DashboardState state)
    {
        using (ui.Column(new Box { Pad = 12, Gap = 8, Height = Size.Fill }))
        {
            DrawHeader(ui, service, state);

            switch (view)
            {
                case MarketLoading:
                    ui.LoadingCenter("Loading global markets...", Size.Fill);
                    DrawFooter(ui, service, state, "");
                    break;

                case MarketError(var message, _):
                    ui.ErrorCenter("Market feed unavailable", message, Size.Fill);
                    DrawFooter(ui, service, state, "");
                    break;

                case MarketSnapshot snapshot:
                    DrawContent(ui, service, snapshot, state);
                    break;
            }
        }
    }

    private static void DrawHeader(Ui ui, MarketService service, DashboardState state)
    {
        var busy = service.IsBusy;

        using (ui.Row(new Box { Align = Align.Center, Gap = 8 }))
        {
            ui.Text("GLOBAL MARKETS", new TextOpts
            {
                Font = Font.Medium,
                Size = 10,
                Color = Color.Accent,
                NoWrap = true,
            });

            ui.Badge(busy ? "Syncing" : "Live · 30s", new BadgeOpts
            {
                Tone = busy ? Tone.Info : Tone.Success,
                Dot = !busy,
                Size = ControlSize.Sm,
            });

            ui.Spacer();

            if (ui.Button("Refresh", new ButtonOpts
            {
                Icon = Icons.RefreshCw,
                Size = ControlSize.Sm,
                Variant = ButtonVariant.Secondary,
                Loading = busy,
            }))
            {
                service.RequestRefresh();
            }
        }

        using (ui.Row(new Box { Align = Align.Center, Gap = 8 }))
        {
            ui.Heading("Market cap leaders", 2);
            ui.Spacer();

            using (ui.Box(new Box { Width = Size.Px(104) }))
                ui.Segmented("mini-mode", DashboardState.CompactModeLabels, ref state.ModeIndex, new SegmentedOpts { FullWidth = true });
        }
    }

    private static void DrawContent(Ui ui, MarketService service, MarketSnapshot snapshot, DashboardState state)
    {
        var count = Math.Min(DefaultRows, snapshot.Rows.Length);
        var anySelected = state.AnySelected(snapshot.Rows, count);
        var stats = SummaryStats.Compute(snapshot.Rows.AsSpan(0, count));

        DrawSummaryBanner(ui, snapshot, stats);

        using (ui.Scroll(new ScrollOpts { Height = Size.Fill, Width = Size.Fill, Pad = 0, Gap = 0 }))
        using (ui.Column(new Box { Gap = 5, Width = Size.Fill }))
        {
            for (var i = 0; i < count; i++)
            {
                var row = snapshot.Rows[i];
                var selected = state.IsSelected(row.Ticker);
                var dimmed = anySelected && !selected;

                if (DrawMiniRow(ui, i + 1, row, state.Horizon, selected, dimmed))
                    state.Toggle(row.Ticker);
            }
        }

        DrawFooter(ui, service, state, snapshot.UpdatedLabel);
    }

    /// <summary>The feed switch has to stay reachable while a feed is failing, so every state draws it.</summary>
    private static void DrawFooter(Ui ui, MarketService service, DashboardState state, string updated)
    {
        using (ui.Row(new Box { Align = Align.Center, Gap = 8 }))
        {
            ui.SourceSwitch(service, state);
            ui.Spacer();
            if (updated.Length > 0) ui.FooterTime(updated);
        }
    }

    private static void DrawSummaryBanner(Ui ui, MarketSnapshot snapshot, SummaryStats stats)
    {
        using (ui.Card(new Card
        {
            Width = Size.Fill,
            PadX = 12,
            PadY = 8,
            Dir = Dir.Row,
            Align = Align.Center,
            Gap = 10,
            Muted = true,
        }))
        {
            using (ui.Column(new Box { Gap = 1 }))
            {
                ui.Text($"Top {stats.Count} combined", new TextOpts { Size = 10, Color = Color.TextMuted });
                ui.Text($"{Format.Money(stats.TotalCapUsd)}", new TextOpts
                {
                    Font = Font.Medium,
                    Size = 15,
                    Tabular = true,
                });
            }

            ui.VLine(28);

            using (ui.Column(new Box { Gap = 1 }))
            {
                ui.Text("Weighted today", new TextOpts { Size = 10, Color = Color.TextMuted });
                ui.Text($"{Format.Percent(stats.WeightedPct)}", new TextOpts
                {
                    Font = Font.Medium,
                    Size = 13,
                    Tabular = true,
                    Color = Format.PercentColor(stats.WeightedPct),
                });
            }

            ui.Spacer();

            using (ui.Column(new Box { Gap = 1, Align = Align.End }))
            {
                ui.Text($"{stats.Up} up · {stats.Down} down", new TextOpts
                {
                    Size = 11,
                    Tabular = true,
                    Color = Color.TextSecondary,
                });
                ui.Text($"CNY {snapshot.Fx.UsdCny:0.00} · JPY {snapshot.Fx.UsdJpy:0.0}", new TextOpts
                {
                    Size = 10,
                    Tabular = true,
                    Color = Color.TextMuted,
                });
            }
        }
    }

    private static bool DrawMiniRow(
        Ui ui,
        int rank,
        QuoteRow row,
        ChartHorizon horizon,
        bool selected,
        bool dimmed)
    {
        var company = row.Company;
        var series = row.SeriesFor(horizon);

        using (ui.Card(new Card
        {
            Id = company.MiniCardId,
            Hoverable = true,
            Selected = selected,
            Width = Size.Fill,
            PadX = 10,
            PadY = 6,
            Dir = Dir.Row,
            Gap = 8,
            Align = Align.Center,
        }, out var clicked))
        {
            ui.Text($"{rank}", new TextOpts
            {
                Size = 11,
                Tabular = true,
                Align = TextAlign.Right,
                Color = Color.TextMuted,
                Width = Size.Px(16),
            });

            using (ui.Box(new Box
            {
                Width = Size.Px(7),
                Height = Size.Px(7),
                Radius = 4,
                Bg = dimmed ? Palette.Dim(company) : Palette.ColorOf(company),
            })) { }

            using (ui.Column(new Box { Width = Size.Fill, MinWidth = 0, Gap = 1 }))
            {
                using (ui.Row(new Box { Align = Align.Center, Gap = 6 }))
                {
                    ui.Text(company.Ticker, new TextOpts
                    {
                        Font = Font.Medium,
                        Size = 13,
                        NoWrap = true,
                        Color = dimmed ? Color.TextSecondary : Color.Foreground,
                    });

                    ui.Text(row.PriceText, new TextOpts
                    {
                        Size = 11,
                        Tabular = true,
                        NoWrap = true,
                        Color = Color.TextMuted,
                    });
                }

                ui.Text(company.Subtitle, new TextOpts
                {
                    Size = 10,
                    NoWrap = true,
                    Color = dimmed ? Color.TextMuted : Color.TextSecondary,
                });
            }

            if (series is { Length: >= 2 })
            {
                ui.Sparkline(series, Size.Px(104), 26f,
                    dimmed ? Palette.Dim(company) : Palette.ColorOf(company));
            }
            else
            {
                using (ui.Box(new Box { Width = Size.Px(104), Height = Size.Px(26) })) { }
            }

            using (ui.Column(new Box { Width = Size.Px(68), Gap = 1, Align = Align.End }))
            {
                ui.Text(row.CapText, new TextOpts
                {
                    Size = 13,
                    Font = Font.Medium,
                    Tabular = true,
                    Align = TextAlign.Right,
                    Color = dimmed ? Color.TextSecondary : Color.Foreground,
                });

                ui.Text(row.PctText, new TextOpts
                {
                    Size = 11,
                    Tabular = true,
                    Align = TextAlign.Right,
                    Color = dimmed ? Color.TextMuted : Format.PercentColor(row.ChangePct),
                });
            }

            return clicked;
        }
    }
}

internal static class HeaderView
{
    private static readonly string ScopeBadge = $"{Catalog.Count} companies · {Catalog.ExchangeCount} exchanges";

    public static void Draw(Ui ui, MarketService service, DashboardState state, bool wide)
    {
        if (wide)
        {
            using (ui.Row(new Box { Align = Align.Center, Gap = 12, Wrap = true }))
            {
                DrawTitle(ui);
                ui.Spacer();
                DrawControls(ui, service, state);
            }
        }
        else
        {
            using (ui.Column(new Box { Gap = 10 }))
            {
                DrawTitle(ui);
                using (ui.Row(new Box { Align = Align.Center, Gap = 8, Wrap = true }))
                    DrawControls(ui, service, state);
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

                ui.Badge(ScopeBadge, new BadgeOpts
                {
                    Tone = Tone.Neutral,
                    Size = ControlSize.Sm,
                });
            }

            ui.Heading("Market cap leaders", 1);
        }
    }

    private static void DrawControls(Ui ui, MarketService service, DashboardState state)
    {
        var busy = service.IsBusy;

        ui.Badge(busy ? "Updating" : "Live · 30s", new BadgeOpts
        {
            Tone = busy ? Tone.Info : Tone.Success,
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
            service.RequestRefresh();
        }

        ui.Text("Top", new TextOpts { Size = 12, Color = Color.TextSecondary });
        ui.Number("topn", ref state.TopN, 5, Catalog.Count, new NumberOpts
        {
            Width = Size.Px(96),
            Step = 1,
            Decimals = 0,
        });

        using (ui.Box(new Box { Width = Size.Px(190) }))
            ui.Segmented("mode", DashboardState.ModeLabels, ref state.ModeIndex, new SegmentedOpts { FullWidth = true });
    }
}

internal static class SummaryView
{
    public static void Draw(Ui ui, MarketView view, DashboardState state, bool wide)
    {
        if (view is not MarketSnapshot { Rows.Length: > 0 } snapshot) return;

        var count = state.TopCount(snapshot.Rows.Length);
        var stats = SummaryStats.Compute(snapshot.Rows.AsSpan(0, count));

        if (wide) DrawRow(ui, snapshot, stats);
        else DrawGrid(ui, snapshot, stats);
    }

    private static void DrawRow(Ui ui, MarketSnapshot snapshot, SummaryStats stats)
    {
        using (ui.Card(new Card { PadY = 12, PadX = 0.1f, Dir = Dir.Row, Gap = 0 }))
        {
            ui.Stat("Combined cap", Format.Money(stats.TotalCapUsd),
                $"top {stats.Count} · weighted {Format.Percent(stats.WeightedPct)} today");
            ui.VLine(42);
            ui.Stat("Largest company", stats.Leader.CapText, stats.Leader.Company.Subtitle);
            ui.VLine(42);
            ui.Stat("Today's breadth", $"{stats.Up} up · {stats.Down} down",
                $"{stats.Unchanged} unchanged in top {stats.Count}");
            ui.VLine(42);
            ui.Stat("FX snapshot", snapshot.Fx.Headline, snapshot.Fx.Detail);
        }
    }

    private static void DrawGrid(Ui ui, MarketSnapshot snapshot, SummaryStats stats)
    {
        using (ui.Box(new Box { Dir = Dir.Grid, Columns = 2, MinColumnWidth = 190, Gap = 10 }))
        {
            ui.StatCard("Combined cap", Format.Money(stats.TotalCapUsd),
                $"top {stats.Count} · weighted {Format.Percent(stats.WeightedPct)} today");
            ui.StatCard("Largest company", stats.Leader.CapText, stats.Leader.Company.Subtitle);
            ui.StatCard("Today's breadth", $"{stats.Up} up · {stats.Down} down",
                $"{stats.Unchanged} unchanged in top {stats.Count}");
            ui.StatCard("FX snapshot", snapshot.Fx.Headline, snapshot.Fx.Detail);
        }
    }
}

internal sealed class ChartPanel
{
    private readonly Series[] _scratch = new Series[Catalog.Count];
    private Series[] _visible = [];

    public void Draw(Ui ui, MarketView view, DashboardState state, bool wide, float height)
    {
        var horizon = state.Horizon;

        using (ui.Card(new Card
        {
            Width = wide ? Size.Fill : Size.Fit,
            Height = wide ? Size.Fill : Size.Fit,
            Pad = 14,
            Gap = 12,
            Dir = Dir.Column,
        }))
        {
            DrawHeading(ui, horizon);

            switch (view)
            {
                case MarketLoading:
                    ui.LoadingCenter("Loading global markets...", Size.Px(height));
                    return;

                case MarketError(var message, _):
                    ui.ErrorCenter("Waiting for market data", message, Size.Px(height));
                    return;

                case MarketSnapshot snapshot:
                    DrawChartBody(ui, snapshot, state, horizon, wide, height);
                    break;
            }
        }
    }

    private static void DrawHeading(Ui ui, ChartHorizon horizon)
    {
        var (title, subtitle, badge) = horizon switch
        {
            SixMonthHorizon h => (h.Title, h.Subtitle, h.Badge),
            IntradayHorizon h => (h.Title, h.Subtitle, h.Badge),
        };

        using (ui.Row(new Box { Align = Align.Center, Gap = 10, Wrap = true }))
        {
            using (ui.Column(new Box { Gap = 2 }))
            {
                ui.Text(title, new TextOpts { Font = Font.Medium, Size = 15 });
                ui.Muted(subtitle);
            }

            ui.Spacer();

            ui.Badge(badge, new BadgeOpts
            {
                Tone = Tone.Neutral,
                Size = ControlSize.Sm,
            });
        }
    }

    private void DrawChartBody(
        Ui ui,
        MarketSnapshot snapshot,
        DashboardState state,
        ChartHorizon horizon,
        bool wide,
        float height)
    {
        var count = state.TopCount(snapshot.Rows.Length);
        var anySelected = state.AnySelected(snapshot.Rows, count);
        var seriesCount = FillSeries(snapshot.Rows.AsSpan(0, count), horizon, state, anySelected);

        if (seriesCount == 0)
        {
            ui.ErrorCenter(
                "No series available",
                "Chart series for the selected horizon is still syncing.",
                Size.Px(height));
        }
        else
        {
            if (_visible.Length != seriesCount) _visible = new Series[seriesCount];
            Array.Copy(_scratch, _visible, seriesCount);

            var (labels, valueFormat) = horizon switch
            {
                SixMonthHorizon h => (snapshot.DateLabels, h.ValueFormat),
                IntradayHorizon h => (null, h.ValueFormat),
            };

            ui.LineChart("chart", _visible, new ChartOpts
            {
                Height = height,
                Labels = labels,
                Format = valueFormat,
                NoLegend = true,
                Smooth = false,
                Area = false,
            });
        }

        DrawCaption(ui, snapshot, horizon, anySelected, wide);
    }

    private int FillSeries(
        ReadOnlySpan<QuoteRow> rows,
        ChartHorizon horizon,
        DashboardState state,
        bool anySelected)
    {
        var drawn = 0;

        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var values = row.SeriesFor(horizon);
            if (values is not { Length: >= 2 }) continue;

            var company = row.Company;
            var dimmed = anySelected && !state.IsSelected(row.Ticker);

            _scratch[drawn++] = new Series
            {
                Name = company.Ticker,
                Values = values,
                Color = dimmed ? Palette.Dim(company) : Palette.ColorOf(company),
            };
        }

        return drawn;
    }

    private static void DrawCaption(
        Ui ui,
        MarketSnapshot snapshot,
        ChartHorizon horizon,
        bool anySelected,
        bool wide)
    {
        var hint = anySelected
            ? "click a highlighted row to clear"
            : "click a row to isolate its curve";

        var left = horizon switch
        {
            SixMonthHorizon => $"{snapshot.FirstDateLabel} - now · {hint}",
            IntradayHorizon => $"Session open to latest · {hint}",
        };

        const string Right = "Quotes & series: Eastmoney";

        if (wide)
        {
            using (ui.Row(new Box { Align = Align.Center, Gap = 10 }))
            {
                ui.Muted($"{left} · hover to inspect");
                ui.Spacer();
                ui.Muted(Right);
            }
        }
        else
        {
            using (ui.Column(new Box { Gap = 2 }))
            {
                ui.Muted(left);
                ui.Muted(Right);
            }
        }
    }
}

internal static class RankingPanel
{
    public static void Draw(Ui ui, MarketView view, DashboardState state, Size height, Size width)
    {
        var rows = view is MarketSnapshot snapshot ? snapshot.Rows : [];
        var count = state.TopCount(rows.Length);
        var anySelected = state.AnySelected(rows, count);

        using (ui.Card(new Card
        {
            Width = width,
            Height = height,
            Pad = 14,
            Gap = 8,
            Dir = Dir.Column,
        }))
        {
            DrawHeading(ui, state, anySelected);
            ui.Separator();

            if (count == 0)
            {
                ui.ErrorCenter("No quotes yet", "The market feed is still loading.", Size.Fill);
                return;
            }

            using (ui.Scroll(new ScrollOpts { Height = Size.Fill, Width = Size.Fill, Pad = 0, Gap = 0 }))
            using (ui.Column(new Box { Gap = 4, Width = Size.Fill }))
            {
                for (var i = 0; i < count; i++)
                {
                    var row = rows[i];
                    var selected = state.IsSelected(row.Ticker);

                    if (ui.RankRow(i + 1, row, selected, anySelected && !selected))
                        state.Toggle(row.Ticker);
                }
            }
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
}

internal static class FooterView
{
    public static void Draw(Ui ui, MarketService service, DashboardState state, MarketView view, bool wide)
    {
        var updated = view is MarketSnapshot snapshot ? snapshot.UpdatedLabel : "";

        if (wide)
        {
            using (ui.Row(new Box { Align = Align.Center, Gap = 10 }))
            {
                ui.SourceSwitch(service, state);
                ui.Muted(service.Attribution);
                ui.Spacer();
                if (updated.Length > 0) ui.FooterTime(updated);
            }
        }
        else
        {
            using (ui.Column(new Box { Gap = 6 }))
            {
                ui.Muted(service.Attribution);
                using (ui.Row(new Box { Align = Align.Center, Gap = 10 }))
                {
                    ui.SourceSwitch(service, state);
                    ui.Spacer();
                    if (updated.Length > 0) ui.FooterTime(updated);
                }
            }
        }
    }
}

// ---------------------------------------------------------------------------
// 3. C# 14 extension members on `Ui`, formatting, and OKLCH palette.
// ---------------------------------------------------------------------------

internal static class MarketUiExtensions
{
    extension(Ui ui)
    {
        public void Stat(string label, string value, string detail)
        {
            using (ui.Column(new Box { Width = Size.Fill, PadX = 14, Gap = 2 }))
                ui.StatText(label, value, detail);
        }

        public void StatCard(string label, string value, string detail)
        {
            using (ui.Card(new Card { MinWidth = 190, PadX = 14, PadY = 10 }))
            using (ui.Column(new Box { Gap = 2 }))
                ui.StatText(label, value, detail);
        }

        public void StatText(string label, string value, string detail)
        {
            ui.Text(label, new TextOpts { Size = 11, Color = Color.TextMuted });
            ui.Text(value, new TextOpts { Font = Font.Medium, Size = 21, Tabular = true });
            ui.Text(detail, new TextOpts { Size = 11, Color = Color.TextSecondary, NoWrap = true });
        }

        public void FooterTime(string text) =>
            ui.Text(text, new TextOpts { Size = 11, Tabular = true, Color = Color.TextMuted });

        /// <summary>The footer feed switch; writing state.SourceIndex is what commits it.</summary>
        public void SourceSwitch(MarketService service, DashboardState state)
        {
            using (ui.Box(new Box { Width = Size.Px(196) }))
            {
                ui.Segmented("source", service.SourceLabels, ref state.SourceIndex,
                    new SegmentedOpts { FullWidth = true, Size = ControlSize.Sm });
            }
        }

        public void VLine(float height)
        {
            using (ui.Box(new Box { Width = Size.Px(1), Height = Size.Px(height), Bg = Color.Border })) { }
        }

        public void LoadingCenter(string message, Size height)
        {
            using (ui.Row(new Box { Height = height, Width = Size.Fill, Align = Align.Center, Justify = Justify.Center, Gap = 10 }))
            {
                ui.Spinner(18);
                ui.Muted(message);
            }
        }

        public void ErrorCenter(string title, string description, Size height)
        {
            using (ui.Box(new Box { Height = height, Width = Size.Fill, Align = Align.Center, Justify = Justify.Center }))
            {
                ui.EmptyState(title, new EmptyOpts
                {
                    Description = description,
                    Icon = Icons.ChartLine,
                });
            }
        }

        public bool RankRow(int rank, QuoteRow row, bool selected, bool dimmed)
        {
            var company = row.Company;

            using (ui.Card(new Card
            {
                Id = company.RowCardId,
                Hoverable = true,
                Selected = selected,
                Muted = !selected,
                Width = Size.Fill,
                PadX = 10,
                PadY = 6,
                Dir = Dir.Row,
                Gap = 9,
                Align = Align.Center,
            }, out var clicked))
            {
                ui.Text($"{rank}", new TextOpts
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
                    using (ui.Row(new Box { Align = Align.Center, Gap = 6 }))
                    {
                        ui.Text(company.Ticker, new TextOpts
                        {
                            Font = Font.Medium,
                            Size = 13,
                            NoWrap = true,
                            Color = dimmed ? Color.TextSecondary : Color.Foreground,
                        });

                        ui.Text(row.PriceText, new TextOpts
                        {
                            Size = 11,
                            Tabular = true,
                            NoWrap = true,
                            Color = Color.TextMuted,
                        });
                    }

                    ui.Text(company.Subtitle, new TextOpts
                    {
                        Size = 11,
                        NoWrap = true,
                        Color = dimmed ? Color.TextMuted : Color.TextSecondary,
                    });
                }

                using (ui.Column(new Box { Width = Size.Px(86), Gap = 1, Align = Align.End }))
                {
                    ui.Text(row.CapText, new TextOpts
                    {
                        Size = 13,
                        Font = Font.Medium,
                        Tabular = true,
                        Align = TextAlign.Right,
                        Color = dimmed ? Color.TextSecondary : Color.Foreground,
                    });

                    ui.Text(row.PctText, new TextOpts
                    {
                        Size = 11,
                        Tabular = true,
                        Align = TextAlign.Right,
                        Color = dimmed ? Color.TextMuted : Format.PercentColor(row.ChangePct),
                    });
                }

                return clicked;
            }
        }
    }
}

internal static class Format
{
    public static string Money(float usd) => usd >= 1e12f
        ? string.Create(CultureInfo.InvariantCulture, $"${usd / 1e12f:0.00}T")
        : string.Create(CultureInfo.InvariantCulture, $"${usd / 1e9f:0.##}B");

    public static string Percent(float pct) =>
        string.Create(CultureInfo.InvariantCulture, $"{(pct >= 0f ? "+" : "")}{pct:0.00}%");

    public static string Price(float price, string currency) => currency switch
    {
        "USD" => string.Create(CultureInfo.InvariantCulture, $"${price:0.00}"),
        "HKD" => string.Create(CultureInfo.InvariantCulture, $"HK${price:0.00}"),
        "CNY" => string.Create(CultureInfo.InvariantCulture, $"¥{price:0.00}"),
        "JPY" => string.Create(CultureInfo.InvariantCulture, $"¥{price:0}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{price:0.00}"),
    };

    // East Asian market convention: red is up, green is down.
    public static Color PercentColor(float pct) =>
        pct > 0.0001f ? Color.Danger
        : pct < -0.0001f ? Color.Success
        : Color.TextMuted;
}

internal static class Palette
{
    public static Color ColorOf(Company company) => company.AccentColor;

    public static Color Dim(Company company) => company.DimColor;
}

// ---------------------------------------------------------------------------
// 4. Domain model & C# 15 union types (`MarketView`, `ChartHorizon`).
// ---------------------------------------------------------------------------

/// <summary>Discriminated union of the three states the market UI can render.</summary>
internal union MarketView(MarketLoading, MarketError, MarketSnapshot);

internal sealed record MarketLoading
{
    public static readonly MarketLoading Instance = new();
}

internal sealed record MarketError(string Message, DateTimeOffset AtUtc);

/// <summary>Discriminated union of the two chart time horizons.</summary>
internal union ChartHorizon(SixMonthHorizon, IntradayHorizon);

internal sealed record SixMonthHorizon
{
    public static readonly SixMonthHorizon Instance = new();
    public string Title => "Market capitalization";
    public string Subtitle => "USD trillions · the last six months";
    public string Badge => "USD · trillions";
    public string ValueFormat => "$%.2fT";
}

internal sealed record IntradayHorizon
{
    public static readonly IntradayHorizon Instance = new();
    public string Title => "Session performance";
    public string Subtitle => "Percent from previous close · intraday";
    public string Badge => "% change";
    public string ValueFormat => "%.2f%%";
}

/// <summary>The id each feed knows one listing by.</summary>
internal readonly record struct SourceIds(string Eastmoney, string TradingView, string Yahoo);

internal sealed record Company(
    string Ticker,
    string Name,
    string Market,
    string Currency,
    int ColorIndex,
    SourceIds Ids)
{
    public string Subtitle { get; } = $"{Name} · {Market}";
    public string RowCardId { get; } = $"row:{Ticker}";
    public string MiniCardId { get; } = $"mini:{Ticker}";
    public Color AccentColor { get; } = Color.Oklch(0.73f, 0.155f, (22f + ColorIndex * 137.50776f) % 360f);
    public Color DimColor { get; } = Color.Mix(
        Color.Oklch(0.73f, 0.155f, (22f + ColorIndex * 137.50776f) % 360f),
        Color.Background,
        0.62f);
}

internal static class Catalog
{
    // Each listing carries the id every feed knows it by: Eastmoney `market.code`
    // (105 NASDAQ, 106 NYSE, 116 HKEX, 1 SSE, 0 SZSE, 176 Tokyo), TradingView
    // `EXCHANGE:TICKER`, and the Yahoo Finance symbol.
    public static readonly Company[] Companies =
    [
        new("NVDA", "NVIDIA", "NASDAQ", "USD", 0, new("105.NVDA", "NASDAQ:NVDA", "NVDA")),
        new("MSFT", "Microsoft", "NASDAQ", "USD", 1, new("105.MSFT", "NASDAQ:MSFT", "MSFT")),
        new("AAPL", "Apple", "NASDAQ", "USD", 2, new("105.AAPL", "NASDAQ:AAPL", "AAPL")),
        new("GOOGL", "Alphabet", "NASDAQ", "USD", 3, new("105.GOOGL", "NASDAQ:GOOGL", "GOOGL")),
        new("AMZN", "Amazon", "NASDAQ", "USD", 4, new("105.AMZN", "NASDAQ:AMZN", "AMZN")),
        new("META", "Meta Platforms", "NASDAQ", "USD", 5, new("105.META", "NASDAQ:META", "META")),
        new("AVGO", "Broadcom", "NASDAQ", "USD", 6, new("105.AVGO", "NASDAQ:AVGO", "AVGO")),
        new("TSLA", "Tesla", "NASDAQ", "USD", 7, new("105.TSLA", "NASDAQ:TSLA", "TSLA")),
        new("BRK.B", "Berkshire Hathaway", "NYSE", "USD", 8, new("106.BRK_B", "NYSE:BRK.B", "BRK-B")),
        new("LLY", "Eli Lilly", "NYSE", "USD", 9, new("106.LLY", "NYSE:LLY", "LLY")),
        new("JPM", "JPMorgan Chase", "NYSE", "USD", 10, new("106.JPM", "NYSE:JPM", "JPM")),
        new("V", "Visa", "NYSE", "USD", 11, new("106.V", "NYSE:V", "V")),
        new("MA", "Mastercard", "NYSE", "USD", 12, new("106.MA", "NYSE:MA", "MA")),
        new("WMT", "Walmart", "NASDAQ", "USD", 13, new("105.WMT", "NASDAQ:WMT", "WMT")),
        new("JNJ", "Johnson & Johnson", "NYSE", "USD", 14, new("106.JNJ", "NYSE:JNJ", "JNJ")),
        new("PG", "Procter & Gamble", "NYSE", "USD", 15, new("106.PG", "NYSE:PG", "PG")),
        new("XOM", "Exxon Mobil", "NYSE", "USD", 16, new("106.XOM", "NYSE:XOM", "XOM")),
        new("UNH", "UnitedHealth", "NYSE", "USD", 17, new("106.UNH", "NYSE:UNH", "UNH")),
        new("HD", "Home Depot", "NYSE", "USD", 18, new("106.HD", "NYSE:HD", "HD")),
        new("COST", "Costco", "NASDAQ", "USD", 19, new("105.COST", "NASDAQ:COST", "COST")),
        new("ORCL", "Oracle", "NYSE", "USD", 20, new("106.ORCL", "NYSE:ORCL", "ORCL")),
        new("NFLX", "Netflix", "NASDAQ", "USD", 21, new("105.NFLX", "NASDAQ:NFLX", "NFLX")),
        new("AMD", "AMD", "NASDAQ", "USD", 22, new("105.AMD", "NASDAQ:AMD", "AMD")),
        new("TSM", "TSMC", "NYSE", "USD", 23, new("106.TSM", "NYSE:TSM", "TSM")),

        new("0700", "Tencent", "HKEX", "HKD", 24, new("116.00700", "HKEX:700", "0700.HK")),
        new("9988", "Alibaba", "HKEX", "HKD", 25, new("116.09988", "HKEX:9988", "9988.HK")),
        new("3690", "Meituan", "HKEX", "HKD", 26, new("116.03690", "HKEX:3690", "3690.HK")),

        new("600519", "Kweichow Moutai", "SSE", "CNY", 27, new("1.600519", "SSE:600519", "600519.SS")),
        new("601398", "ICBC", "SSE", "CNY", 28, new("1.601398", "SSE:601398", "601398.SS")),
        new("300750", "CATL", "SZSE", "CNY", 29, new("0.300750", "SZSE:300750", "300750.SZ")),

        new("7203", "Toyota", "TSE", "JPY", 30, new("176.7203", "TSE:7203", "7203.T")),
        new("6758", "Sony", "TSE", "JPY", 31, new("176.6758", "TSE:6758", "6758.T")),
        new("9984", "SoftBank Group", "TSE", "JPY", 32, new("176.9984", "TSE:9984", "9984.T")),
    ];

    public static int Count => Companies.Length;

    public static readonly int ExchangeCount =
        Companies.Select(static c => c.Market).Distinct(StringComparer.Ordinal).Count();

    public static readonly Dictionary<string, Company> ByTicker =
        Companies.ToDictionary(static c => c.Ticker, StringComparer.Ordinal);

    public static readonly Dictionary<string, Company> BySecId =
        Companies.ToDictionary(static c => c.Ids.Eastmoney, StringComparer.Ordinal);

    public static readonly Dictionary<string, Company> ByScanSymbol =
        Companies.ToDictionary(static c => c.Ids.TradingView, StringComparer.Ordinal);

    public static Company GetOrFallback(string ticker) =>
        ByTicker.TryGetValue(ticker, out var company) ? company : Companies[0];
}

internal sealed record QuoteRow(
    string Ticker,
    float Price,
    float PrevClose,
    float ChangePct,
    float MarketCapUsd,
    float[] CapHistory,
    float[] IntradayPct)
{
    [JsonIgnore]
    public Company Company { get; } = Catalog.GetOrFallback(Ticker);

    [JsonIgnore]
    public string CapText { get; } = Format.Money(MarketCapUsd);

    [JsonIgnore]
    public string PctText { get; } = Format.Percent(ChangePct);

    [JsonIgnore]
    public string PriceText { get; } = Format.Price(Price, Catalog.GetOrFallback(Ticker).Currency);

    public float[] SeriesFor(ChartHorizon horizon) => horizon switch
    {
        SixMonthHorizon => CapHistory,
        IntradayHorizon => IntradayPct,
    };
}

internal readonly record struct FxSnapshot(
    float UsdHkd = 7.80f,
    float UsdJpy = 152.0f,
    float UsdCny = 7.20f)
{
    [JsonIgnore]
    public string Headline => string.Create(CultureInfo.InvariantCulture, $"USD/CNY {UsdCny:0.00}");

    [JsonIgnore]
    public string Detail => string.Create(CultureInfo.InvariantCulture, $"HKD {UsdHkd:0.000} · JPY {UsdJpy:0.0}");
}

internal sealed record MarketSnapshot(
    DateTimeOffset UpdatedUtc,
    QuoteRow[] Rows,
    string[] DateLabels,
    FxSnapshot Fx,
    [property: JsonIgnore] bool Cached = false)
{
    [JsonIgnore]
    public string FirstDateLabel => DateLabels is [var first, ..] ? first : "";

    [JsonIgnore]
    public string UpdatedLabel { get; } = Cached
        ? string.Create(CultureInfo.InvariantCulture, $"Cached · {UpdatedUtc.ToLocalTime():MMM d, HH:mm}")
        : string.Create(CultureInfo.InvariantCulture, $"Updated {UpdatedUtc.ToLocalTime():HH:mm:ss}");
}

internal readonly record struct SummaryStats(
    float TotalCapUsd,
    float WeightedPct,
    int Up,
    int Down,
    int Count,
    QuoteRow Leader)
{
    public int Unchanged => Count - Up - Down;

    public static SummaryStats Compute(ReadOnlySpan<QuoteRow> rows)
    {
        var total = 0f;
        var weighted = 0f;
        var up = 0;
        var down = 0;

        foreach (var row in rows)
        {
            total += row.MarketCapUsd;
            weighted += row.MarketCapUsd * row.ChangePct;

            if (row.ChangePct > 0.0001f) up++;
            else if (row.ChangePct < -0.0001f) down++;
        }

        return new SummaryStats(
            TotalCapUsd: total,
            WeightedPct: total > 0f ? weighted / total : 0f,
            Up: up,
            Down: down,
            Count: rows.Length,
            Leader: rows[0]);
    }
}

// ---------------------------------------------------------------------------
// 5. MarketService: drives background updates via CuiSharp's `Mutation<T>`, and picks
//    between `MarketSource` implementations, each doing its own quote + series calls.
// ---------------------------------------------------------------------------

internal sealed class MarketService : IDisposable
{
    public const float RefreshSeconds = 30f;

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(RefreshSeconds);

    private readonly MarketSource[] _sources;
    private readonly string[] _labels;
    private readonly Mutation<MarketView> _mutation;

    private MarketSource _source;
    private MarketView _current;
    private int _requestedCount = DashboardState.DefaultTopCount;
    private int _fetchedCount;
    private long _lastRefreshTick;
    private bool _started;

    public MarketService()
    {
        var saved = Cache.Load();

        _sources = [new EastmoneySource(), new YahooSource()];
        _labels = [.. _sources.Select(static source => source.Label)];
        _source = Array.Find(_sources, source => source.Id == saved?.Source) ?? _sources[0];

        _current = saved?.Snapshot is { } snapshot ? snapshot : MarketLoading.Instance;

        _mutation = new Mutation<MarketView>(FetchAsync, apply: view => _current = view);
    }

    public MarketView Current => _current;
    public bool IsBusy => _mutation.IsRunning;
    public string[] SourceLabels => _labels;
    public int SelectedSource => Array.IndexOf(_sources, _source);
    public string Attribution => _source.Attribution;

    public async Task WarmUpAsync()
    {
        _current = await FetchAsync(CancellationToken.None).ConfigureAwait(false);
        _fetchedCount = _requestedCount;
        _lastRefreshTick = Stopwatch.GetTimestamp();
        _started = true;
    }

    public void EnsureFresh(int topCount)
    {
        _requestedCount = Math.Clamp(topCount, MiniView.DefaultRows, Catalog.Count);

        var needsMoreRows = _requestedCount > _fetchedCount;
        var timerExpired = _started && Stopwatch.GetElapsedTime(_lastRefreshTick) >= RefreshInterval;

        if (!_started || needsMoreRows || timerExpired)
            TriggerRefresh();
    }

    public void RequestRefresh() => TriggerRefresh();

    /// <summary>Switches feeds, remembers the choice, and refetches straight away.</summary>
    public void SelectSource(int index)
    {
        if ((uint)index >= (uint)_sources.Length) return;

        var next = _sources[index];
        if (ReferenceEquals(next, _source)) return;

        _source = next;
        _current = MarketLoading.Instance;
        _fetchedCount = 0;

        // Persist the choice before the network runs: a feed that cannot be reached
        // still has to be the one selected after a restart.
        Cache.Save(_source.Id, null);
        TriggerRefresh();
    }

    public void Dispose()
    {
        _mutation.Dispose();

        foreach (var source in _sources)
            source.Dispose();
    }

    private void TriggerRefresh()
    {
        _started = true;
        _fetchedCount = Math.Max(_fetchedCount, _requestedCount);
        _lastRefreshTick = Stopwatch.GetTimestamp();
        _mutation.Restart();
    }

    private async Task<MarketView> FetchAsync(CancellationToken ct)
    {
        var source = _source;
        var previous = _current is MarketSnapshot existing ? existing : null;
        var count = Math.Clamp(_requestedCount, MiniView.DefaultRows, Catalog.Count);

        try
        {
            var snapshot = await source.FetchSnapshotAsync(count, previous, ct).ConfigureAwait(false);
            if (snapshot is not null)
            {
                Cache.Save(source.Id, snapshot);
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                return snapshot;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Write($"refresh error: {ex.Message}");
        }

        return previous is not null
            ? previous
            : new MarketError("Could not reach the market feeds. Retrying every 30 seconds.", DateTimeOffset.UtcNow);
    }
}

internal readonly record struct DailyClose(DateOnly Date, float Close);

internal static class FxIds
{
    public const string UsdHkd = "119.USDHKD";
    public const string UsdJpy = "119.USDJPY";
    public const string UsdCny = "133.USDCNH";
}

/// <summary>One market feed: quotes, daily history and intraday series for the same catalog.</summary>
internal abstract class MarketSource : IDisposable
{
    protected const int IntradayGridPoints = 120;
    protected static readonly TimeSpan HistoryCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<string, DailyClose[]> _dailyCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, float[]> _intradayCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, float> _lastPrice = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate;
    private readonly int _maxAttempts;
    private readonly TimeSpan _retryDelay;
    private DateTimeOffset _dailyFetchedAt = DateTimeOffset.MinValue;

    protected MarketSource(int concurrency, int maxAttempts, TimeSpan retryDelay)
    {
        _gate = new SemaphoreSlim(concurrency, concurrency);
        _maxAttempts = maxAttempts;
        _retryDelay = retryDelay;
        Http = CreateHttpClient(concurrency);
    }

    public abstract string Id { get; }
    public abstract string Label { get; }
    public abstract string Attribution { get; }

    protected HttpClient Http { get; }

    public void Dispose()
    {
        Http.Dispose();
        _gate.Dispose();
    }

    public async Task<MarketSnapshot?> FetchSnapshotAsync(int topCount, MarketSnapshot? previous, CancellationToken ct)
    {
        var feed = await FetchQuotesAsync(previous?.Fx ?? new FxSnapshot(), ct).ConfigureAwait(false);
        if (feed is not { } quotes || quotes.Quotes.Count == 0) return null;

        var targetCompanies = quotes.Quotes
            .Take(Math.Min(topCount, quotes.Quotes.Count))
            .Select(static q => q.Company)
            .ToArray();

        var now = DateTimeOffset.UtcNow;
        var stale = now - _dailyFetchedAt > HistoryCacheTtl;

        if (stale || targetCompanies.Any(c => !_dailyCache.ContainsKey(c.Ticker)))
        {
            // Refetch the whole window only when it went stale; otherwise ask for just the
            // symbols that came back empty, so one bad request does not replay 33 others.
            var batch = stale
                ? targetCompanies
                : [.. targetCompanies.Where(c => !_dailyCache.ContainsKey(c.Ticker))];

            await FetchDailyBatchAsync(batch, ct).ConfigureAwait(false);

            // Only start the TTL once every symbol has history; a partial network failure
            // must be retried on the next 30 s cycle instead of leaving the chart empty.
            if (targetCompanies.All(c => _dailyCache.ContainsKey(c.Ticker)))
                _dailyFetchedAt = now;
        }

        await FetchIntradayBatchAsync(quotes.Quotes, targetCompanies, ct).ConfigureAwait(false);

        return BuildSnapshot(quotes.Quotes, quotes.Fx, now, previous);
    }

    protected abstract Task<QuoteFeed?> FetchQuotesAsync(FxSnapshot fallback, CancellationToken ct);

    protected abstract Task FetchDailyBatchAsync(Company[] companies, CancellationToken ct);

    protected abstract Task FetchIntradayBatchAsync(List<RawQuote> quotes, Company[] companies, CancellationToken ct);

    /// <summary>A closed exchange repeats the same price every cycle, so a series only has to
    /// be refetched once it is missing or its quote moved.</summary>
    protected bool NeedsIntraday(Company company, RawQuote quote) =>
        !_intradayCache.ContainsKey(company.Ticker)
        || !_lastPrice.TryGetValue(company.Ticker, out var last)
        || last != quote.Price;

    protected void StoreDaily(Company company, DailyClose[] points) => _dailyCache[company.Ticker] = points;

    protected void StoreIntraday(Company company, float price, float[] series)
    {
        _intradayCache[company.Ticker] = series;
        _lastPrice[company.Ticker] = price;
    }

    protected Task<T?> GetJsonAsync<T>(string url, JsonTypeInfo<T> type, CancellationToken ct)
        where T : class =>
        GetJsonAsync([url], type, ct);

    /// <summary>Rotates through the URLs one per attempt, so a host that is dropping connections
    /// does not consume the whole retry budget.</summary>
    protected Task<T?> GetJsonAsync<T>(string[] urls, JsonTypeInfo<T> type, CancellationToken ct)
        where T : class =>
        SendAsync(urls, (url, token) => Http.GetFromJsonAsync(url, type, token), ct);

    protected Task<TResponse?> PostJsonAsync<TRequest, TResponse>(
        string url,
        TRequest body,
        JsonTypeInfo<TRequest> requestType,
        JsonTypeInfo<TResponse> responseType,
        CancellationToken ct)
        where TResponse : class =>
        SendAsync([url], async (target, token) =>
        {
            using var response = await Http.PostAsJsonAsync(target, body, requestType, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            return await response.Content.ReadFromJsonAsync(responseType, token).ConfigureAwait(false);
        }, ct);

    private async Task<T?> SendAsync<T>(
        string[] urls,
        Func<string, CancellationToken, Task<T?>> send,
        CancellationToken ct)
        where T : class
    {
        // Feeds drop connections when a burst arrives at once, so requests stay few and far
        // apart and a throttled one is retried with backoff instead of being lost.
        for (var attempt = 0; attempt < _maxAttempts; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(_retryDelay * attempt, ct).ConfigureAwait(false);

            var url = urls[attempt % urls.Length];

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(RequestTimeout);

                return await send(url, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or IOException)
            {
                Log.Write($"{Id}: {url} failed (attempt {attempt + 1}/{_maxAttempts}): {ex.GetBaseException().Message}");
            }
            finally
            {
                _gate.Release();
            }
        }

        return null;
    }

    private MarketSnapshot BuildSnapshot(
        List<RawQuote> quotes,
        FxSnapshot fx,
        DateTimeOffset nowUtc,
        MarketSnapshot? previous)
    {
        var dates = new SortedSet<DateOnly>();
        foreach (var quote in quotes)
        {
            if (_dailyCache.TryGetValue(quote.Company.Ticker, out var closes))
                foreach (var point in closes) dates.Add(point.Date);
        }

        var sortedDates = dates.ToArray();
        var dateLabels = new string[sortedDates.Length];
        for (var i = 0; i < sortedDates.Length; i++)
            dateLabels[i] = sortedDates[i].ToString("MMM d", CultureInfo.InvariantCulture);

        // A cycle that brought no history at all leaves the axis unchanged, so the series
        // already on screen are still valid; keep them instead of blanking every sparkline.
        var carried = previous is { DateLabels.Length: > 0 } earlier
            && (sortedDates.Length == 0 || earlier.DateLabels.AsSpan().SequenceEqual(dateLabels))
                ? earlier
                : null;

        if (carried is not null && sortedDates.Length == 0)
            dateLabels = carried.DateLabels;

        var previousRows = previous?.Rows.ToDictionary(static r => r.Ticker, StringComparer.Ordinal);

        var rows = new QuoteRow[quotes.Count];
        for (var i = 0; i < quotes.Count; i++)
        {
            var quote = quotes[i];
            var capHistory = BuildCapSeries(quote, sortedDates);
            var intradayPct = _intradayCache.GetValueOrDefault(quote.Company.Ticker) ?? [];

            if (previousRows is not null && previousRows.TryGetValue(quote.Company.Ticker, out var old))
            {
                if (carried is not null
                    && capHistory.Length != dateLabels.Length
                    && old.CapHistory.Length == dateLabels.Length)
                {
                    capHistory = old.CapHistory;
                }

                if (intradayPct.Length < 2 && old.IntradayPct.Length >= 2)
                    intradayPct = old.IntradayPct;
            }

            rows[i] = new QuoteRow(
                Ticker: quote.Company.Ticker,
                Price: quote.Price,
                PrevClose: quote.PrevClose,
                ChangePct: quote.ChangePct,
                MarketCapUsd: quote.MarketCapUsd,
                CapHistory: capHistory,
                IntradayPct: intradayPct);
        }

        return new MarketSnapshot(
            UpdatedUtc: nowUtc,
            Rows: rows,
            DateLabels: dateLabels,
            Fx: fx,
            Cached: false);
    }

    private float[] BuildCapSeries(RawQuote quote, ReadOnlySpan<DateOnly> dates)
    {
        if (dates.IsEmpty || !_dailyCache.TryGetValue(quote.Company.Ticker, out var points) || points.Length < 2)
            return [];

        var lastClose = points[0].Close;
        var capPerUnitPriceTrillions = quote.MarketCapUsd / quote.Price / 1e12f;
        var values = new float[dates.Length];
        var p = 0;

        for (var i = 0; i < dates.Length; i++)
        {
            var target = dates[i];
            while (p + 1 < points.Length && points[p + 1].Date <= target)
                p++;

            if (points[p].Date <= target)
                lastClose = points[p].Close;

            values[i] = lastClose * capPerUnitPriceTrillions;
        }

        values[^1] = quote.MarketCapUsd / 1e12f;
        return values;
    }

    private static HttpClient CreateHttpClient(int concurrency)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = concurrency,
        };

        var http = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0 Safari/537.36");

        return http;
    }
}

/// <summary>Eastmoney: one batch quote call, then a k-line and a trend call per symbol.</summary>
internal sealed class EastmoneySource() : MarketSource(2, 3, TimeSpan.FromMilliseconds(300))
{
    private const string QuoteUrl = "https://push2.eastmoney.com/api/qt/ulist.np/get";
    private const string KlineUrl = "https://push2his.eastmoney.com/api/qt/stock/kline/get";
    private const string TrendsUrl = "https://push2his.eastmoney.com/api/qt/stock/trends2/get";

    private static readonly string QuoteQuery =
        "fltt=2&invt=2&fields=f2,f3,f4,f12,f13,f14,f20&secids="
        + string.Join(',', Catalog.Companies.Select(static c => c.Ids.Eastmoney).Concat([FxIds.UsdHkd, FxIds.UsdJpy, FxIds.UsdCny]));

    public override string Id => "eastmoney";
    public override string Label => "Eastmoney";
    public override string Attribution => "Quotes, market caps and series from Eastmoney · may be delayed";

    protected override async Task<QuoteFeed?> FetchQuotesAsync(FxSnapshot fallback, CancellationToken ct)
    {
        var response = await GetJsonAsync($"{QuoteUrl}?{QuoteQuery}", MarketJsonContext.Default.EmQuoteResponse, ct)
            .ConfigureAwait(false);
        if (response?.Data?.Diff is not { Length: > 0 } items) return null;

        var fx = ParseFx(items, fallback);
        var quotes = ParseQuotes(items, fx);
        return quotes.Count == 0 ? null : new QuoteFeed(quotes, fx);
    }

    protected override Task FetchDailyBatchAsync(Company[] companies, CancellationToken ct)
    {
        var beg = DateTimeOffset.UtcNow.AddDays(-200).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return Parallel.ForEachAsync(companies, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 1 },
            async (company, ct) => await FetchDailyAsync(company, beg, ct));
    }

    private async Task FetchDailyAsync(Company company, string beg, CancellationToken ct)
    {
        var url = $"{KlineUrl}?secid={company.Ids.Eastmoney}&klt=101&fqt=1&beg={beg}&end=20500101"
            + "&fields1=f1,f2,f3,f4,f5,f6&fields2=f51,f53";

        var response = await GetJsonAsync(url, MarketJsonContext.Default.EmKlineResponse, ct).ConfigureAwait(false);
        if (ParseDailyCloses(response?.Data?.Klines) is { Length: >= 2 } points)
            StoreDaily(company, points);
    }

    protected override Task FetchIntradayBatchAsync(List<RawQuote> quotes, Company[] companies, CancellationToken ct)
    {
        var byTicker = quotes.ToDictionary(static q => q.Company.Ticker, StringComparer.Ordinal);
        return Parallel.ForEachAsync(companies, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 1 },
            async (company, ct) =>
            {
                if (!byTicker.TryGetValue(company.Ticker, out var quote)) return;
                if (!NeedsIntraday(company, quote)) return;
                await FetchIntradayAsync(company, quote, ct);
            });
    }

    private async Task FetchIntradayAsync(Company company, RawQuote quote, CancellationToken ct)
    {
        var url = $"{TrendsUrl}?secid={company.Ids.Eastmoney}&ndays=1&iscr=0"
            + "&fields1=f1,f2,f3,f4,f5,f6,f7,f8&fields2=f51,f53";

        var response = await GetJsonAsync(url, MarketJsonContext.Default.EmTrendsResponse, ct).ConfigureAwait(false);
        if (response?.Data is not { } data) return;

        var baseline = TryNumber(data.PreClose, out var pre) && pre > 0 ? (float)pre : quote.PrevClose;
        if (ResampleIntradayPct(data.Trends, baseline) is { Length: >= 2 } pctSeries)
            StoreIntraday(company, quote.Price, pctSeries);
    }

    private static FxSnapshot ParseFx(EmQuoteRow[] items, FxSnapshot fallback)
    {
        var hkd = fallback.UsdHkd;
        var jpy = fallback.UsdJpy;
        var cny = fallback.UsdCny;

        foreach (var item in items)
        {
            if (!TryNumber(item.Price, out var rate) || rate <= 0) continue;

            switch (item.SecId)
            {
                case FxIds.UsdHkd: hkd = (float)rate; break;
                case FxIds.UsdJpy: jpy = (float)rate; break;
                case FxIds.UsdCny: cny = (float)rate; break;
            }
        }

        return new FxSnapshot(hkd, jpy, cny);
    }

    private static List<RawQuote> ParseQuotes(EmQuoteRow[] items, FxSnapshot fx)
    {
        var quotes = new List<RawQuote>(Catalog.Count);

        foreach (var item in items)
        {
            if (!Catalog.BySecId.TryGetValue(item.SecId, out var company)) continue;
            if (!TryNumber(item.Price, out var price) || price <= 0) continue;
            if (!TryNumber(item.MarketCap, out var cap) || cap <= 0) continue;

            var priceF = (float)price;
            var pct = TryNumber(item.ChangePct, out var changePct) ? (float)changePct : 0f;
            var changeAbs = TryNumber(item.ChangeAbs, out var diff) ? (float)diff : 0f;
            var prev = priceF > changeAbs ? priceF - changeAbs : priceF;
            var capUsd = (float)(cap / FxToUsd(company.Currency, fx));

            quotes.Add(new RawQuote(company, priceF, prev, pct, capUsd));
        }

        quotes.Sort(static (a, b) => b.MarketCapUsd.CompareTo(a.MarketCapUsd));
        return quotes;
    }

    private static double FxToUsd(string currency, FxSnapshot fx) => currency switch
    {
        "HKD" => fx.UsdHkd,
        "CNY" => fx.UsdCny,
        "JPY" => fx.UsdJpy,
        _ => 1d,
    };

    private static bool TryNumber(JsonElement element, out double value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value)) return true;
        value = 0;
        return false;
    }

    private static DailyClose[] ParseDailyCloses(string[]? lines)
    {
        if (lines is not { Length: > 0 }) return [];

        var points = new List<DailyClose>(lines.Length);
        foreach (var line in lines)
        {
            var comma = line.IndexOf(',');
            if (comma <= 0) continue;
            if (!DateOnly.TryParseExact(line.AsSpan(0, comma), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date)) continue;
            if (!float.TryParse(line.AsSpan(comma + 1), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var close)) continue;
            if (close <= 0f) continue;

            points.Add(new DailyClose(date, close));
        }

        return [.. points];
    }

    private static float[] ResampleIntradayPct(string[]? trends, float fallbackPrevClose)
    {
        if (trends is not { Length: >= 2 } lines) return [];

        var valid = new List<float>(lines.Length);
        foreach (var line in lines)
        {
            var comma = line.LastIndexOf(',');
            if (comma <= 0) continue;
            if (float.TryParse(line.AsSpan(comma + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var price)
                && price > 0f) valid.Add(price);
        }

        if (valid.Count < 2) return [];

        var baseline = fallbackPrevClose > 0f ? fallbackPrevClose : valid[0];

        var resampled = new float[IntradayGridPoints];
        for (var i = 0; i < IntradayGridPoints; i++)
        {
            var sourceIndex = (int)MathF.Round(i / (float)(IntradayGridPoints - 1) * (valid.Count - 1));
            resampled[i] = (valid[sourceIndex] / baseline - 1f) * 100f;
        }

        return resampled;
    }
}

/// <summary>TradingView quotes plus Yahoo Finance spark series, the original feed pair.</summary>
internal sealed class YahooSource() : MarketSource(4, 2, TimeSpan.FromMilliseconds(200))
{
    private const string ScanUrl = "https://scanner.tradingview.com/global/scan";
    private const string SparkBaseUrl = "https://query1.finance.yahoo.com/v8/finance/spark";
    private const int SparkBatchSize = 20;

    private static readonly TvScanRequest ScanPayload = new(
        new TvSymbols([
            .. Catalog.Companies.Select(static c => c.Ids.TradingView),
            "FX_IDC:USDHKD",
            "FX_IDC:USDJPY",
            "FX_IDC:USDCNY",
        ]),
        ["close", "change", "change_abs", "market_cap_basic"]);

    public override string Id => "yahoo";
    public override string Label => "Yahoo";
    public override string Attribution => "Quotes from TradingView · series from Yahoo Finance · may be delayed";

    protected override async Task<QuoteFeed?> FetchQuotesAsync(FxSnapshot fallback, CancellationToken ct)
    {
        var scan = await PostJsonAsync(
            ScanUrl,
            ScanPayload,
            MarketJsonContext.Default.TvScanRequest,
            MarketJsonContext.Default.TvScanResponse,
            ct).ConfigureAwait(false);
        if (scan?.Data is not { Length: > 0 } items) return null;

        var quotes = ParseQuotes(items);
        return quotes.Count == 0 ? null : new QuoteFeed(quotes, ParseFx(items, fallback));
    }

    protected override Task FetchDailyBatchAsync(Company[] companies, CancellationToken ct)
    {
        return Parallel.ForEachAsync(companies.Chunk(SparkBatchSize), new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 1 },
            async (chunk, ct) =>
            {
                var symbols = string.Join(',', chunk.Select(static c => c.Ids.Yahoo));
                await FetchDailyBatchAsync(
                    chunk,
                    $"{SparkBaseUrl}?symbols={symbols}&range=6mo&interval=1d",
                    ct);
            });
    }

    private async Task FetchDailyBatchAsync(Company[] companies, string url, CancellationToken ct)
    {
        var batch = await GetJsonAsync(url, MarketJsonContext.Default.DictionaryStringYahooSparkSeries, ct)
            .ConfigureAwait(false);
        if (batch is null) return;

        foreach (var company in companies)
        {
            if (!batch.TryGetValue(company.Ids.Yahoo, out var spark)) continue;
            if (ParseDailyCloses(spark) is { Length: >= 2 } points)
                StoreDaily(company, points);
        }
    }

    protected override Task FetchIntradayBatchAsync(List<RawQuote> quotes, Company[] companies, CancellationToken ct)
    {
        var byTicker = quotes.ToDictionary(static q => q.Company.Ticker, StringComparer.Ordinal);
        var pending = new List<Task>();

        foreach (var chunk in companies.Chunk(SparkBatchSize))
        {
            var wanted = chunk
                .Where(company => byTicker.TryGetValue(company.Ticker, out var quote) && NeedsIntraday(company, quote))
                .ToArray();
            if (wanted.Length == 0) continue;

            var symbols = string.Join(',', wanted.Select(static c => c.Ids.Yahoo));
            pending.Add(FetchIntradayBatchAsync(
                wanted,
                byTicker,
                $"{SparkBaseUrl}?symbols={symbols}&range=1d&interval=5m",
                ct));
        }

        return Task.WhenAll(pending);
    }

    private async Task FetchIntradayBatchAsync(
        Company[] companies,
        Dictionary<string, RawQuote> byTicker,
        string url,
        CancellationToken ct)
    {
        var batch = await GetJsonAsync(url, MarketJsonContext.Default.DictionaryStringYahooSparkSeries, ct)
            .ConfigureAwait(false);
        if (batch is null) return;

        foreach (var company in companies)
        {
            if (!batch.TryGetValue(company.Ids.Yahoo, out var spark)) continue;

            var quote = byTicker[company.Ticker];
            if (ResampleIntradayPct(spark, quote.PrevClose) is { Length: >= 2 } pctSeries)
                StoreIntraday(company, quote.Price, pctSeries);
        }
    }

    private static FxSnapshot ParseFx(TvRow[] items, FxSnapshot fallback)
    {
        var hkd = fallback.UsdHkd;
        var jpy = fallback.UsdJpy;
        var cny = fallback.UsdCny;

        foreach (var item in items)
        {
            if (item.Values is not [{ } rate, ..] || rate <= 0f) continue;

            switch (item.Symbol)
            {
                case "FX_IDC:USDHKD": hkd = rate; break;
                case "FX_IDC:USDJPY": jpy = rate; break;
                case "FX_IDC:USDCNY": cny = rate; break;
            }
        }

        return new FxSnapshot(hkd, jpy, cny);
    }

    private static List<RawQuote> ParseQuotes(TvRow[] items)
    {
        var quotes = new List<RawQuote>(Catalog.Count);

        foreach (var item in items)
        {
            if (!Catalog.ByScanSymbol.TryGetValue(item.Symbol, out var company)) continue;
            if (item.Values is not [{ } price, var pctOrNull, var changeAbsOrNull, { } capUsd, ..]) continue;
            if (price <= 0f || capUsd <= 0f) continue;

            var pct = pctOrNull ?? 0f;
            var prev = changeAbsOrNull is { } diff && price > diff ? price - diff : price;

            quotes.Add(new RawQuote(company, price, prev, pct, capUsd));
        }

        quotes.Sort(static (a, b) => b.MarketCapUsd.CompareTo(a.MarketCapUsd));
        return quotes;
    }

    private static DailyClose[] ParseDailyCloses(YahooSparkSeries spark)
    {
        if (spark.Timestamps is not { Length: > 0 } timestamps
            || spark.Closes is not { Length: > 0 } closes) return [];

        var length = Math.Min(timestamps.Length, closes.Length);
        var points = new List<DailyClose>(length);

        for (var i = 0; i < length; i++)
        {
            if (closes[i] is not { } close || close <= 0f) continue;
            var date = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(timestamps[i]).UtcDateTime);
            points.Add(new DailyClose(date, close));
        }

        return [.. points];
    }

    private static float[] ResampleIntradayPct(YahooSparkSeries spark, float fallbackPrevClose)
    {
        if (spark.Closes is not { Length: >= 2 } rawCloses) return [];

        var valid = new List<float>(rawCloses.Length);
        foreach (var price in rawCloses)
            if (price is > 0f) valid.Add(price.Value);

        if (valid.Count < 2) return [];

        var baseline = spark.PreviousClose is > 0f
            ? spark.PreviousClose.Value
            : fallbackPrevClose > 0f ? fallbackPrevClose : valid[0];

        var resampled = new float[IntradayGridPoints];
        for (var i = 0; i < IntradayGridPoints; i++)
        {
            var sourceIndex = (int)MathF.Round(i / (float)(IntradayGridPoints - 1) * (valid.Count - 1));
            resampled[i] = (valid[sourceIndex] / baseline - 1f) * 100f;
        }

        return resampled;
    }
}

internal readonly record struct QuoteFeed(List<RawQuote> Quotes, FxSnapshot Fx);

internal readonly record struct RawQuote(
    Company Company,
    float Price,
    float PrevClose,
    float ChangePct,
    float MarketCapUsd);

// ---------------------------------------------------------------------------
// 6. Strongly-typed wire DTOs, AOT JsonSerializerContext, and disk cache.
// ---------------------------------------------------------------------------

internal sealed record EmQuoteResponse(
    [property: JsonPropertyName("data")] EmQuoteData? Data);

internal sealed record EmQuoteData(
    [property: JsonPropertyName("diff")] EmQuoteRow[]? Diff);

// Numeric fields arrive as JSON numbers, or as "-" when the exchange has no value,
// so they stay JsonElement until TryNumber inspects them.
internal sealed record EmQuoteRow(
    [property: JsonPropertyName("f2")] JsonElement Price,
    [property: JsonPropertyName("f3")] JsonElement ChangePct,
    [property: JsonPropertyName("f4")] JsonElement ChangeAbs,
    [property: JsonPropertyName("f12")] string? Code,
    [property: JsonPropertyName("f13")] int? Market,
    [property: JsonPropertyName("f20")] JsonElement MarketCap)
{
    [JsonIgnore]
    public string SecId => $"{Market}.{Code}";
}

internal sealed record EmKlineResponse(
    [property: JsonPropertyName("data")] EmKlineData? Data);

internal sealed record EmKlineData(
    [property: JsonPropertyName("klines")] string[]? Klines);

internal sealed record EmTrendsResponse(
    [property: JsonPropertyName("data")] EmTrendsData? Data);

internal sealed record EmTrendsData(
    [property: JsonPropertyName("preClose")] JsonElement PreClose,
    [property: JsonPropertyName("trends")] string[]? Trends);

internal sealed record TvScanRequest(
    [property: JsonPropertyName("symbols")] TvSymbols Symbols,
    [property: JsonPropertyName("columns")] string[] Columns);

internal sealed record TvSymbols(
    [property: JsonPropertyName("tickers")] string[] Tickers);

internal sealed record TvScanResponse(
    [property: JsonPropertyName("data")] TvRow[]? Data);

internal sealed record TvRow(
    [property: JsonPropertyName("s")] string Symbol,
    [property: JsonPropertyName("d")] float?[]? Values);

internal sealed record YahooSparkSeries(
    [property: JsonPropertyName("timestamp")] long[]? Timestamps,
    [property: JsonPropertyName("close")] float?[]? Closes,
    [property: JsonPropertyName("chartPreviousClose")] float? PreviousClose);

/// <summary>What the disk file holds: the chosen feed, and the snapshot it last produced.</summary>
internal sealed record MarketState(
    int Version,
    string Source,
    MarketSnapshot? Snapshot);

[JsonSerializable(typeof(EmQuoteResponse))]
[JsonSerializable(typeof(EmKlineResponse))]
[JsonSerializable(typeof(EmTrendsResponse))]
[JsonSerializable(typeof(TvScanRequest))]
[JsonSerializable(typeof(TvScanResponse))]
[JsonSerializable(typeof(Dictionary<string, YahooSparkSeries>))]
[JsonSerializable(typeof(MarketState))]
[JsonSerializable(typeof(MarketSnapshot))]
internal partial class MarketJsonContext : JsonSerializerContext;

internal static class Cache
{
    public const int CurrentVersion = 4;

    private static readonly string FilePath = Path.Combine(StateRoot(), "GlobalMarketCap", "snapshot.json");

    private static string StateRoot() =>
        Environment.GetEnvironmentVariable("CUISHARP_STATE_HOME") is { Length: > 0 } root
            ? root
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static MarketState? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;

            using var stream = File.OpenRead(FilePath);
            var state = JsonSerializer.Deserialize(stream, MarketJsonContext.Default.MarketState);

            if (state is not { Version: CurrentVersion }) return null;
            if (state.Snapshot is not { Rows.Length: > 0 } snapshot) return state with { Snapshot = null };

            return state with { Snapshot = snapshot with { Cached = true } };
        }
        catch (Exception ex)
        {
            Log.Write($"cache load: {ex.Message}");
            return null;
        }
    }

    public static void Save(string source, MarketSnapshot? snapshot)
    {
        if (snapshot is { Rows.Length: 0 }) return;

        try
        {
            if (Path.GetDirectoryName(FilePath) is { } dir)
                Directory.CreateDirectory(dir);

            var state = new MarketState(CurrentVersion, source, snapshot);
            var tempPath = FilePath + ".tmp";
            using (var stream = File.Create(tempPath))
            {
                JsonSerializer.Serialize(stream, state, MarketJsonContext.Default.MarketState);
            }

            File.Move(tempPath, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write($"cache save: {ex.Message}");
        }
    }
}

internal static class Log
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("TRACK_DEBUG") is "1";

    public static void Write(string message)
    {
        if (Enabled) Console.Error.WriteLine($"[track] {message}");
    }
}
