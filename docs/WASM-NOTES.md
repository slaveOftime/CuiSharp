# WASM investigation — running notes (`investigate/wasm-github-pages`)

Goal: serve the **real C# gallery** in a browser on GitHub Pages. This is an investigation log,
not settled product documentation.

## Current verdict

**The managed-to-native bridge is not the blocker.** A stock .NET browser app can call CUI directly
through the SDK-generated `LibraryImport("cui")` P/Invoke table when `libcui.a` is a
`NativeFileReference`. No `cui_sym`, generated delegate table, Blazor, JS interop, or C# binding
mode is needed for ordinary CUI calls.

**The actual AOT blocker is `LayoutScope`/`OpenScope`.** Their disposal stores a function pointer to
a generated `LibraryImport` method and calls it indirectly. Under the browser AOT runtime that
pointer is null; the simple `Column` scope traps on disposal, after its widgets already drew. The
same simple scope works under the interpreter. A one-end-function `[UnmanagedCallersOnly]` thunk
prototype fixed it in .NET 10 AOT, with no CUI or application changes. A generated per-end thunk
looks like the smallest route that preserves the existing per-scope linker reachability rule; it
still needs a full validation before adoption.

## What is verified

- **CUI builds from pristine C sources.** The .NET 11 RC workload's Emscripten 6.0.2 compiled the
  CUI core, software renderer, GL renderer, and dialog source. The archive is about 1.6 MB before
  link-time dead stripping. No `native/CUI` edits or custom libc are needed. A software-only
  archive built with `-Oz`, function/data sections, and no GL renderer was about 1.4 MB.
- **CUI runs in the browser.** `NativeFileReference(libcui.a)` causes the WASM SDK to generate the
  `cui` entries in `pinvoke-table.h`; direct calls to `CuiSurface.Create`, draw functions,
  `EndFrame`, and `Render` work in headless Chrome. I reproduced this with both .NET 10 and .NET 11
  workloads. The .NET 10 AOT test ran using its Emscripten 3.1.56 pack with `-O0` to work around a
  local `wasm-opt` option mismatch; the optimized .NET 10 publish still needs a clean CI check.
- **The full gallery draws under both interpreters.** A standalone browser app linked the current
  gallery sources, selected each of its 28 pages, drew each into a `CuiSurface`, then rasterized the
  last frame. Chrome reported `GALLERY-28-PAGES-OK faults=0 cmds=13 nonBackground=38092` under the
  .NET 11 RC3 `-Oz` build. The same test passed under .NET 10.0.14 with `-O0` (the local optimized
  toolchain issue below). This is a C# + CUI render proof, not yet a canvas host or an AOT-ready
  Pages bundle.
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

The earlier `WASM-NOTES.md` claim that direct P/Invoke resolution was broken, and the committed
`cui_sym` bridge probe, were false conclusions from an incomplete probe. They are superseded here;
the bridge artifacts have been removed from `docs/wasm-probe/` and replaced with the direct-call
reproduction.

## Size and dependency evidence

A **non-AOT** .NET 11 browser app using the current 28-page gallery, `CuiSharp`, invariant
culture/timezone, the size profile, the software-only CUI archive, and Release `-Oz` produced:

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
not functions; preserve the map/GC output in the final size gate before claiming any finer-grained
widget savings.

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

### Browser frame cost (before canvas presentation)

On the .NET 11 RC3 interpreter, headless Chrome, 1240x820, one reused pixel buffer, 8 warm-up frames
and 80 measured frames, each sample included `BeginFrame` → draw the real gallery page → `EndFrame`
→ software `Render`:

| Gallery page | `Incremental=false` mean / p95 | `Incremental=true` mean / p95 |
|---|---:|---:|
| Overview | 9.35 / 12.50 ms | 1.71 / 2.50 ms |
| More charts (index 23, page 24 of 28) | 12.11 / 16.80 ms | 3.95 / 6.00 ms |

