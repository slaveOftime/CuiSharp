# Performance

Every number here is measured by `src/CuiSharp.Benchmarks` on one Windows
machine. Trust the shape of the curves; treat absolute values as indicative.

```sh
dotnet run -c Release --project src/CuiSharp.Benchmarks     # BenchmarkDotNet suites
dotnet run -c Release --project src/CuiSharp.Benchmarks -- --scale    # 1k..1M points, one shot
dotnet run -c Release --project src/CuiSharp.Benchmarks -- --fps 900  # live-chart frame times
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
CI → Run workflow → **budget**) because a shared runner is too noisy to fail one.

| | Value | Where it is enforced |
|---|---|---|
| Gallery frame, 1240x820, software raster | **0.82 ms** mean (p50 0.69, p95 1.13, p99 1.33) | budget 0.90 ms, `--fps 900` |
| Build only (a GPU would rasterize) | 0.02 ms | same run |
| Managed allocation per frame | **0 B** | `FrameLifecycleTests`, `NewComponentTests` |
| Gallery AOT executable, win-x64 | 2 580 992 B | budget 2.7 MB |
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
