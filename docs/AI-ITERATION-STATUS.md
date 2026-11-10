# AI iteration — checkpoint status

What of [AI-ITERATION-PLAN.md](AI-ITERATION-PLAN.md) is implemented, what the evidence is, and what
was cut and why. The plan itself is unchanged in intent; this file is the record of what happened to
it.

Branch: `ai-iteration`. Reviewed from `b27a909`.

## Verdict

The plan's **reliability half** was worth doing and is done. Its **ergonomics half** — a stateful
session tool, hot reload inside the tool, a token-efficient delta protocol, an MCP server — was
built far enough to see what it costs, then cut. An agent's best loop here is a test it can run
again, not a process it keeps alive.

## P0 — reliability: **done**

Each item has a behaviour test that failed first, and the gates below all pass.

| # | Plan item | What changed | Evidence |
|---|---|---|---|
| 0.1 | Mutation contract / lifetime | `Hosting/Mutation.cs`: the work runs on the pool (`Task.Run`) instead of inline in `Draw`; every run has a generation; a disposed or superseded run cannot apply, cannot publish an error, and cannot settle a newer run's pending-work count | `HostingTests`: `TheWorksSynchronousPrefixRunsOffTheFrameThread`, `ASlowSynchronousPrefixInTheWorkDoesNotHoldUpRun`, `ADisposedMutationCannotApplyItsResult`, `ACancelledWorkCannotApplyItsResult`, `ACompletedOperationDoesNotOverwriteANewerOne`, `RepeatedOperationsSettlePendingWorkExactly` |
| 0.2 | Packaged native compatibility | `build/CuiSharp.targets` matches the RID exactly, errors for an unsupported RID and for a missing archive, and removes stray native files on every platform; `native/build-native.sh` refuses a `libcui.so` needing more than `CUI_GLIBC_BASELINE` (2.35) and writes revision/compiler/ABI/sha256 provenance; the package carries `runtimes/native-provenance.txt`; CI pins Linux native builds to `ubuntu-22.04`, adds a Linux AOT leg, a `packaged-consumer` job and a format gate; `tools/verify-template.ps1` is host-RID generic and asserts no native library is left beside the AOT executable. Declared baseline: [NATIVE-ABI.md](NATIVE-ABI.md) | `dotnet pack` → provenance in the nupkg; a consumer project: unsupported `linux-arm64` and missing-archive publishes both fail with the intended message, `win-x64` AOT publish leaves `app.exe` + `app.pdb` only, JIT `CUI-READY 1080 720` + `tree` from the package's own native library; `verify-template.ps1` passes |
| 0.3 | Protocol bounds / diagnostics | `HeadlessClient`: 16 MiB payload cap, 4 KiB line cap, bounded stderr tail, a header cut short is an error instead of a desync, timeouts say which wait expired, `CancellationToken` support; `HeadlessLoop`: 64 KiB command bound, 16 MiB answer bound answered as `CUI-ERR`; `AppLocator`: drains both pipes concurrently, refuses to guess between app projects, resolves `TargetPath` from MSBuild evaluation (`-getProperty`) instead of the newest DLL | `ProtocolBoundsTests` (9), `ToolResolutionTests` (5) |
| 0.4 | Documentation accuracy | `CuiApp`'s class comment now describes the native callback and why the window owns the loop; `SKILL.md` and `README.md` say options are public fields, the input example uses `InputResult.Changed`; the guide's custom-control example passes `float[]` (a paint lambda cannot capture a `Span`) — the guide did not compile before; the guide now opens its working section with the test driver and puts `dotnet cui` after it | `UiCompilerTests.TheGuidesExamplesCompile` compiles the guide's own snippets out of `SKILL.md`; `DocumentationDriftTests.TheGuideDescribesOptionsAndInputResultsAsTheyAre`, `TheGuidePutsTheTestDriverBeforeTheOneShotTool` |

## Cut by decision

**P1 session tooling, P2 hot reload, P3 token protocol, P4 MCP.** A working `dotnet cui dev` was
implemented and removed again; `SessionController` and its tests are gone, and the tool's surface is
what it was, plus the P0 fixes.

Why:

- **A test beats a session for an agent.** `CuiSharp.Testing` draws frames on demand: a click is
  deterministic, a check costs milliseconds, state can be set directly instead of clicked into
  existence, and the reproduction survives the task. A session leaves a shell history nobody reruns.
