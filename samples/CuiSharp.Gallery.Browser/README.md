# CuiSharp Gallery in the browser

This is the real 28-page C# gallery running against a windowless `CuiSurface`, using CUI's software
renderer and the .NET 10 WebAssembly interpreter. It does not use Blazor, a JS framework, a managed
browser package, or a generated native-function bridge. Ordinary `LibraryImport("cui")` calls link
directly to the Emscripten archive.

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

- `requestAnimationFrame` drives `CuiSurface.BeginFrame` → `GalleryApp.Frame` → `EndFrame` → software
  `Render`.
- Incremental rendering is enabled. `LastDamage` limits the canvas update to the changed rectangle.
- The RGBA framebuffer is a single long-lived pinned managed array. A synchronous `JSImport` passes
  its wasm32 address and damage rectangle; JS uses .NET's `getDotnetRuntime(0).Module.HEAPU8` to
  create `ImageData` as a typed view over the pinned buffer, avoiding a second full-frame pixel copy.
  The view is recreated if WASM memory grows. Canvas `putImageData` copies only the damaged area to
  the visible canvas.
- Pointer, wheel, keyboard, text, paste, focus, and resize events are forwarded to `CuiSurface`.
  Browser file dialogs and access to local filesystem paths are intentionally not emulated.

Append `?page=23` to open More charts directly. To measure the whole synchronous .NET frame plus
canvas presentation in headless Chrome, add `&perf=1` (for example `?page=23&perf=1`). It reports
180-frame mean/p50/p95 frame cost and rAF interval to the console. The normal page emits no per-frame
logs.
