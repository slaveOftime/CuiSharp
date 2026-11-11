# Performance

Every number here is measured on one Windows machine: the frame and scale tables by
`src/CuiSharp.Benchmarks`, the page-by-page table by `tools/page-costs.ps1` driving the real
gallery through `dotnet cui`. Trust the shape of the curves; treat absolute values as indicative.

```sh
dotnet run -c Release --project src/CuiSharp.Benchmarks     # BenchmarkDotNet suites
dotnet run -c Release --project src/CuiSharp.Benchmarks -- --scale    # 1k..1M points, one shot
dotnet run -c Release --project src/CuiSharp.Benchmarks -- --fps 900  # live-chart frame times
pwsh tools/page-costs.ps1                                   # every gallery page, 3 s each
```

BenchmarkDotNet output lands in `BenchmarkDotNet.Artifacts/results/`. All benchmark runs are
manual: Actions → CI → Run workflow → select **benchmarks** for every BenchmarkDotNet suite, or
**budget** for the frame-time measurement. Pushes and pull requests run neither. From the CLI:

```sh
gh workflow run ci.yml -f benchmarks=true        # all suites, reports uploaded as artifacts
gh workflow run ci.yml -f budget=true            # frame-time budget only
```

The `--fps` figures below were taken with .NET SDK 11.0.100-rc.3 on Windows 11, x64;
re-measure before quoting them.

## Today, in one table

The two rows that decide a regression are read by `tools/budget-check.ps1`, which fails a run over
either ceiling: the size check runs on every pull request, and the frame check on demand (Actions →
CI → Run workflow → **budget**) because a shared runner is too noisy to fail one. That on-demand job
reports its numbers in the run summary and warns instead of failing: a hosted runner measured 1.69 ms
where the reference machine measures 0.82 ms, so the script itself is the gate when it is run on the
machine the budget came from.

| | Value | Where it is enforced |
|---|---|---|
| Gallery frame, 1240x820, software raster | **0.83 ms** mean (p50 0.69, p95 1.16, p99 1.39) | budget 0.90 ms, `--fps 900` |
| Build only (a GPU would rasterize) | 0.02 ms | same run |
| Managed allocation per frame | **0 B** | `FrameLifecycleTests`, `NewComponentTests` |
| Gallery AOT executable, win-x64 | 3 167 232 B | budget 3.2 MB |
| A million virtual list rows | ~12 µs | `--scale` |
| A million chart points, undecimated | 45.9 ms | why decimation exists |
| A million chart points, decimated to 800 | 0.018 ms | same |
| Idle window CPU | 0 % | by construction: CUI sleeps between frames |

The executable grew from 2 141 184 B to 2 579 456 B with CUI 1.3, and the growth is upstream's: the
twenty components it added are about 5 000 lines of new C (`cui_devtools.c`, `cui_devpanels.c`,
`cui_devviews.c`, `cui_graph.c`, `cui_regex.c`) compiled into the same library. The frame time did not
move, because a page still draws only the widgets on it. The ceiling moved with the binary rather
than the other way round, and it is a round number above the measurement, so the next regression still
fails the gate. `Localization` added 1 536 B on top of that, and no measurable frame time: it costs
one null test per frame, and a published table is compared slot by slot only when one is attached.

## Gallery, page by page

`tools/page-costs.ps1` walks all twenty-eight pages in one headless process: it clicks each page,
proves the heading says the page opened, parks the pointer, and then *dwells* — holding the page
for three seconds while it really draws, through the silent `pulse` command, so the measurement
never pays for serialising the tree it is measuring — while `dotnet cui` samples the app's
process. The run below is the reference machine, light theme, `-Each 3`.

`ms/frame` is wall time over frames completed in the dwell: it includes the protocol round trips
around the drawing, so it is an upper bound of what the page's own frame costs.

