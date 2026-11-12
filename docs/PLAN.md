# What is left

Only open work. What shipped is in [CHANGELOG.md](../CHANGELOG.md); how to build, test and release is
in [AGENTS.md](../AGENTS.md) and [RELEASING.md](RELEASING.md); the decisions that must not break are
in [ARCHITECTURE.md](ARCHITECTURE.md). Every item here passes the same gates as any other change.

## 1. Open work

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
- **Installing a skill into a particular agent harness.** `dotnet cui docs` is the portable form of
  the guide: it works in any shell, for any tool. Per-harness registration (a `skills/` folder, a
  plugin manifest, a system prompt) stays a local convention, and none of it is generated.
- **One window per app.** CUI creates one window; multi-window is an upstream question.
- **IME coverage** beyond what upstream implements.
- **Chart time axes and gaps.** The managed `NormalizeMode` is a y-value transform; CUI still spaces
  line/bar points uniformly by index and has no explicit gap segments. True timestamps, irregular
  calendars, aligned hover/labels and NaN-gap semantics need native API and renderer work upstream.
- **Complete layout introspection.** `cui layout` can report verified named rectangles and
  lower-bound unions, but CUI exposes neither id-0 container rectangles nor authoritative
  scroll/content extents. A read-only layout walk and scroll-info API belong upstream.
- **Cross-platform live-window control.** Windows live attach is opt-in and uses the pinned
  backend's between-frame message pump, thread/context-owned callback guards and destruction
  hooks. Other backends still need an upstream post-at-frame-boundary/wake and destroy-lifecycle
  API; do not substitute a background render loop or silently start a headless app.
- **macOS signing and notarization** for a distributed `.app`.
