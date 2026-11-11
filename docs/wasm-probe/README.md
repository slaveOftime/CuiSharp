# The 2026-11-11 feasibility probe (kept for reproduction)

`cui-wasm-ok` was produced by a stock `dotnet new blazorwasm` app plus:

| File | Role |
|---|---|
| `cui_glue.c` | the `cui_sym` pointer table, compiled into the module via `NativeFileReference` |
| `Probe.cs` | `[JSInvokable]` entry + `GetDelegateForFunctionPointer` call of `cui_sw_create/destroy` |
| `boot-bridge.js.txt` | JS that walks `getDotnetRuntime(0).Module` and hands pointers to C# after boot |

Build: `libcui.a` from `src/cui_*.c` + `render/cui_render_sw.c` with the workload's
emsdk 6.0.2; csproj `Microsoft.NET.Sdk.BlazorWebAssembly`,
`<RunAOTCompilation>true</RunAOTCompilation>`,
`<NativeFileReference Include="libcui.a" />`, `<NativeFileReference Include="cui_glue.c" />`,
`<EmccExportedFunction Include="_cui_sym" />`. Serve `dist/wwwroot`, run headless
Chrome (`--remote-debugging-port`), read the DOM — see ../WASM-NOTES.md for the
full findings and traps (underscore in EmccExportedFunction, `__Internal` and
`dlopen` unsupported, AOT required for the ilc pinvoke pass even if unused).
