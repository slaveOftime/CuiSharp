# What is left

Only open work. What shipped is in [CHANGELOG.md](../CHANGELOG.md); how to build, test and release is
in [AGENTS.md](../AGENTS.md) and [RELEASING.md](RELEASING.md); the decisions that must not break are
in [ARCHITECTURE.md](ARCHITECTURE.md). Every item here passes the same gates as any other change.

## 1. Decisions waiting on a person

**Linux glibc tier.** The package promises glibc 2.35 (Ubuntu 22.04, Debian 12, RHEL 9) and refuses
to build a `libcui.so` that needs more. Lowering it widens who can run the package and costs a
container in the native build:

| Floor | Runs on | Cost |
|---|---|---|
| 2.35 (now) | Ubuntu 22.04+, Debian 12+, RHEL 9+ | none |
| 2.31 | + Debian 11, Ubuntu 20.04 | build in a `debian:11` image; X11 headers must be there |
| 2.28 | + manylinux_2_28 targets, RHEL 8 | build in `manylinux_2_28`; oldest compiler, most to verify |

`native/build-native.sh` already takes `CUI_GLIBC_BASELINE`, so the change is the CI image plus the
number in `CuiNative.LinuxFloor` and [native-abi.md](../skills/cui-dotnet/native-abi.md).

**`DataGrid` and `Segmented` ceremony.** `DataGrid` takes six `ref` parameters plus a `bool[]` per
row, which is why an app that only wants a read-only table uses `VirtualList` instead. Proposal: a
`GridState` class holding `First`, `Last`, `Selected`, `Sort`, `Cursor`, `Anchor` and a
`ui.DataGrid(id, columns, rows, state, body)` overload that forwards the fields as `ref`, plus
`ui.Segmented(id, items, value)` returning `int?` so a page keeps one field instead of a mirror. Both
are `MANUAL` in `overrides.json` and change the public surface, so they need a decision before code.

## 2. Not started

- **`dotnet new cui-script`** — one file, `#:package CuiSharp@*`, `dotnet run app.cs` opens a window.
  The template covers the solution case only. Prove it in `verify-template.ps1`.
- **The thirteen extended gallery pages** (`examples/gallery_more.c`): the coverage proof nothing
  else gives, because those pages exercise components the tests do not. One page per commit-sized
  slice; a missing wrapper is a generator or `overrides.json` fix, never a hand edit.
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
- **`org.freedesktop.portal.FileChooser`** on Linux, ahead of the zenity/kdialog fallback: it is the
  only one that works under Wayland, Flatpak and a remote session.
- **Documentation on the declaration.** 664 of the wrapped functions have no comment in the header and
  none above the definition, so their wrapper can only say `cui_table_row`.

## 4. Tracked gaps, not planned

- **Accessibility.** CUI publishes no accessibility tree, so there is no UIA, AT-SPI or
  NSAccessibility. The semantic tree is the foundation to build on, not a substitute.
- **One window per app.** CUI creates one window; multi-window is an upstream question.
- **IME coverage** beyond what upstream implements.
- **macOS signing and notarization** for a distributed `.app`.

## 5. Written here, not yet proven on CI

The gates themselves are new. They pass locally, and only a pull request and a dispatched run can
prove them on the runners: the size budget and public-surface diff steps, the native-build cache
keyed on the submodule commit, and the on-demand frame-budget job (`Actions → CI → Run workflow`).