Damage tracking is therefore the first performance lever, not AOT or a GPU rewrite. These are not
full host numbers: they exclude requestAnimationFrame scheduling, JavaScript interop, and
`putImageData`. The browser host must measure those too. The buffer was reused, but no DOM canvas
was presented in this probe.

## First-principles implementation plan

1. **Keep direct P/Invoke and the size fixes already in this worktree.** Link a locally built,
   software-only `libcui.a` through `NativeFileReference`; let the WASM SDK provide the import table.
   Keep JS out of the CUI call path. `size_t`/`uintptr_t`/`cui_texture` use `nuint` in native
   signatures (the wasm32 ABI is 32-bit), while the friendly image/offset API keeps its intended
   `ulong` surface and converts at the boundary. The generator self-test pins that mapping.
2. **Start with the interpreter, not AOT.** Real-gallery frames with incremental raster already fit
   a 60 Hz budget before canvas presentation, and the current compressed artifact is about half the
   AOT test bundle. This avoids both the scope-pointer AOT trap and unnecessary code size. AOT is an
   optional follow-up, not a goal in itself.
3. **Build a browser host with no framework dependency.** Add
   `samples/CuiSharp.Gallery.Browser` using the stock `wasmbrowser` template and link the existing
   gallery source. A single `requestAnimationFrame` loop drives `CuiSurface.BeginFrame`, the gallery
   draw, `EndFrame`, and software render. Start with one persistent pinned RGBA buffer and one
   synchronous JS canvas present per frame; forward pointer, keyboard, text, scroll, and resize
   input to CUI. Then profile before considering WebGL.
4. **Make the pixels cheap and prove input.** Use `CuiSurface.Incremental` from the start and
   `LastDamage` for a dirty-rectangle canvas update if it is correct. Do not send pixels through JSON,
   allocate an `ImageData` every frame, or copy the full buffer unnecessarily. Measure the actual
   1240x820 `requestAnimationFrame` loop including the JS/Canvas leg; add a visual/pixel assertion
   and input interaction smoke. Consider WebGL2 only if the measured software path misses target.
5. **Keep an AOT escape hatch.** If browser measurements demand AOT, generate one Cdecl
   `[UnmanagedCallersOnly]` thunk per scope-ending import and pass that thunk from the wrapper
   (plus the two hand-written scope callers in `Ui.DataGrid.cs` and `Ui.Overlays.cs`); its
   body directly calls `Native.<End>(ui)`. The `BoxEnd` prototype works and preserves a statically
   visible, per-end path. Test every generated scope, callbacks, and all pages; compare payload and
   frame time before choosing AOT. Avoid one global switch naming all endings unless its link-size
   cost is measured and accepted.
6. **Budget and deploy.** Fix/verify optimized .NET 10 workload behavior on the intended runner,
   capture raw size and actual GitHub Pages transfer headers, strip unused `.gz`/`.br` sidecars,
   emit only the deploy tree, then wire `actions/upload-pages-artifact` and
   `actions/deploy-pages`. Keep `native/CUI` pristine.

## Current risks / unverified

- .NET 10 is the library's supported target. This host's .NET 10.0.14 WebAssembly workload has a
  local `wasm-opt` mismatch: default Release `-Oz` fails with `Unknown option --enable-bulk-memory-opt`,
  while `-O0` publishes and works. The .NET 11 RC3 `-Oz` path works. Verify optimized .NET 10 on the
  intended GitHub runner rather than silently shipping `-O0`.
- The C# renderer has a Chrome frame-time measurement, but no actual HTML canvas presentation or
  `requestAnimationFrame` benchmark exists yet. The 0.83 ms native Windows software-raster figure in
  `docs/PERFORMANCE.md` is not a browser/WASM measurement.
- Gallery controls that model local files, OS dialogs, or external windows need a browser-specific
  behavior review. `CuiSurface` already avoids the desktop window/app layer; it does not make the
  browser have a local filesystem or native file picker.
