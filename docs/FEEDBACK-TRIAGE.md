# Agent feedback triage — Front Yield Monitor on 0.1.0

Source: an external build report (Native AOT, win-x64 + linux-x64, no webview) against the
**released 0.1.0 package**. Verified item by item against this branch, because several complaints
describe code that has since changed. Cost is the agent time I estimate for us to close it;
`S` ≈ under an hour, `M` ≈ a day, `L` ≈ multi-day or a product decision.

## Already closed on this branch (do not re-plan)

| Report item | Status here |
|---|---|
| Linux binary requires GLIBC_2.43, nothing loads | `native/build-native.sh` refuses a `libcui.so` needing more than `CUI_GLIBC_BASELINE` (2.35) and writes provenance; release builds native on `ubuntu-22.04`; the package carries `runtimes/native-provenance.txt`; `docs/NATIVE-ABI.md` states the floor. **The 0.1.0 artifact predates this** — a new package must be built and the floor verified on a 2.35 box |
| AOT publish leaves `cui.dll` / `cui.lib` beside the exe, apps hand-roll a verify target | `CuiSharpDropNativeFilesFromAotPublish` removes them on every platform; `verify-template.ps1` fails if anything but the executable remains. Their `VerifySingleExecutable` target is unnecessary — a doc line, not a feature |
| "No way to print the native revision" | Provenance is in the package and names `repository … @ rev` |
| Analyzer does not warn on blocking I/O in a frame | `CUI001` already reports `Task.Result`, `.Wait()`, `GetAwaiter().GetResult()`, `Thread.Sleep/SpinWait/Yield` and suggests `Mutation`. It does **not** cover `File.*`/`Directory.*`/`Task.Run` — see below |

## Confirmed bugs (cheap, real)

**B1 — `CuiApplication.Run` initializes the root before the window exists.** `S`
`CuiApplication.cs:70` calls `root.Initialize()` and then `CuiApp.Run(...)`. The window is created
inside `cui_app_run`, so `OnInitialized` runs with no HWND — exactly the report's `WM_SETICON`
no-op. `Component.Initialize`'s own doc says "once, on the first frame that draws it", and the
`CuiApp.Run` path honours that; `CuiApplication` is the only eager caller. Two things are wrong: the
ordering, and that the framework offers no hook for "the window exists".

**B2 — a missing or too-new native library reports a bare loader error.** `S`–`M`
`CuiApp.Run` throws `InvalidOperationException("cui_app_create returned null…")` when the DLL is
absent, and a `DllNotFoundException` when the ELF cannot resolve symbols. Neither names the RID, the
glibc floor, or the two remedies. Wrap the first native call in a check that reports: which RID was
packaged, the floor from provenance, and the two fixes (rebuild on the platform, or update the
distro).

**B3 — `TestApp.SavePng` captures overlays.** `S`
The last click leaves a tooltip in the image, so every snapshot test parks the pointer by hand
(`MoveTo(1, 800)`). That is a framework errand: capture should park the pointer first, or skip
overlay layers.

**B4 — `Frame(n)` does not advance the clock.** `M`
Frames are free, time is not, so an animation (bar growing, fade) is caught mid-flight and every
snapshot test invents its own `60 × (16 ms sleep + Frame)` settle loop. Needs `app.Advance(TimeSpan)`
and `app.Settle()`, plus an `Animating` flag so a test knows when nothing is in flight.

## Confirmed doc gaps (cheap, high yield for an agent)

- **D1 — the docs an agent needs are not in the package.** `S`
  `llms.txt`, `skills/cui-dotnet/SKILL.md` and `docs/NATIVE-ABI.md` exist in the repo and are not
  packed; the package ships `README.md` only. Pack them, plus a short `docs/agents.md` (the loop, the
  pitfalls, the publish recipe).
- **D2 — font fallback behaviour is only in `cui_text.c`.** `S`
  The 4-fallback cap, the global any-font scan, the "missing glyph has zero advance" rule and the
  `MeasureText(...).Width == 0` probe are undocumented. `AddFontFallback` exists and says nothing
  about any of it. Say it in `Ui.Fonts.cs` and in the guide's text section.
- **D3 — `OnInitialized` has no stated ordering.** `S` (merged with B1)
  Whatever the fix, the doc must say when the window exists and where branding belongs.
- **D4 — publish guidance omits the floor and the static-native rule.** `S`
  README's Linux paragraph says `libx11-dev` at build time and nothing about the glibc floor.
