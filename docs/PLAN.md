# Remaining work

What is left before 1.0, in the order it should be done. Rules and decisions live in
[ARCHITECTURE.md](ARCHITECTURE.md); how the current numbers were measured is in
[PERFORMANCE.md](PERFORMANCE.md).

AI/DX review: [AI-ITERATION-PLAN.md](AI-ITERATION-PLAN.md) proposed runtime/package reliability,
session tooling, hot reload, token-efficient observations and MCP help. The reliability half is
implemented; the rest was cut by decision — see [AI-ITERATION-STATUS.md](AI-ITERATION-STATUS.md) for
what changed, the evidence, and why a test-first loop beats a stateful tool for an agent. Nothing in
it changes the architecture decisions.

Status: **everything below is unstarted unless the step says otherwise.** Phases 0–4 are complete:
the binding is generated and syncs from upstream, the runtime is allocation-free, hosting and
analyzers ship, a template generates a working app, and `dotnet cui` drives any app headless.

## Working rules

1. One step at a time. A step ends with its checkpoint passing; do not start the next one red.
2. Never hand-edit `*.g.cs` or `bindings/api.json`. Change `tools/bindgen.cs` or
   `bindings/overrides.json`, then `pwsh tools/regen.ps1`.
3. Never edit `native/CUI/`. See D2: the native library is pristine upstream.
4. After a perf-relevant change, re-run the benchmark and update the budget table below.
5. Gates, all of them, before calling a step done:

```sh
dotnet build CuiSharp.slnx -c Release -warnaserror
dotnet test  CuiSharp.slnx -c Release
dotnet format CuiSharp.slnx --verify-no-changes
pwsh tools/regen.ps1 -NoNative -NoDiff      # generator self-checks + no stale output
pwsh tools/verify-template.ps1              # dotnet new -> build -> test -> AOT -> dotnet cui
dotnet publish samples/CuiSharp.Gallery -c Release -r win-x64   # zero IL2xxx/IL3xxx
dotnet run -c Release --project src/CuiSharp.Benchmarks -- --fps 900
```

6. Close a running app before rebuilding on Windows: `cui.dll` is locked while loaded.
7. If this plan turns out to be wrong, stop, write the finding down where the work happened, and ask.

## 1. `dotnet cui mcp` — the agent bridge (next)

The loop, the client and the tree exist; this exposes them over MCP so an agent can drive a real
app without a shell.

- `dotnet cui mcp` speaks newline-delimited JSON-RPC on stdio: `initialize`, `tools/list`,
  `tools/call`. Tools: `tree`, `find`, `click`, `type`, `key`, `move`, `frame`, `faults`, `png`,
  `restart`.
- One long-lived app process per server, restarted on demand, so a session is one build+startup.
- `png` answers with an image content block, not a path, so a model can look at the frame it is
  being asked about.
- Reuse `HeadlessClient`; do not add an MCP dependency. The protocol is small and having no
  package keeps the tool's AOT-friendly shape.

Checkpoint: `claude`/`codex` (or a JSON-RPC script) lists the tools, clicks a control in the
gallery, and receives the frame tree and a PNG; a command that cannot be obeyed returns an error
result, not a crash.

## 2. `cui-script`: the single-file story

`docs/PLAN.md`'s DX bar has promised a one-file app since the beginning; the template covers the
solution case only.

- `dotnet new cui-script -n hello` produces one `app.cs` with `#:package CuiSharp@*` and a
  `dotnet run app.cs` that opens a window.
- Prove it in `verify-template.ps1` (build + `dotnet cui tree` against the file-based app).

Checkpoint: a fresh file-based app runs, publishes AOT, and is drivable by `dotnet cui`.

## 3. Port the thirteen extended gallery pages

`examples/gallery_more.c` is the coverage proof nothing else gives: the pages exercise components
our tests do not. Nine of the eleven gallery pages are ported; the extended set is not.

- One page per commit-sized slice, one file per page, in the existing style.
- Any wrapper that turns out missing is a generator or `overrides.json` fix, not a hand edit.

Checkpoint: every page renders, `dotnet cui tree` finds its headings, and no `PENDING` appears in
`bindings/coverage.txt`.

## 4. Multi-RID native builds and packages

