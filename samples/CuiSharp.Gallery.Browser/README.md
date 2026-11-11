# CuiSharp Gallery in the browser

This is the real 28-page C# gallery running against a windowless `CuiSurface`, drawn with a WebGL2
renderer on the .NET 10 WebAssembly interpreter. It does not use Blazor, a JS framework, a managed
browser package, or a generated native-function bridge. Ordinary `LibraryImport("cui")` calls link
directly to the Emscripten archive.

## Renderers

The page defaults to **WebGL2** and falls back to the **software** rasterizer where WebGL2 is
missing or fails to initialize; `?renderer=software` forces the fallback. Both paths run the same
C# frame:

- **WebGL2** (`main.js`'s `GLRenderer`, a port of CUI's own `web/site/js/gl.js`, which is a port of
  `src/render/cui_render_gl.c`): the surface is created with `CuiSurface.CreateGpu`, no CPU
  rasterization happens, and after `EndFrame` the host hands the `cui_draw_data` pointer to JS. The
  GL renderer reads vertices (64 B), indices and commands (28 B on wasm32) straight out of the wasm
  heap and draws with the same shaders, blending and scissoring as the desktop GL renderer. CUI
  uploads its font atlas — and any image texture — through texture callbacks that are
  `[UnmanagedCallersOnly]` thunks calling `textures.create/update/destroy` in `main.js`, the same
  arrangement as CUI's `web/host/web_host.c`. One full-frame GPU redraw per frame; there is no
  damage rectangle on this path.
- **Software**: `CuiSurface.Create` + `Render` into a pinned RGBA buffer, presented as an
  `ImageData` view over `getDotnetRuntime(0).Module.HEAPU8` with dirty-rectangle `putImageData`.

Headless Chrome under `--disable-gpu` (what the CI smoke test uses) renders WebGL2 through
SwiftShader, so the GPU path is exercised in CI. Measured there, GPU frame cost matches or beats the
software path — on the chart-heavy page 23, p95 1.8 ms vs 4.7 ms, because the shader does the shadow
blur the software renderer spends CPU on.

## Build and run

Install .NET SDK 10.0.403 or a later .NET 10 feature band, plus its browser workloads, on Windows
x64; then run these commands from the repository root.
The archive script currently resolves the Windows workload packs; the Pages workflow uses the same
Windows x64 toolchain.

```powershell
Push-Location samples/CuiSharp.Gallery.Browser
dotnet workload install wasm-tools wasm-experimental
Pop-Location
pwsh tools/build-cui-wasm.ps1
Push-Location samples/CuiSharp.Gallery.Browser
Remove-Item -Recurse -Force ../../artifacts/browser -ErrorAction SilentlyContinue
dotnet publish CuiSharp.Gallery.Browser.csproj -c Release -o ../../artifacts/browser
Pop-Location
python -m http.server 8080 --directory artifacts/browser/wwwroot
```

Open `http://localhost:8080/`. A static HTTP server is required; `file://` is not supported. The
native build writes only to `artifacts/wasm/` and never edits the pinned `native/CUI` submodule.

The native archive must be named **`cui.a`**, not `libcui.a`. The WASM SDK derives the import module
name from the archive basename; `NativeFileReference` for `libcui.a` registers `libcui`, while
CuiSharp imports `cui`. `tools/build-cui-wasm.ps1` selects the Emscripten pack matching the .NET SDK
band to keep clang, wasm-ld and wasm-opt aligned.

## Host design

- `requestAnimationFrame` drives `CuiSurface.BeginFrame` → `GalleryApp.Frame` → `EndFrame`, then
  presents: `presentGpu` (WebGL2, draw data pointer) or software `Render` + damage rectangle.
- The page chooses the renderer before the first frame (`host.Initialize(useGpu)`); the smoke test
  and screenshots read the choice back from `document.documentElement.dataset.renderer` instead of
  calling `getContext` themselves — on a context-less canvas that would create a 2d context and
  lock the canvas out of webgl2.
- Pointer, wheel, keyboard, text, paste, focus, and resize events are forwarded to `CuiSurface`.
  Browser file dialogs and access to local filesystem paths are intentionally not emulated.

Append `?page=23` to open More charts directly. To measure the whole synchronous .NET frame plus
canvas presentation in headless Chrome, add `&perf=1` (for example `?page=23&perf=1`). It reports
180-frame mean/p50/p95 frame cost and rAF interval to the console. The normal page emits no per-frame
logs.