- **D5 — no widget behaviour contracts.** `M`–`L`, the most valuable item on the list
  Does `DataGrid` keep selection across frames, is the folder picker modal, what does `Dropzone`
  return, when does the HWND exist — all answered only by reading `cui_grid.c`. `bindgen` already
  reads `cui.h`, so the fix is mechanical: carry the native doc comment into the generated XML doc,
  and emit one grep-able widget catalog per release instead of 40 `.g.cs` files to skim.
  `cui docs <widget>` as a CLI command is the wrong shape here — we just decided the tool stays a
  relay, and a doc lookup belongs in generated files, not a runtime.

## Feature asks, by cost

**S — do next, no product question**

- `FindById(id)` / `Click(id)` and an id-based query, so a test is not written against a translated
  label (report: `Find("Segment", "中文")` breaks on localization). Ids are already recorded for
  tabs and segments, so this is a query-surface addition.
- `CuiApplication`: expose `NativeWindow`, `Backend`, `IsSoftwareRenderer`, `IsMaximized` (all exist
  on `CuiApp` and are unreachable from the recommended host), and a `WindowCreated` hook.
- `ParkPointer()` and `SavePng(overlays: false)`.

**M — worth it once the above lands**

- `CuiDiagnostics.Report()`: one structured snapshot (backend, software renderer, native revision,
  glibc floor, display, dialog helper, loaded fallbacks). Every app writes this by hand today, and
  we have half the data in provenance already. Ship it in the template as `--check`.
- `CUI001` extended to `File.*`/`Directory.*`/`Task.Run` inside a frame, and a reflection-for-JSON
  warning under AOT (`CUI008`).
- Injectable file-picker seam (`IFileDialogProvider` or an override on the app) so a picker flow is
  testable without Xvfb and `xdotool`. Structured result (`Picked`/`Cancelled`/`Error`) is only
  honest if the native side can distinguish them — check before promising.
- `AssertGoldenPng` with tolerance, and `pwsh tools/snapshots.ps1` promoted to a public helper.

**L — product decisions**

- **glibc tier.** 2.35 (Ubuntu 22.04, current baseline) excludes Debian 11 / Ubuntu 20.04 (2.31)
  and anything manylinux-2.28 shaped. Building the `.so` in a `manylinux_2_28` container is the
  cheap route to a wider floor; it needs a container step in CI and a decision about how many tiers
  we publish. The report's `dotnet cui natives --rid … --revision …` helper is the expensive answer
  to the same question and I would not build it: cmake, the submodule and a compiler on the
  consumer's machine is a worse trade than one more prebuilt tier.
- **System font fallback helper** (`UseSystemFallback(Script.Han)`). Cheapest honest slice is a
  `HasGlyph(font, rune)` probe plus a one-time missing-glyph diagnostic naming the codepoint and the
  widget — that turns the silent blank UI into a message. A per-OS path-probing helper (msyh.ttc /
  `fc-match` / PingFang) is real work with real fragility, and fontconfig-by-subprocess inside a
  windowing app is a smell worth debating first.
- **Reducing `DataGrid` ceremony** (`ui.DataGrid(model)` over a `SelectionState`/`SortState`). The
  report is right that six `ref` parameters pushed them off the widget entirely. This is a wrapper
  design question, `MANUAL` in `overrides.json`, and it sets a precedent for "managed shape over C
  shape" that the project has so far avoided.
- **`Segmented`/`Tabs` returning a value** instead of `ref int` + `string[]`: same question, smaller.

## Upstream, not ours

- `WM_SETICON` / X11 icon properties do not exist in `cui.h`. Exposing the HWND unblocks the report's
  P/Invoke; a cross-platform `Icon` option belongs in CUI, per D2.
- `org.freedesktop.portal.FileChooser` on Linux is CUI's dialog implementation; ours is the managed
  shape around it.
- An accessibility tree stays a known gap for the same reason.

## Recommended order

1. **B1 + D3** (lifecycle bug and its doc) and **B3** (snapshot overlays): one PR, all cheap, all
   things every app trips on.
2. **D1** (pack the docs) and **D4**: a package that explains itself is the cheapest possible fix
   for the biggest cost in the report — an agent reading C source to answer API questions.
3. **S-tier feature asks**: id-based finders, `CuiApplication` surface, `ParkPointer`.
4. **B4** (`Advance`/`Settle`/`Animating`) with tests, then **D5** (native doc comments into `.g.cs`,
   the widget catalog) — the single largest token saving for the next app, and mechanical.
5. **M-tier**: `CuiDiagnostics.Report()`, `CUI001` coverage, golden images, injectable picker.
6. Product decisions: glibc tier, font fallback, `DataGrid`/`Segmented` shape.

## Notes for the report's author

- The `dotnet cui dev` / session mode they may have seen referenced was implemented and removed; the
  tool is a one-shot relay plus `--stdin` (`docs/AI-ITERATION-STATUS.md` records why).
- `TestApp`'s doc comment names `dotnet cui mcp`, which does not exist and is no longer planned; that
  reference is stale and being removed.
