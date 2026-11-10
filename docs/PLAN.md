# What is left

Only open work. What shipped is in [CHANGELOG.md](../CHANGELOG.md); how to build, test and release is
in [AGENTS.md](../AGENTS.md) and [RELEASING.md](RELEASING.md); the decisions that must not break are
in [ARCHITECTURE.md](ARCHITECTURE.md). Every item here passes the same gates as any other change.

## 1. Open work

- **The size profile, measured in [SIZE.md](SIZE.md)**. A minimal app is 58% .NET runtime, 19% CUI
  native and 3% CuiSharp's own code, and the widget code an app does not use is already dropped by
  `/OPT:REF`; so a second "mini" package would save about 2% of the bytes for a second binding,
  analyzer set and release artifact. Land these instead, each measured:
  - **The `tuned` NativeAOT switches by default** in what the template publishes
    (`UseSystemResourceKeys`, `EventSourceSupport=false`, `MetadataUpdaterSupport=false`,
    `StackTraceSupport=false`, `HttpActivityPropagationSupport=false`, `UseNativeHttpHandler=true`,
    and the unsafe-serialization pair): −128 512 B on the smallest app, −156 672 B on the gallery.
    The two that change observable behaviour need a line in the template and in the docs.
  - **`CuiSharp.Headless` off unless asked for**: one MSBuild property in `build/CuiSharp.targets`
    whose default is `false`, with `CuiSharp.Tools` and the gallery turning it back on and the
    template documenting `dotnet run -p:CuiSharpHeadless=true`. Worth −181 248 B on a windowed app.
  - **`ScopeEnds.Close` must not name every container.** It roots all 39 `*_end` functions from
    every app that opens one container, which drags `cui_pickers`, `cui_dock` and their
    dependencies in unasked and stops a reduced archive from linking at all. −24 064 B, and the
    generator change that would let a widget set and a binary be coupled.
  - **A size row per shape** in `tools/budget-check.ps1`, fed by `tools/size-report.ps1`, so an
    upstream bump that reaches a used widget fails as a number instead of arriving in a release.
- **The gallery pages for the extended components** (`examples/gallery_more.c`'s other fifteen):
  *Developer tools* and *More developer tools* cover CUI 1.3's twenty, but actions, smart inputs,
  pickers, capture, disclosure, reader, data grid, code, social and media, planning, more charts,
  motion, AI, audio and files still draw only in C's own gallery. One page per commit-sized slice;
  a missing wrapper is a generator or `overrides.json` fix, never a hand edit.
- **Remaining analyzer code fixes** for CUI001, CUI006–CUI008. CUI002/CUI003 rewrite scope disposal
  and guards; CUI004 keys repeated widgets in simple for/foreach loops; CUI005 removes an awaited
  BCL `ConfigureAwait(bool)`. IDE-only fixes ship in a separate assembly so command-line analyzers
  do not depend on Workspaces. Still needed: safe rewrites for the remaining rules and sequential
  fix-all for overlapping scope bodies. Nested loops and ambiguous row keys remain manual.

## 2. Where upstream has to move first

`native/CUI` is pristine (D2). These are its work, not ours:

- **A glyph-coverage query** (`cui_font_has_glyph`). `UseSystemFontFallback` currently decides by
  painting a sample and comparing pixels, because a missing glyph still reports a `.notdef` advance.
- **A window icon** on all three platforms, and `_NET_WM_ICON` on X11. The handle is now reachable
  (`CuiWindow.NativeWindow`), so an app can P/Invoke its own on Windows today.
- **Documentation on the declaration.** 208 of the 278 generated wrappers carry nothing but the C
  name, because the header has no comment the generator can read, so `ui.TableRow` says
  `cui_table_row_ex` and nothing more.

## 3. Tracked gaps, not planned

- **Accessibility.** CUI publishes no accessibility tree, so there is no UIA, AT-SPI or
  NSAccessibility. The semantic tree is the foundation to build on, not a substitute.
- **One window per app.** CUI creates one window; multi-window is an upstream question.
- **IME coverage** beyond what upstream implements.
- **macOS signing and notarization** for a distributed `.app`.