| Page | ms/frame | cpu ms | ws MB | priv MB |
|---|---|---|---|---|
| Overview | 0.09 | 3 219 | 56.3 | 38.9 |
| Buttons | 0.10 | 2 922 | 57.0 | 39.3 |
| Inputs | 0.08 | 2 984 | 57.7 | 39.7 |
| Selection | 0.11 | 2 984 | 54.4 | 36.4 |
| Navigation | 0.07 | 2 969 | 55.0 | 37.0 |
| Overlays | 0.07 | 3 016 | 55.2 | 37.0 |
| Feedback | 0.11 | 2 969 | 55.5 | 37.0 |
| Data display | 0.14 | 2 969 | 57.2 | 38.4 |
| Charts | 0.08 | 2 938 | 57.6 | 38.4 |
| Studio | 0.12 | 2 953 | 58.3 | 39.0 |
| Tools | 0.12 | 3 000 | 78.3 | 60.1 |
| Developer tools | 0.21 | 3 078 | 79.3 | 59.2 |
| More developer tools | 0.21 | 3 062 | 79.8 | 59.2 |
| Actions | 0.12 | 2 984 | 80.0 | 59.2 |
| Smart inputs | 0.10 | 2 969 | 80.1 | 59.2 |
| Pickers | 0.12 | 2 953 | 80.2 | 59.2 |
| Capture | 0.12 | 3 000 | 81.2 | 60.2 |
| Disclosure | 0.17 | 2 984 | 81.1 | 59.7 |
| Reader | 0.29 | 2 984 | 81.1 | 59.7 |
| Data grid | 0.18 | 2 984 | 81.7 | 60.1 |
| Code | 0.17 | 2 906 | 82.1 | 60.8 |
| Social and media | 0.21 | 2 906 | 86.1 | 64.7 |
| Planning | 0.19 | 3 062 | 81.9 | 60.8 |
| **More charts** | **0.85** | 3 062 | 82.4 | 61.5 |
| Motion | 0.15 | 2 984 | 82.6 | 61.6 |
| AI | 0.22 | 2 969 | 82.9 | 61.6 |
| Audio | 0.13 | 2 969 | 82.9 | 61.6 |
| Files | 0.13 | 2 859 | 83.1 | 62.5 |

What the table says:

- **A page costs what it shows.** Navigation and Overlays, mostly buttons and labels, draw in
  0.07 ms; Reader flows thirty heading-and-paragraph blocks in 0.29 ms; More charts, fifteen
  charts including a 168-cell heatmap, a year of squares and a sankey, costs 0.85 ms — the most
  expensive page in the gallery still fits a 60 fps frame seven times over.
- **The cpu column is flat by construction**: the dwell keeps one core busy drawing, so every
  page burns ~3 000 cpu-ms per 3 000 wall-ms. It becomes a signal only when a page cannot keep
  a core fed — none today.
- **ws and priv climb across the walk, not per page**: the steps are Tools (+21 MB) and Social
  and media (+4 MB), both one-time native state. Walking the same twenty-eight pages a second time
  in the same process adds **2.6 MB** (62.5 → 65.1), so nothing accumulates — the first walk is the
  process meeting each page for the first time.
- **The 21 MB at Tools is the colour-emoji font**, and it is the only large one. CUI draws emoji
  from the system font, and the first emoji anywhere in the app loads `seguiemj.ttf`: 12.3 MB on
  disk, kept whole in memory because it carries outlines rather than bitmap strikes, plus copies
  of its 7.1 MB `COLR` and 0.25 MB `CPAL` tables. Three emoji at 32 px in an otherwise empty
  headless process take private bytes from 13.3 to 34.2 MB; a new pixel size adds an atlas (1 MB
  up to 40 px, 4 MB above). Nothing else on the page is visible at that scale: the terminal's ring
  buffer is 32 KB of native memory, the log's 16 KB, and the file browser lists two dozen entries.
- **The +4 MB at Social and media is its scene textures, and it is freed on the way out**: Planning
  is 2.5 MB lower, and the dips on earlier pages (Overlays −3.5 MB) are the same thing — CUI
  releasing textures the scene no longer shows.
