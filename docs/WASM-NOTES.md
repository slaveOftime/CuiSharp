# WASM investigation — running notes (`investigate/wasm-github-pages`)

Goal: serve the **real C# gallery** in a browser on GitHub Pages. This is an investigation log,
not settled product documentation.

## Current verdict

**The managed-to-native bridge is not the blocker.** A stock .NET browser app calls CUI directly
through the SDK-generated `LibraryImport("cui")` P/Invoke table when `cui.a` is a
`NativeFileReference`. The filename matters: the SDK derives the P/Invoke module name from the
archive basename; `libcui.a` registers `libcui` and fails to resolve imports named `cui`. There is no
`cui_sym` or delegate bridge. The canvas host uses one synchronous JS import for presentation, not
for CUI calls.

**The actual AOT blocker is `LayoutScope`/`OpenScope`.** Their disposal stores a function pointer to
a generated `LibraryImport` method and calls it indirectly. Under the browser AOT runtime that
pointer is null; the simple `Column` scope traps on disposal, after its widgets already drew. The
same simple scope works under the interpreter. A one-end-function `[UnmanagedCallersOnly]` thunk
prototype fixed it in .NET 10 AOT, with no CUI or application changes. A generated per-end thunk
looks like the smallest route that preserves the existing per-scope linker reachability rule; it
still needs a full validation before adoption.

## What is verified

- **CUI builds from pristine C sources.** `tools/build-cui-wasm.ps1` selects the Emscripten pack
  paired with the target SDK. With .NET 10.0.403 / Emscripten 3.1.56 it compiles 54 C sources into
  a software-only `artifacts/wasm/cui.a` of 1,450,548 bytes, with function/data sections for linker
  dead stripping. The GL renderer, desktop app layer, and dialogs are omitted. No `native/CUI`
  edits or custom libc are needed.
- **Direct P/Invoke and optimized .NET 10 work.** `NativeFileReference(artifacts/wasm/cui.a)`
  registers module `cui`; the full browser sample's publish table contains 318 CUI imports under
  that name. Naming the archive `libcui.a` instead registers `libcui` and fails at the first call
  with `ManagedError: cui`.
  Compiling the archive with the Emscripten 6 pack and linking it with the .NET 10 / Emscripten 3
  pack also failed in `wasm-opt` (`Unknown option --enable-bulk-memory-opt`); the matching 3.1.56
  archive publishes with Release `-Oz` and runs in Chrome.
- **The full gallery and browser host run under the interpreter.**
  `samples/CuiSharp.Gallery.Browser` links the real 28-page gallery and presents it to a canvas. A
  separate all-pages loop reported `GALLERY-28-PAGES-OK faults=0` with the .NET 10.0.403 optimized
  build. Headless Chrome screenshots show the rendered Overview and Buttons pages; a click changes
  the selected page, and an input smoke types `Browser Input` into the gallery's Full name field.
  The checked-in `tools/test-browser-smoke.py` exercises this against the published output.
- **AOT direct calls work; AOT scope disposal does not.** In Chrome, no-scope `Heading`, `Text`,
  `Button`, and the `CuiSurface` lifecycle worked under AOT. The same test inside
  `using (ui.Column(...))` threw `RuntimeError: null function` exactly on scope disposal, in both
  .NET 10 and .NET 11. `Ui.Scopes.cs` stores `delegate*<nint, void>` and calls it in `Dispose()`;
  generated wrappers pass `&Native.<End>`, an address of a `LibraryImport` method.
- **A targeted fix prototype worked.** Replacing the stored import-stub address with an
  `[UnmanagedCallersOnly(CallConvCdecl)]` thunk that directly calls `Native.BoxEnd` let the same
  `.NET 10` AOT `Column` test draw and dispose. The prototype was reverted after the experiment.
  Next implement thunks in `tools/bindgen.cs`, one per native scope-ending function, and test every
  generated scope. Do not replace this with one switch that names every end function unless the
  size cost is measured and accepted: that would undermine the existing reachability design.

