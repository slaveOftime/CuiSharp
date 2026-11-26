# CuiSharp Gallery in the browser

This is the real C# gallery running against a windowless `CuiSurface`, drawn with WebGL2 or the
software rasterizer on the .NET 11 WebAssembly interpreter. The app itself does not use Blazor
or a JS framework. Ordinary `LibraryImport("cui")` calls link directly to the Emscripten archive.

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

Install the .NET 11 SDK and its browser workloads on Windows x64, then run these commands from the
repository root. This sample targets `net11.0` and pins no SDK, so the SDK that builds it must be
the one that installed `wasm-tools`: a wasm workload belongs to the SDK feature band that installed
it, and that band's runtime and Emscripten packs (6.0.2 for 11.0) are what the app links against.
The archive script resolves the Windows workload packs; the Pages workflow uses the same Windows
x64 toolchain.

From the repository root:

```powershell
dotnet workload install wasm-tools
pwsh tools/build-cui-wasm.ps1
```

Rebuild the archive after updating the CUI submodule or `browser_platform.c`. An older
`artifacts/wasm/cui.a` does not contain the IME adapter.

Then, to develop:

```powershell
dotnet run --project samples/CuiSharp.Gallery.Browser
```

That builds the Debug app bundle and serves it; open <http://localhost:5000/>, the dev server's
default port (`--urls http://localhost:8080` picks another, Ctrl+C stops). The .NET 11 Blazor
Gateway serves the SDK's fingerprinted assets and is not part of the published site.

For hot reload, use `dotnet watch --project samples/CuiSharp.Gallery.Browser run`, or `dotnet watch run`
from the project directory. The Gateway connects the browser to `dotnet watch` and reports hot
reload as disabled for plain `dotnet run`. Without watch, restart `dotnet run` after editing
`main.js` or the runtime so the SDK regenerates fingerprinted assets and integrity hashes.

A Debug bundle is not trimmed, so it keeps the P/Invoke stubs for the desktop `cui_app_*` layer the
browser host replaces; those stay undefined at link time (`WasmAllowUndefinedSymbols`) there. Release publish keeps the strict
link that catches a genuinely missing symbol.

To reproduce what GitHub Pages serves:

```powershell
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
  The browser's editing proxy follows CUI's caret and sends only the committed composition to CUI.
  A small C adapter receives the by-value `cui_rect` and forwards only scalars into Mono. Native
  file dialogs and access to local filesystem paths are intentionally not emulated.
- The emoji picker and `ui.Emoji` use the browser's default emoji fonts via Canvas for color.
  Regular text is laid out and painted by CUI, so CSS fonts cannot fill missing glyphs. The
  browser loads a same-site 777 KB outline fallback when emoji are entered or the picker draws,
  so ordinary text fields can display basic emoji in monochrome. Joined emoji sequences do not
  necessarily form one glyph in text fields.
- The first Chinese input loads a same-site 1.82 MB OpenType fallback for common Simplified
  Chinese glyphs (GB2312), not all CJK scripts. Neither font blocks the first frame or input.

Append `?page=23` to open More charts directly. To measure the whole synchronous .NET frame plus
canvas presentation in headless Chrome, add `&perf=1` (for example `?page=23&perf=1`). It reports
180-frame mean/p50/p95 frame cost and rAF interval to the console. The normal page emits no per-frame
logs.

### Verification and font provenance

With `dotnet run` active, run `python tools/test-browser-smoke.py --url http://localhost:5000/`
from the root (`--renderer 2d` for software). For a publish, pass its `wwwroot` path in place of
`--url`. In Chrome on Windows, the test checks the focused IME proxy, composition commit,
Chinese and emoji ink in the text field, and color pixels in the emoji picker. The system
candidate panel still needs a manual OS IME check.

[`GallerySansSC-Regular.otf`](wwwroot/fonts/GallerySansSC-Regular.otf) is a renamed subset of
[`NotoSansCJKsc-Regular.otf` from Sans2.004](https://github.com/notofonts/noto-cjk/blob/Sans2.004/Sans/OTF/SimplifiedChinese/NotoSansCJKsc-Regular.otf),
under the [SIL Open Font License](wwwroot/fonts/OFL.txt). Source SHA-256:
`2c76254f6fc379fddfce0a7e84fb5385bb135d3e399294f6eeb6680d0365b74b`.
[`GalleryEmoji-Regular.ttf`](wwwroot/fonts/GalleryEmoji-Regular.ttf) is a renamed static
regular instance of [Noto Emoji 3.002](https://github.com/google/fonts/blob/6ebe737e44fd64ee736bbc6feed6136a6dbfd222/ofl/notoemoji/NotoEmoji%5Bwght%5D.ttf),
under the [SIL Open Font License](wwwroot/fonts/OFL-Emoji.txt). Source SHA-256:
`de6c18832938afc99caf132b39d6a30a19bac7f2e812e28db2535b4608d27551`.
To regenerate the fallbacks with `fonttools==4.60.1`, run from the root:

```powershell
python tools/subset-browser-font.py NotoSansCJKsc-Regular.otf samples/CuiSharp.Gallery.Browser/wwwroot/fonts/GallerySansSC-Regular.otf
python tools/subset-browser-font.py 'NotoEmoji[wght].ttf' samples/CuiSharp.Gallery.Browser/wwwroot/fonts/GalleryEmoji-Regular.ttf --emoji
```

This is a maintainer step only; builds and CI use the checked-in fonts.
