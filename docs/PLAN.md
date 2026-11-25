# What is left

Only open work. What shipped is in [CHANGELOG.md](../CHANGELOG.md); how to build, test and release is
in [AGENTS.md](../AGENTS.md) and [RELEASING.md](RELEASING.md); the decisions that must not break are
in [ARCHITECTURE.md](ARCHITECTURE.md). Every item here passes the same gates as any other change.

## 1. Open work

- **Remaining analyzer code fixes** for CUI001, CUI006–CUI008. CUI002/CUI003 rewrite scope disposal
  and guards; CUI004 keys repeated widgets in simple for/foreach loops. IDE-only fixes ship in
  a separate assembly so command-line analyzers do not depend on Workspaces. Still needed: safe rewrites for the remaining rules and sequential
  fix-all for overlapping scope bodies. Nested loops and ambiguous row keys remain manual.

- **Profile the real-time chart regression across the CUI 1.4-to-1.5 window.** Alternating isolated
  old- and new-pin builds measured 0.82–0.84 ms versus 0.94 ms at 1280x720, with 0 B/frame
  throughout. The frame ceiling moved to 1.00 ms explicitly; identify the cost before claiming a
  cause or dismissing it as noise. Native fixes still belong upstream.
- **Bind the axis-label and can-drop callbacks** — done in this sync: the frame-callback bridge
  rents them (`Func<double, string?>` for axis labels, `Func<int, int, bool>` for the tree table's
  can-drop). What is still missing, in the same spirit:
- **Bind the custom canvas's input surface** — `cui_canvas_ex` (focusable canvases),
  `cui_wheel_region`/`cui_wheel_take`, `cui_set_cursor` and `cui_request_component_focus` are
  excluded with the Custom drawing section; a managed surface for keyboard focus, wheel takeover
  and cursor shaping on a hand-drawn canvas is its own design.
- **Expose the window-state calls through CuiApp** — `cui_app_minimize`/`restore`/
  `set_position`/`window_state`/`placement` are excluded with the `cui_app` section because
  window ownership belongs to CuiApp; a supported subset on `CuiWindow` (like the live session's)
  would let apps save and reopen where they were.
- **A managed GPU renderer bridge** — `cui_gpu_*` are excluded: CUI builds them behind compile-time
  flags and hands over a native interface of function pointers and device handles. Worth a design
  only if an app needs to skip OpenGL.
- **The session tool crashes driving the Pickers page** after a nav click: the click's frame is
  fine (the page opens), the next `frame` kills the session, while the same page frames cleanly
  under `TestApp`. Suspect the session host, not the page; the dark `16-pickers.png` is the one
  gallery snapshot this gap leaves missing.
- **The Data display page allocates ~300 B/frame in steady state** (measured at 0.4.7): the demo
  invoice rows build their label and amount strings fresh every frame. The delegates the tree
  table gained are hoisted into fields; the page's demo rows could cache theirs the same way.

## 2. Where upstream has to move first

`native/CUI` is pristine (D2). These are its work, not ours:

- **A glyph-coverage query** (`cui_font_has_glyph`). `UseSystemFontFallback` currently decides by
  painting a sample and comparing pixels, because a missing glyph still reports a `.notdef` advance.
- **A window icon** on all three platforms, and `_NET_WM_ICON` on X11. The handle is now reachable
  (`CuiWindow.NativeWindow`), so an app can P/Invoke its own on Windows today.
- **Documentation on the declaration.** 91 wrapped functions still say nothing but the C name.
  Everything CUI writes now reaches the wrapper (above the declaration, beside it, above the
  definition, the component's paragraph wherever upstream attached it, and every field and constant
  comment), so what is left is what upstream has not written: `ui.Button`, `ui.Text`, `ui.Card` and
  their neighbours get a sentence only when CUI documents the function itself. A few components are
  documented in a block that no declaration follows — `cui_code_editor_ex`'s paragraph sits between
  the previous struct's comment and `cui_text_mark`'s own one-liner — and libclang attaches it to
  neither, so it is unreachable without reading sections rather than declarations.

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
- **A wake for an idle window off Windows, and pixels for a live one.** Live attach works on every
  backend, but two things still need the Win32 backend. Only it can be woken from another thread —
  CUI pumps messages inside `cui__plat_wait` there, so a post is a frame — while the other backends
  are reached by a half-second heartbeat the frame loop arms itself (`LiveSession.FrameDrew`), which
  is why a command that arrives at a sleeping X11 or Cocoa window can take that long to answer. And
  only it hands back the `cui_app*`: `cui_app_screenshot` takes no `NULL` for "the running app" the
  way the other `cui_app_*` calls do, so `dotnet cui png -a` answers with that reason off Windows
  while the tree, the rectangles and every input step work. A backend that can be woken from another
  thread — an X11 client message, `glfwPostEmptyEvent`, `SDL_PushEvent`, a main-queue dispatch on
  Cocoa — needs an upstream API for it; the same wake retires the 500 ms idle cap
  `Hosting/HotReloadWake` arms under `dotnet watch` off Windows. A `cui_app_screenshot` that honours
  `NULL` retires the second, and both belong upstream, not in `native/patches/`. Do not substitute a
  background render loop or silently start a headless app.
- **macOS signing and notarization** for a distributed `.app`.