Every RID below is built on its own runner, and the release packs all of them into one package.

| RID | Native build | Notes |
|---|---|---|
| `win-x64` | done | `native/build-native.ps1` |
| `linux-x64` | done | `native/build-native.sh` on an ubuntu runner (x11 backend, needs `libx11-dev`); CUI dlopens GL, so no GL headers are needed |
| `osx-arm64` | done | `native/build-native.sh` on a macos runner (Cocoa backend) |
| `win-arm64` | not started | cross-compile check |
| `linux-arm64` | not started | |

- `CuiSharp.targets` picks the static archive per RID (win-x64 `cui.lib`, linux-x64 and osx-arm64
  `libcui.a`) and the platform's linker arguments.
- The release downloads each platform's artifacts into `artifacts/native/<rid>/` and passes
  `-p:CuiNativeWinDir`/`-p:CuiNativeLinuxDir`/`-p:CuiNativeOsxDir`. They cannot share one directory:
  Linux and macOS both build `libcui.a`, and one would silently overwrite the other.

Checkpoint: `dotnet publish -r <rid>` for each supported RID produces a single executable that opens
the gallery, on that platform, from a clean machine.

## 5. CI

`.github/workflows/ci.yml` runs on every pull request and on `main`: build `-warnaserror` on Windows,
Linux and macOS with the full test suite on all three (each leg builds CUI for its own backend; the
suite draws real frames through the software renderer, so no display is involved), the template
round trip (pack, scaffold, build) on all three, a NativeAOT publish per platform (win-x64,
osx-arm64) that asserts the executable is self-contained and then renders a frame headlessly, the
benchmark suites on Windows and macOS, and regen-no-diff against the submodule headers.

What it does not do yet, and should:

- **`dotnet format --verify-no-changes`**, the other gate that catches a formatting drift.
- **A NativeAOT publish for `linux-x64`** in CI, so the x11 linker arguments are covered too.
- **Frame time against a stored baseline** (`--fps 900` mean, tolerance 10 %) and the AOT size
  budget, so a perf or size regression fails a pull request instead of a release.
- **`docs/API-SURFACE.txt` as a required review artifact**: the diff should be posted on the pull
  request that changes it.
- Cache the native build keyed on the submodule SHA.

Checkpoint: a pull request that changes a wrapper without regenerating, that regresses frame time by
more than 10 %, or that grows the AOT binary past its budget fails.

## 6. Then: 1.0

See [RELEASING.md](RELEASING.md) for the mechanics. What 1.0 additionally needs:

- Analyzer code fixes for `CUI001`–`CUI007` (diagnostics and messages exist; no fixes yet).
- The public surface reviewed once as a whole — `docs/API-SURFACE.txt` read top to bottom — because
  after 1.0 it is a promise.
- `skills/cui-dotnet/SKILL.md` and `llms.txt` regenerated from the final surface, and the gallery
  snapshots retaken (`pwsh tools/snapshots.ps1`).

## Budgets

Measured on Windows 11, .NET SDK 11.0.100-rc.3 / runtime 10.0. Re-measure per release; the shape
of the curves matters more than the digits.

| Metric | Current | Budget |
|---|---|---|
| Gallery AOT exe (win-x64) | 2 073 088 B | ≤ 2.1 MB |
| Template AOT exe (win-x64) | 2 555 392 B | ≤ 2.6 MB |
| Steady-state managed allocation per frame | 0 B | 0 B |
| Live chart, `--fps 900` | mean 0.85 ms, p50 0.71, p95 1.20, p99 1.52, 0 B/frame | ≤ baseline + 10 % |
| Idle CPU | 0 % | 0 % |
| AOT/trim warnings | 0 | 0 |
| Binding coverage | 356 generated, 156 manual, 4 excluded, 0 pending (of 459 C functions) | 0 pending |

## Known gaps (tracked, not planned)

- **Accessibility**: CUI publishes no accessibility tree, so there is no UIA/AT-SPI/NSAccessibility.
  The semantic tree is the foundation to build on, not a substitute.
- **One window per app**: CUI creates one window; multi-window is an upstream question.
- **IME coverage** beyond what upstream CUI implements.
- **macOS signing and notarization** for a distributed `.app`.