- **The managed heap is flat, and it is in the CSV.** Across two walks `heap` stays between 6.9 and
  18.6 MB with no trend: the gen0 sawtooth, not growth. Private bytes rising against a flat heap
  are native by definition — and .NET keeps freed gen2 segments committed until something asks it
  for them, which is why a .NET process' private bytes never fall back as fast as its live data
  does. What a *frame* allocates is pinned at **0 B** by `FrameLifecycleTests`; that, not a sampled
  heap, is the number worth a column.

```sh
pwsh tools/page-costs.ps1                      # the whole walk, 3 s per page
pwsh tools/page-costs.ps1 -Each 5 -Csv .tmp/page-costs.csv
```

## Scale: a million of something

Two kinds of "big" behave completely differently, and the difference decides how
you should build the screen.

### Rows virtualize — cost is flat

`cui_virtual_list` derives the visible window from the scroll offset and view
height and builds only those rows. 1 000× more rows costs 1.6× time:

| Rows | Frame build | Allocated |
|---|---|---|
| 1 000 | 7.5 µs | 0 B |
| 100 000 | 11.7 µs | 0 B |
| 1 000 000 | 11.8 µs | 0 B |

So a million-row list is about 12 µs of a 16.7 ms frame. Row count is not the
constraint; row *content* is.

### Chart points do not virtualize — cost is linear

`cui_line_chart_ex` turns every value into geometry. Nothing is culled, because
nothing knows the points are off-screen.

| Points | Build only | Build + software raster | Build after decimating to 800 | Decimation itself |
|---|---|---|---|---|
| 1 000 | 0.025 ms | 2.1 ms | 0.022 ms | 0.011 ms |
| 10 000 | 0.192 ms | 9.3 ms | 0.019 ms | 0.033 ms |
| 100 000 | 3.2 ms | 60.2 ms | 0.019 ms | 0.158 ms |
| **1 000 000** | **45.9 ms** | **337 ms** | **0.018 ms** | 0.765 ms |

A million raw points is 45.9 ms of geometry building before any pixels — a 22 fps
ceiling on the CPU alone, and 3 fps with the software rasterizer. That is not a
chart, it is a stall.

Decimating first flattens it: **0.018 ms regardless of input size**, for 0.765 ms
of decimation work. That is why `SeriesDecimator` exists — it collapses each
bucket of consecutive samples to its minimum and maximum in the order they
occurred, which keeps spikes that averaging would erase. Pass `width / 2` buckets
and you get about one point per pixel.

Feed it a reusable buffer and tell the chart how much is live via `Series.Count`,
otherwise slicing the buffer allocates every frame:

```csharp
private readonly float[] _window = new float[SeriesDecimator.RequiredCapacity(400)];
private readonly Series[] _series = [new Series { Values = _window }];

int n = SeriesDecimator.Decimate(rawSamples, _window, 400);
_series[0].Count = n;                      // 0 would mean "all of _window"
ui.LineChart("live", _series, new ChartOpts { Height = 300 });
```

## Real time

A stock ticker or a monitoring dashboard is judged on frame times, not averages.
`--fps` runs a 600-point window over a one-million-sample feed, feeds it one new
sample per frame, decimates, draws and rasterizes, then reports the distribution
over 900 frames at 1280×720:

| | mean | p50 | p95 | p99 |
|---|---|---|---|---|
| Build + software raster | 0.82 ms | 0.69 | 1.11 | 1.17 |
| Build only (a GPU would rasterize) | 0.02 ms | 0.02 | 0.02 | 0.02 |

(An earlier revision of this table read 1.38 ms; the difference is the 1240×820 window the harness
draws now, and the arena work described below.)

| Budget | Frames that fit |
|---|---|
| 144 fps (6.94 ms) | 100% |
| 120 fps (8.33 ms) | 100% |
| 60 fps (16.67 ms) | 100% |
| 30 fps (33.33 ms) | 100% |

**Managed allocation per frame: 0 B.** Steady-state rendering does not allocate,
which is what keeps the p99 close to the p50 — no Gen0 collection lands inside a
frame.

These are software-raster figures. With the OpenGL renderer the raster half moves
to the GPU and the CPU side is the 0.02 ms row, so the frame budget is dominated
by your own data work, not by CUI.

