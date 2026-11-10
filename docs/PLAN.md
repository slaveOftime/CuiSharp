# What is left

Only open work. What shipped is in [CHANGELOG.md](../CHANGELOG.md); how to build, test and release is
in [AGENTS.md](../AGENTS.md) and [RELEASING.md](RELEASING.md); the decisions that must not break are
in [ARCHITECTURE.md](ARCHITECTURE.md). Every item here passes the same gates as any other change.

## 1. Decisions waiting on a person

**`DataGrid` and `Segmented` ceremony.** `DataGrid` takes six `ref` parameters plus a `bool[]` per
row, which is why an app that only wants a read-only table uses `VirtualList` instead. Proposal: a
`GridState` class holding `First`, `Last`, `Selected`, `Sort`, `Cursor`, `Anchor` and a
`ui.DataGrid(id, columns, rows, state, body)` overload that forwards the fields as `ref`, plus
`ui.Segmented(id, items, value)` returning `int?` so a page keeps one field instead of a mirror. Both
are `MANUAL` in `overrides.json` and change the public surface, so they need a decision before code.

**A count beside an array in a record.** `QueryField.Choices` and `ChoiceCount`, `ApiEndpoint.Params`
and `ParamCount`: the count is what native reads, so the two can disagree and a caller has to keep
them in step. The generator already derives a count for option structs; a record could hide the count
and fill it from the array's length in `ToNative()`. It is not derived today because a count on an
*output* record (a result CUI fills in) is information, not a derivation, and the generator cannot
tell the two roles apart from the declaration alone.

## 2. Not started

- **`dotnet new cui-script`** — one file, `#:package CuiSharp@*`, `dotnet run app.cs` opens a window.
  The template covers the solution case only. Prove it in `verify-template.ps1`.
- **A `Localization` owner for `cui_set_strings`.** CUI keeps the pointer to its month, weekday and
  "Today" table and reads it every frame, so the wrapper needs storage that outlives the call — the
  frame arena cannot provide it, and the two functions stay `EXCLUDED` until it exists. The shape:
  a `Localization` class holding the managed strings, one unmanaged block rebuilt when they change,
  and `ui.SetLocalization(table)`; `CuiSurface`/`CuiApp` own the instance the way they own the
  theme. Twelve month names, their short forms, three weekday forms and `Today`, each entry
  nullable so a partial table falls back to English.
- **The gallery pages for the extended components** (`examples/gallery_more.c`'s other fifteen):
  *Developer tools* and *More developer tools* cover CUI 1.3's twenty, but actions, smart inputs,
  pickers, capture, disclosure, reader, data grid, code, social and media, planning, more charts,
  motion, AI, audio and files still draw only in C's own gallery. One page per commit-sized slice;
  a missing wrapper is a generator or `overrides.json` fix, never a hand edit.
- **`win-arm64` and `linux-arm64`** native builds, so the RID list stops at three. Every RID is built
  on its own runner; a new one needs the archive, the linker arguments and a leg in `ci.yml`.
- **Analyzer code fixes** for CUI001–CUI008. The diagnostics and their messages exist and are
  enforced; none of them can rewrite the code yet.

## 3. Where upstream has to move first

`native/CUI` is pristine (D2). These are its work, not ours:

- **A glyph-coverage query** (`cui_font_has_glyph`). `UseSystemFontFallback` currently decides by
  painting a sample and comparing pixels, because a missing glyph still reports a `.notdef` advance.
- **A window icon** on all three platforms, and `_NET_WM_ICON` on X11. The handle is now reachable
  (`CuiWindow.NativeWindow`), so an app can P/Invoke its own on Windows today.
- **Documentation on the declaration.** 208 of the 278 generated wrappers carry nothing but the C
  name, because the header has no comment the generator can read, so `ui.TableRow` says
  `cui_table_row_ex` and nothing more.

## 4. Tracked gaps, not planned

- **Accessibility.** CUI publishes no accessibility tree, so there is no UIA, AT-SPI or
  NSAccessibility. The semantic tree is the foundation to build on, not a substitute.
- **One window per app.** CUI creates one window; multi-window is an upstream question.
- **IME coverage** beyond what upstream implements.
- **macOS signing and notarization** for a distributed `.app`.
