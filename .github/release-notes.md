First release of CuiSharp — native desktop UI in plain C#.

Immediate-mode components rendered by CUI (C99), bound to C# from the pinned headers by a
generator: 430 functions, 208 structs, 33 enums. `Ui` and every scope are `ref struct`s, so a
frame allocates 0 B, and NativeAOT publishes a single executable with CUI linked in.

| Package | What it is |
|---|---|
| `CuiSharp` | The library, with `native/cui.{dll,lib}` for win-x64 and `native/libcui.{dylib,a}` for osx-arm64, plus the CUI001–CUI007 analyzers |
| `CuiSharp.Testing` | Headless test harness: find a control by label, click it, type into it, assert on the frame |
| `CuiSharp.Tools` | `dotnet cui` — screenshot an app, print its controls, click or type into it |
| `CuiSharp.Templates` | `dotnet new cui-app` — an app, a test project and a solution |

### Start

```sh
dotnet new install CuiSharp.Templates
dotnet new cui-app -n MyApp && cd MyApp
dotnet run
```

### Also

```sh
dotnet add package CuiSharp              # library + analyzers + native assets
dotnet add package CuiSharp.Testing      # tests only
dotnet tool install -g CuiSharp.Tools    # the `cui` command
```

Requires the .NET 10 SDK. Supported on Windows (x64) and macOS (arm64); the Linux build of CUI
exists but is not in this package yet (`docs/PLAN.md` §4).