- **State preservation is the only thing a session adds, and it is rarely worth it.** The one
  command that needs it — explore an app you have not read — is already `dotnet cui --stdin`: one
  build, one startup, many answers, no daemon to supervise.
- **Hot reload in the tool is fragile by construction.** It means multiplexing a watcher's output,
  delta-application events, rude-edit detection and the headless protocol on one pipe, across SDK
  versions, to save a rebuild an agent does not experience as waiting. `dotnet watch` already does
  it for the human at the keyboard, in a real window, where it matters.
- **The tool must stay a relay.** It builds, starts the app headless, sends one command, prints one
  answer, exits. Every feature that wanted a daemon, a handle table, a delta protocol or a node
  identity was paying for a state model the framework does not need — and would have become a second
  thing to keep correct. This is now written into [ARCHITECTURE.md](ARCHITECTURE.md) so it is not
  re-litigated.

Unattended workflow this leaves: edit → `dotnet test` → look at a PNG when the question is visual
(`app.SavePng` in a test, or `dotnet cui png`). Human-in-the-loop: `dotnet watch` in one window,
edit in the other. Both are documented in the guide's "How to work on a UI".

## Second pass: an app's feedback, five checkpoints

An external Native-AOT app reported what building on 0.1.0 cost it. The full triage is
[FEEDBACK-TRIAGE.md](FEEDBACK-TRIAGE.md); what shipped from it:

| Checkpoint | Result |
|---|---|
| **Window resize** (maintainer's own bug) | A drag cost two frames and two display waits per mouse step. `Hosting/LiveResize` subclasses CUI's window procedure instead of patching it: **2180 ms → 1444 ms** for the same 20-step script against the real window, one frame per step, and vsync dropped for the drag. |
| **Lifecycle and OS integration** | `OnInitialized` no longer runs before the window (`CuiApplication.Run` stopped initializing eagerly, which contradicted `Component`'s own doc). New `CuiWindow` — handle, backend, renderer, title, minimize/maximize/close — on `CuiApp.Window`, `CuiApplication.Window` and `CuiAppOptions.WindowCreated`. Native load failures explain themselves (`CuiSharpNativeException`). |
| **Headless ergonomics** | `Advance(TimeSpan, frames)`, `Settle()`, `Animating`, `ParkPointer()`, pointer-free `SavePng`, `FindById`, `FindItem(id, index)`, `id:` queries, `SemanticWidget.Id`. |
| **Fonts** | `UseSystemFontFallback("中文")` probes the faces each OS ships and registers the first that *paints* the sample — advance cannot answer it, a `.notdef` has one too. The fallback chain, global scan and atlas limit are documented. |
| **Docs** | The package now carries `docs/llms.txt`, `docs/SKILL.md`, `docs/NATIVE-ABI.md` and CUI's own 2 065-line component documentation — CuiSharp shipped none of it before. Header and definition comments now reach the `.g.cs`; the reference lists the hand-written members it used to omit. |
| **Analyzer** | `CUI001` also reports `File.*`, `Directory.*` and `Task.Run` inside a frame. |

Tests went from 435 to 452. Budgets held: gallery AOT 2 134 016 B (≤ 2.1 MB), template 2 582 016 B
(≤ 2.6 MB), benchmark mean 0.82 ms with 0 B per frame.

## Open, optional

**P5 public APIs** — native file/folder dialogs, exact brand tokens, window icon operations — is
still unimplemented, only worth doing when an app needs it. Nothing else from the plan is pending.

## Gates run for this checkpoint

```sh
dotnet build CuiSharp.slnx -c Release -warnaserror      # passed
dotnet test  CuiSharp.slnx -c Release                   # passed (452 + 28; was 412 + 28)
dotnet format CuiSharp.slnx --verify-no-changes         # passed
pwsh tools/regen.ps1 -NoNative -NoDiff                  # passed, no generated drift
pwsh tools/verify-template.ps1                          # passed (pack → JIT tests → AOT → dotnet cui)
dotnet publish samples/CuiSharp.Gallery -r win-x64      # zero IL2xxx/IL3xxx
dotnet run … --fps 900                                  # mean 0.82 ms, p95 1.12, 0 B/frame
```

Not run locally, covered only by CI on their own platforms: the Linux/macOS native builds, the
Linux AOT publish, and the `ubuntu-22.04` packaged-consumer leg. The glibc-baseline check in
`build-native.sh` has not executed on Linux yet.
