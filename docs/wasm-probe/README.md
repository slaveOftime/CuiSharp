# Direct P/Invoke feasibility probe

This replaces the earlier `cui_sym`/JS-delegate experiment. That bridge is not needed.

## Reproduce

1. Install the `wasm-tools` and `wasm-experimental` workloads for a .NET SDK with the browser
   template (`wasmbrowser`).
2. Create the stock `dotnet new wasmbrowser` project. Link `Program.cs` from this directory, add a
   project reference to `src/CuiSharp/CuiSharp.csproj`, and add the Emscripten-built software
   archive as:

   ```xml
   <NativeFileReference Include="path/to/libcui.a" />
   ```

3. Build the archive with the Emscripten version paired with that SDK's browser runtime pack. Do not
   hand-build or patch the pinned `native/CUI` submodule.
4. Keep the stock browser entry point, which calls `dotnet.create()` and `runMain()`. Publish, serve
   `wwwroot` over HTTP, and check the browser console for `CUI-DIRECT-PINVOKE-OK`.

`Program.cs` intentionally avoids scopes: it proves that normal `CuiSharp` `LibraryImport("cui")`
methods link and call directly, while scope pointer dispatch has its own AOT issue. For the
reproduced full-gallery and scope results, plus measured bundle sizes, see
[WASM-NOTES.md](../WASM-NOTES.md).

## Expected evidence

The publish-generated `obj/.../wasm/for-publish/pinvoke-table.h` has the CUI imports registered
under `"cui"`; `NativeFileReference` supplies the implementations. `CuiSurface.Create`, direct
widgets, `EndFrame`, and `Render` execute in Chrome. No `cui_sym`, `DllImportResolver`, Blazor,
`JSExport`, or bridge C file is involved.
