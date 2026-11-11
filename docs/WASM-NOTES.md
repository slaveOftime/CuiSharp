# WASM investigation — running notes (worktree `CuiSharp-wasm`)

Goal: serve the gallery as a GitHub Pages bundle by running the **real C# gallery**
in the browser (Blazor WASM), with CUI compiled to wasm and linked into the .NET module.

These are live findings for the investigation branch, not settled docs.

## What is proven

1. **CUI compiles for wasm out of the box.** All 54 `src/cui_*.c` plus
   `render/cui_render_sw.c`, `render/cui_render_gl.c` and `platform/cui_dialogs.c`
   compile with the emsdk 6.0.2 toolchain that ships inside the dotnet `wasm-tools`
   workload packs (env: `DOTNET_EMSCRIPTEN_LLVM_ROOT`, `DOTNET_EMSCRIPTEN_NODE_JS`,
   `EM_CACHE` → `Microsoft.NET.Runtime.Emscripten.6.0.2.Cache` pack, `FROZEN_CACHE` on).
   No upstream patch, no shim libc (emscripten provides libc). `libcui.a` ≈ 1.6 MB,
   625 exported `cui_*` functions.
2. **The library is platform-free for our use**: `CuiSurface` drives the software
   rasterizer with pure `cui_*` calls (`cui_sw_create`, `cui_create`, `cui_begin/end_frame`,
   `cui_get_draw_data`, `cui_sw_render`) plus `cui_input_*` for input — no app/window
   layer, which is exactly the surface a browser host needs. Frames are explicit,
   matching `requestAnimationFrame`.
3. **`<NativeFileReference Include="libcui.a" />` links the archive into
   `dotnet.native.wasm`** in a Blazor WASM app (verified in the wasm-ld command line).
   GC: unreferenced archive members are dropped — exports alone do not root them.
   Working recipe to keep+export chosen symbols: a tiny `.c` glue (compiled with the
   library) that takes the address of the needed symbols, plus
   `<EmccExportedFunction Include="_name" />` — the underscore prefix is required
   (the MSBuild task does not add it; without it emcc fails with
   `undefined exported symbol`). Raw `-Wl,--export=` via `$(EmccExtraLDFlags)` also
   reaches wasm-ld.
4. **P/Invoke name resolution is the hard part.** In this runtime (11.0-rc.3 browser
   runtime + interpreter), `DllImport("cui")` throws `DllNotFoundException: cui`:
   - `runtime.c: wasm_dl_load()` only answers for library names present in the
     *generated pinvoke table*, plus `System.Globalization.Native`; everything else
     needs `dlopen`, which the browser runtime does not support (`GetMainProgramHandle()`
     throws, `NativeLibrary.Load("cui")` fails, `DllImportResolver` returning a JS-side
     address is the only other door).
   - The SDK does generate `obj/.../wasm/for-publish/pinvoke-table.h` containing an
     `__Internal_imports[]` table for the app's `DllImport("__Internal")`s — but the
     string never reaches `pinvoke.o` / the linked wasm, so the interpreter never sees
     the table. Repro: /tmp probe app, `DllNotFoundException` with both `"cui"` and
     `"__Internal"` under `RunAOTCompilation=true`.

## BREAKTHROUGH — the resolution gap is closed (verified in headless Chrome)

`PROBE-RESULT cui-wasm-ok`: a Blazor WASM (AOT) app ran `cui_sw_create` /
`cui_sw_destroy` for real and got a live native handle. The winning pattern:

1. `cui_glue.c` (shipped beside `libcui.a`, table generated from `llvm-nm` of the
   archive): a `void *cui_sym(const char *name)` that bsearches a
   linker-initialized `{cui_create, cui_begin_frame, …}[]` pointer table.
   Function addresses in wasm are the table indices mono's interpreter needs.
2. Export it with `<EmccExportedFunction Include="_cui_sym" />` (underscore!).
3. JS after boot: `getDotnetRuntime(0).Module` is the emscripten module; allocate
   the name with `_malloc` + `stringToUTF8Array`, call `_cui_sym(buf)`, free, hand
   the integer pointer to C# via `DotNet.invokeMethodAsync("app", "InitSyms", p1, p2)`
   with `[JSInvokable("InitSyms")]` (explicit name required under trim/AOT).
4. C#: `Marshal.GetDelegateForFunctionPointer<TDel>(ptr)` with
   `[UnmanagedFunctionPointer(CallingConvention.Cdecl)]` — invokes through
   `call_indirect`; verified non-trapping and mono stays alive.

Corollaries for the product:
- One JS round-trip can return **all** binding pointers (they are just ints);
  the browser binding layer is a static table of pointers + cached delegates.
- The generated `LibraryImport("cui")` shims cannot be reused in the browser —
  `tools/bindgen.cs` needs a browser emit mode (`static readonly FuncPtr`s
  resolved from `cui_sym`, delegates cached per call site). This is generator
  work in our repo (D5), not an upstream or runtime blocker.
- `cui_sym`'s 625-entry table links every member of `libcui.a` (≈ size cost;
  a per-app subset table is available via bindgen's coverage list).

Probe app recipe (all steps reproduced from this machine, 2026-11-11):
`dotnet new blazorwasm` + `NativeFileReference(libcui.a, cui_glue.c)` +
`EmccExportedFunction(_cui_sym)` + `RunAOTCompilation=true`.

## Candidate designs

| # | Design | Verdict so far |
|---|--------|----------------|
| A | C# gallery on Blazor WASM + `libcui.a` statically linked, P/Invoke direct | Direct `LibraryImport` resolution dead (finding 4); **solved via E**, proven end to end. |
| B | Same, but **AOT only** and rely on ilc-generated pinvoke table | AOT runs ("AOT'ing 41 assemblies") yet resolution still fails — same root cause. |
| C | Upstream CUI-to-wasm build (no .NET): C gallery | Off-mission: the point is C# in the browser. |
| D | WebGL2 path via upstream `web/host` + JS renderer | Nice-to-have perf upgrade after A works; software raster is enough for a gallery. |
| E | **`cui_sym` bridge**: pointer table exported by a generated glue, addresses handed JS→C#, delegates via `GetDelegateForFunctionPointer` | **PROVEN** (`cui-wasm-ok`). Cost: bindgen browser emit mode + delegate caching. |

## GitHub Pages bundle plan (regardless of A/E)

- `tools/build-cui-wasm.ps1` — emcc from the workload packs → `libcui.a` (CI: also
  doable on linux runners).
- `samples/CuiSharp.Gallery.Browser/` — Blazor WASM standalone app (`net10.0`), links
  `libcui.a` via `NativeFileReference`, hosts the gallery through a `CuiSurface`-style
  loop: rAF → `BeginFrame`/draw/`EndFrame` → `Render` into a pinned buffer →
  one JSInterop blit to `<canvas>` (damage-aware), input events → `cui_input_*`.
- `dotnet publish -p:RunAOTCompilation=true` → static bundle; `.github/workflows/pages.yml`
  publishes `wwwroot` with `actions/upload-pages-artifact` + `deploy-pages`.