## Allocation model

**Frame arena.** `Utf8` is a chunked bump allocator reset at the start of every
frame. It holds the UTF-8 of each label and the pointer, struct and float arrays
CUI takes as `const char *const *`. It replaced a `Dictionary<string, IntPtr>`
that hashed every label every frame and grew without bound.

Chunks are **never moved or reallocated**, only added. CUI reads text during the
frame's paint phase, after your draw code returns, so a pointer handed over early
must still be valid at the end of the frame. A frame that outgrows a chunk takes
another; chunks are reused next frame.

| Marshalling | Time | Allocated |
|---|---|---|
| `Utf8.Get("literal")` | 4.0 ns | 0 B |
| `Utf8.Get("literal"u8)` | 1.7 ns | 0 B |
| handler, two values (`$"rows {a} of {b}"`) | 24.5 ns | 0 B |
| `$"rows {a} of {b}"` as a `string` | 22.0 ns | 56 B |

## Interpolation through `UiText`

The interpolated-string handler used to be ~1.4× slower per call than building a
string and encoding it, so `Textf` bought allocation-free frames at a CPU cost.
That is gone: the handler now formats numbers straight into the arena through
`IUtf8SpanFormattable`, reserves once from an estimate, hands the unused tail
back to the arena, and grows (rather than truncating) when a value is longer
than the estimate. Per call it is within noise of string interpolation, and per
frame it is the cheaper of the two.

Before that change — the numbers that motivated it, 2 000 rows over 60 frames:

| | Time | Allocated |
|---|---|---|
| `ui.Text($"#{i}")` with a `string` label | 1 168–1 257 µs | 64 KB |
| `ui.Textf($"#{i}")` with a `u8` label | 1 076–1 186 µs | 0 B |

Both overload sets are now one `UiText` method on the allocation-free path. Steady-state headless
tests cover interpolation in text, metrics, drawing and canvas callbacks together at
**0 B/frame**, and the live-chart harness reports **0 B/frame** at a 0.82 ms mean.

Standard format specifiers (`F2`, `N0`, `X`) are formatted natively, alignment
(`$"{v,8}"`) works, and a custom pattern such as `0.00`, which `Utf8Formatter`
rejects, still falls back to an allocating `ToString` — correctness first.

## Bugs these measurements found

Each was invisible to a normal build:

- **Shared handler buffer.** The interpolated handler first wrote to a single
  per-thread buffer. Since CUI reads text in the paint phase, every row rendered
  the last value written. Handler strings now come from the frame arena, and a
  test pins that two strings in one frame never share memory.
- **Truncated values.** The handler sized its block from `literalLength +
  formattedCount * 24` and silently dropped whatever did not fit, so
  `$"Note: {longTitle}"` rendered `Note: `. It now grows the block and copies,
  and `Utf8HandlerTests` pins a 200-byte value.
- **Headless frames grew without bound.** `CuiApp` reset the arena and the
  deferred-painter list every frame, but `CuiSurface` never did: a headless test
  or benchmark leaked both per frame. `CuiSurface.BeginFrame` now does the same
  reset, and `FrameLifecycleTests` runs 1 000 frames and asserts a flat arena.
- **A `byte[]` per draw call.** `Ui.DrawText` encoded through
  `Encoding.UTF8.GetBytes` and then re-measured the UTF-8 length by scanning for
  the terminator. It now encodes once with the length already known, and takes
  UTF-8 bytes directly.
- **Deferred canvas paint.** `cui_canvas` stores its callback on the layout node
  and runs it while painting, so a delegate cleared when `Canvas()` returned
  never ran. Painters are kept until `cui_app_frame` returns.
- **Arena reallocation.** Growing the arena by reallocating invalidated pointers
  already handed to CUI during the same frame. Hence chunks that never move.
- **A slice per frame.** The live-chart harness showed 2 483 B allocated per
  frame, from `decimated[..n]` plus an interpolated label. `Series.Count` now
  lets a caller reuse one buffer, and the harness now allocates 0 B.