The earlier conclusion that direct P/Invoke resolution was broken, and the committed `cui_sym`
bridge probe, were false conclusions from an incomplete probe. They are superseded here; the bridge
artifacts have been removed from `docs/wasm-probe/` and replaced with the direct-call reproduction.

## Size and dependency evidence

An earlier standalone **non-AOT** .NET 11 browser app using the current 28-page gallery,
`CuiSharp`, invariant culture/timezone, the size profile, the software-only CUI archive, and Release
`-Oz` produced:

| Measure | Bytes |
|---|---:|
| `_framework` raw `.wasm` assets, combined | 3,535,676 |
| `_framework` `.gz` assets, combined | 1,659,118 |
| `_framework` `.br` sidecars, combined | 1,379,758 |
| largest compressed item, `dotnet.native.wasm.gz` | 956,821 |

These include the .NET runtime, CUI, CuiSharp, and the managed gallery, not a Blazor host. A no-CUI
browser baseline built with the same SDK had a 1,220,836-byte `dotnet.native.wasm`; the gallery
build's native module was 2,070,463 bytes. This gives an initial, measurable native-link delta of
about 850 KB raw. An Emscripten link with `--print-gc-sections` reported 167 unused CUI code/data
sections removed for this gallery. That confirms dead stripping works, but the counts are sections,
not functions.

The current `samples/CuiSharp.Gallery.Browser` candidate, published for `net10.0` with the matching
.NET 10.0.403 / Emscripten 3.1.56 workload, `PublishTrimmed`, and `-Oz`, produced:

| Measure | Bytes |
|---|---:|
| raw `.wasm` assets, combined | 3,503,646 |
| `.gz` assets, combined | 1,639,204 |
| `.br` sidecars, combined | 1,360,793 |
| largest gzip asset, `dotnet.native.wasm.gz` | 952,149 |
| Pages tree after dropping sidecars (with `.nojekyll`) | 3,973,466 |

The checked-in Pages workflow builds this tree, strips the redundant compression sidecars, and runs
`tools/test-browser-smoke.py` against the exact deploy tree before upload. The smoke verifies
non-background canvas pixels and exercises pointer focus, Ctrl+A, and text insertion into the real
Full name field. The site has not yet been deployed, so its own edge response headers remain
unverified.

For comparison only, the full-gallery **AOT** publish emitted 3,142,651 bytes of `.gz` assets and
2,300,270 bytes of `.br` assets. It is not a valid candidate bundle yet: scope disposal crashes at
runtime. AOT also increases transfer size substantially here, so the first Pages candidate should
use the interpreter. Revisit AOT only if end-to-end browser measurements show a real frame-time or
startup problem that is worth roughly doubling the compressed payload.

GitHub Pages was checked against an existing CUI `.wasm` asset with
`curl -I -H 'Accept-Encoding: br, gzip'`: GitHub returned `Content-Type: application/wasm` and
`Content-Encoding: gzip`. The .NET resource manifest requests the plain `.wasm` names, not `.wasm.gz`
or `.wasm.br`; Pages therefore supplies its own compression. For the deployed artifact, remove the
SDK's redundant `.gz`/`.br` sidecars after publish (saves stored/uploaded artifact bytes, not the
edge-compressed transfer) and smoke-test the deployed headers and app.

`src/CuiSharp` uses `System.Linq` only in `InputResult.ToString()` and `TextFieldPool.Clear()` for
this path. Those uses were removed; the later bundle no longer contains `System.Linq.wasm` (about
5 KB compressed in the earlier output). The C# browser app is `Microsoft.NET.Sdk.WebAssembly`
(`wasmbrowser`), not Blazor, and needs no third-party JS or managed package for the frame host.
This is the right starting point for a small bundle.

### Browser frame cost

An earlier .NET 11 RC3 interpreter probe measured only C# and software rasterization at 1240x820,
with one reused buffer, 8 warm-up frames and 80 measured frames per sample. Each sample included
`BeginFrame` → draw the real gallery page → `EndFrame` → software `Render`:

| Gallery page | `Incremental=false` mean / p95 | `Incremental=true` mean / p95 |
|---|---:|---:|
| Overview | 9.35 / 12.50 ms | 1.71 / 2.50 ms |
| More charts (index 23, page 24 of 28) | 12.11 / 16.80 ms | 3.95 / 6.00 ms |

Those isolated numbers showed damage tracking is the first performance lever, not AOT or a GPU
rewrite. The actual .NET 10 browser host now measures `BrowserHost.Frame` synchronously, including
managed/CUI work, software render, the JS import, and canvas `putImageData`; rAF intervals are
reported separately. In headless Chrome at a 1240x820 window, 180-frame windows after warm-up
measured:

| Page | steady frame mean | frame p95 | rAF interval p50 / p95 |
|---|---:|---:|---:|
| Overview | 0.76–1.04 ms | 2.0–2.7 ms | 16.7 / 16.7–16.8 ms |
| More charts (index 23) | 1.24–2.92 ms | 2.2–5.7 ms | 16.7 / 16.7–16.8 ms |

The first window was slower while the interpreter and page warmed up (up to 3.9 ms p95 on Overview
and 5.5 ms p95 on More charts). The measured rAF intervals remained at 60 Hz. Add `?perf=1` to
report timings; the normal page emits no per-frame logs.

## Implementation state and remaining work

### Done and verified locally

- Direct `LibraryImport("cui")` with a correctly named `cui.a`; no symbol bridge.
- `nuint` native-width bindings for `size_t`, `uintptr_t`, and `cui_texture`; public `ulong`
  texture/offset APIs convert at the boundary.
- A clean .NET 10.0.403 / Emscripten 3.1.56 software-only archive build. Cross-linking an archive
  from the .NET 11 Emscripten 6 pack into the .NET 10 pack caused the `wasm-opt` feature-option
  error; using the matching workload pack resolves it. `-O0` is not required.
- `samples/CuiSharp.Gallery.Browser`: real gallery sources, interpreter, `CuiSurface.Incremental`,
  one pinned RGBA framebuffer, one no-copy `ImageData` view, dirty-rectangle `putImageData`, rAF,
  and pointer/wheel/keyboard/text/paste/focus/resize input.
- `tools/test-browser-smoke.py`: serves the actual deploy tree and verifies canvas pixels plus a
  synthetic pointer → Ctrl+A → text-entry interaction. Verified locally in headless Chrome; the
  Full name field changes to “Browser Input”.
- The clean candidate is about 1.64 MB combined gzip / 1.36 MB Brotli for `_framework` WASM assets.
  The deploy tree is about 3.97 MB raw after removing redundant `.gz`/`.br` sidecars.
- End-to-end headless Chrome measurements include `BrowserHost.Frame` and the canvas update. The
  Overview and More charts pages stayed under 5.7 ms p95 after warm-up, with rAF p95 around 16.8 ms.
- `.github/workflows/pages.yml` builds on Windows with the matching .NET 10 workload, strips
  sidecars, smoke-tests the deploy tree, uploads the Pages artifact, and deploys on `main`.

### Remaining

1. Let the GitHub Actions Pages workflow run on `main`; confirm the repository's Pages source and
   deploy permissions, then inspect the deployed app under the repository subpath. Verify its real
   `_framework/*.wasm` response headers and browser console; only an existing CUI Pages asset has
   been checked for edge compression so far.
2. Keep the interpreter as the shipped configuration. AOT is optional: its scope-end function
   pointer still traps. If later profiling justifies AOT's larger payload, generate and test one
   Cdecl `[UnmanagedCallersOnly]` thunk per generated scope end and cover the hand-written callers
   in `Ui.DataGrid.cs` and `Ui.Overlays.cs` too.
3. Review gallery features that imply local files, native file pickers, or external windows. The
   browser host deliberately does not claim those desktop capabilities.

The Pages build script currently targets a Windows x64 runner so it uses the workload's own
Emscripten, Binaryen, Node, and Python pack versions. `native/CUI` remains untouched.
