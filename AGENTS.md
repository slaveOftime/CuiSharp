# Agent notes for CuiSharp

CuiSharp binds CUI (C99) to C#. Read these three before changing code, in this order:

| File | Answers |
|---|---|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | the settled decisions and the rules that must not break |
| [docs/PLAN.md](docs/PLAN.md) | what is left to do, and the gates a step must pass |
| [skills/cui-dotnet/SKILL.md](skills/cui-dotnet/SKILL.md) | how the API is meant to be used, pitfalls included |

`docs/ARCHITECTURE.md` is why things are the way they are; read an entry when you are about to change
something that looks odd.

## Hard rules

- **Never hand-edit generated files**: `src/CuiSharp/Interop/*.g.cs`, `src/CuiSharp/Ui/*.g.cs`,
  `src/CuiSharp/Icons.g.cs`, `src/CuiSharp.Tests/*.g.cs`, `skills/cui-dotnet/{llms.txt,api/*.md}`,
  `bindings/{api.json,coverage.txt}`, and the bundle's `widgets*.md`, `hosting.md`, `native-abi.md`
  (copies). Change `tools/bindgen.cs`, `bindings/overrides.json` or the source page, then
  `pwsh tools/regen.ps1`.
- **Never edit `native/CUI/`** — it is a pinned upstream submodule, and `native/patches/` is empty on
  purpose. Native behaviour belongs upstream; if you think you need a patch, stop and ask.
- **`Ui` and every scope are `ref struct`.** No fields, no capture, no boxing, no `await`. A frame
  allocates 0 B in steady state, and that is a test, not an aspiration.
- **One API per task.** If a call site needs a workaround, the fix belongs in the framework, not in
  the app, the template, or the docs.
- **No useless tests.** A test must be able to fail: assert on behaviour or on observable output,
  not on the shape of your own code.
- **`EXCLUDED` in `coverage.txt` means nothing wraps the function.** Anything exposed by hand is
  `MANUAL` and names the member and the file.
- **Do not commit secrets or PII.** No tokens, keys, personal email or internal paths in code,
  comments, commit messages or fixtures.

## Verify before saying a step is done

```sh
dotnet build CuiSharp.slnx -c Release -warnaserror
dotnet test  CuiSharp.slnx -c Release
dotnet format CuiSharp.slnx --verify-no-changes
pwsh tools/regen.ps1 -NoNative -NoDiff      # generated output is fresh, 13 generator self-checks
pwsh tools/verify-template.ps1              # dotnet new -> build -> test -> AOT -> dotnet cui
```

Performance-relevant work also needs:

```sh
dotnet publish samples/CuiSharp.Gallery -c Release -r win-x64     # zero IL2xxx/IL3xxx warnings
dotnet run -c Release --project src/CuiSharp.Benchmarks -- --fps 900
```

On Windows, close a running app before rebuilding: `cui.dll` is locked while it is loaded.

## Where things live

| Task | Files |
|---|---|
| a C function gains a wrapper | `bindings/overrides.json` (or nothing — the generator does it), then regenerate |
| a hand-written control | `src/CuiSharp/Ui/Ui.*.cs` plus `WidgetRecorder.Note(...)`, and a `MANUAL` line in `overrides.json` |
| the frame loop, window, headless mode | `CuiApp.cs`, `CuiSurface.cs`, `Hosting/HeadlessLoop.cs` |
| what a frame records | `Semantics/WidgetRecorder.cs`, `Semantics/SemanticNode.cs`, `Semantics/WidgetQuery.cs` |
| `dotnet cui` | `src/CuiSharp.Tools/` |
| a new analyzer | `src/CuiSharp.Analyzers/`, one file per rule, plus a case in `AnalyzerTests` |
| the gallery | `samples/CuiSharp.Gallery/Pages/*.cs`, one page per file |
| the template | `src/CuiSharp.Templates/CuiSharp.App/` |
| docs a user reads | `README.md`, the bundle `skills/cui-dotnet/` (guide, reference, API pages, widget docs), `docs/ARCHITECTURE.md` |

## Style

Match the file you are in. Generated wrappers carry one line of header documentation; hand-written
code explains *why* in a comment, not *what*. Tests are named for the behaviour they pin
(`AnExactLabelBeatsAHeadingThatRepeatsIt`), not for the method they call.
