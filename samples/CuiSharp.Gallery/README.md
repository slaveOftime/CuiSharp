# CuiSharp.Gallery

Every component the binding wraps, in twenty-eight pages, one file per page. It is the integration test
and the size/performance canary at the same time: it uses no hosting, no DI and no test project, so
it measures the library a person gets.

![Overview](../../docs/images/gallery/01-overview.png)

```sh
dotnet run                              # JIT, loads the CUI shared library
dotnet watch                            # the same, with hot reload on the page files
dotnet run -- --page=8                  # open one page directly
dotnet publish -c Release -r win-x64     # one self-contained executable
```

Environment and arguments:

| Setting | Effect |
|---|---|
| `--page=N` | start on page N (0-based, in sidebar order) |
| `CUI_THEME=dark` | start in dark mode (Ctrl+J toggles at runtime) |
| `CUI_SCREENSHOT=shot.png` | CUI draws N frames, writes a PNG and exits |
| `CUI_SCREENSHOT_FRAMES=8` | how many frames before that PNG |

## Driving it without a window

Any CuiSharp app serves the headless protocol when it is asked to, so the gallery needs no debug
mode — `dotnet cui` builds it, runs it and talks to it:

```sh
dotnet cui tree                     # every control the last frame drew, nested
dotnet cui click NavItem "Charts"   # navigate, and print the page the click opened
dotnet cui frame 6                  # let an animation settle
dotnet cui png docs/images/gallery/09-charts.png
```

The screenshots in `../docs/images/gallery` come from exactly that; `pwsh tools/snapshots.ps1`
regenerates all of them (and `-Theme dark` the dark set).

`pwsh tools/page-costs.ps1` uses the same driving to walk every page and measure what the app
costs while it shows each one — the page-by-page table in `docs/PERFORMANCE.md` is its output.

## Files

| File | Contents |
|---|---|
| `Program.cs` | the host: `CuiApp.Run` plus the `--page` argument |
| `GalleryApp.cs` | state, page list, static data |
| `GalleryApp.Shell.cs` | frame loop, sidebar, navigation, command palette, dialogs |
| `Pages/*.cs` | one page each |
| `Pages/CustomControls.cs` | hand-written controls as `extension(Ui ui)` members |

## What is ported

All nine pages of CUI's `examples/gallery_ui.c` — Overview, Buttons, Inputs, Selection, Navigation,
Overlays, Feedback, Data display, Charts — plus Studio (gauge, knobs, faders, meters), Tools
(terminal, log view, hex view, diff), and CUI 1.3's two developer-tools pages, **Developer tools**
(trace, waterfall, dependency graph, commit graph, pipeline, problems, key = value pairs, filter
builder, cron schedule, keymap) and **More developer tools** (tests, stack trace, blame, coverage,
regex tester, HTTP client, API reference, feature flags, uptime, event histogram).

All fifteen pages of `examples/gallery_more.c` are ported too: **Actions** (split buttons, toggle
groups, reactions, swipe, speed dial), **Smart inputs** (inline edit, scrub, money, affixes,
shortcut recorder, emoji), **Pickers** (multi-select, date range, time, wheel, dial, ruler),
**Capture** (dropzones, avatar, signature), **Disclosure** (banner, hover card, expandable,
countdown, checklist, undo toast), **Reader** (scroll-spy ToC), **Data grid** (filter bar,
2 000-row virtualized grid, tree table), **Code** (highlighted blocks, JSON viewer), **Social and
media** (chat, comments, presence, carousel, image viewer/cropper/compare, card stack),
**Planning** (kanban, week calendar, gantt, month calendar), **More charts** (heatmap, treemap,
radar, funnel, waffle, sankey, activity rings, candlestick…), **Motion** (shimmer, typewriter,
spotlight, dock, now playing, waveform), **AI** (composer, reasoning, tool calls, sources, plan,
voice orb), **Audio** (knobs, faders, meters, tour) and **Files** (browser, dialogs, terminal).

Some nav items sit below the sidebar's fold; `dotnet cui wheel 120 400 -8` scrolls it before the
click, which is also how `tools/snapshots.ps1` reaches them.

## A page

A page is a method that draws into the `Ui` it is given. State lives in `GalleryApp` fields, and
nothing subscribes to anything:

```csharp
private void PageCharts(Ui ui)
{
    Section(ui, "Line chart", "Hover to inspect values.");
    using (ui.Card(new Card()))
        ui.LineChart("revenue", _lineSeries, new ChartOpts { Height = 240 });
}
```
