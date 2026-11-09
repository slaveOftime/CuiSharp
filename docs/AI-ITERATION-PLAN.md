# Proposal: reliable, token-efficient AI iteration

Status: **P0 implemented. P1 (session tooling), P2 (hot reload) and P3/P4 (token protocol, MCP)
were cut by decision — see [AI-ITERATION-STATUS.md](AI-ITERATION-STATUS.md) for the reasoning and the
evidence. P5 remains optional and unstarted.** The text below is the proposal as reviewed, kept for
the record.

Reviewed at `b27a909`. Complements [PLAN.md](PLAN.md); preserves [ARCHITECTURE.md](ARCHITECTURE.md).

## Recommendation

Keep CuiSharp's immediate-mode C#, real software-rendered tests and Native AOT story.
Improve the loop around them:

> edit → apply/restart → render a verified new frame → inspect a small change → act/assert

First fix runtime/package reliability, then add a persistent development session that combines
`dotnet watch` with the existing headless protocol. Put MCP, CLI and compact observation on the
**same session/controller**, not separate implementations. Do not build an AI coding agent inside CUI.

## 1. Evidence, not assumptions

| Finding | Evidence / implication |
|---|---|
| One-shot commands lose app state | Benign counter probe: two separate `dotnet cui click Add --app ...` calls returned Count 1 and Count 1, with different child processes. `--stdin` returned Count 1 then Count 2 in one child. Existing streaming mode already provides useful immediate savings. |
| Hot reload can work with headless stdin | .NET SDK 10 counter probe through `dotnet watch --non-interactive`: compatible method-body edit changed the rendered heading, kept Count 1 and kept the same child process. Final readiness-gated trial took **1.23 s from edit to verified new frame**. One trial, not a latency guarantee or verification of every edit, SDK or native window mode. |
| App ready is not enough for edits | Initial probe edited immediately after `CUI-READY`; the update was missed. Waiting until the watcher reported it was watching changes made the update observable. Dev readiness must include both app/protocol readiness and watcher readiness. |
| Work is invoked inline | `Mutation.Run` calls `_work(...)` directly. A probe reported `WORK_RAN_ON_CALLER=True`; contrary to the pool-execution promise in its docs. An async method's synchronous prefix can therefore block Draw. FYM had to add Task.Run itself. |
| Disposed mutation can still apply | Run → Dispose → complete a TaskCompletionSource → draw one benign headless frame: `APPLIED_AFTER_DISPOSE=True`. Successful completion is queued without a disposed/generation check. |
| Headless observations can be stale | `HeadlessLoop` blocks on ReadLine. `tree`, `find`, `faults` and `png` inspect the last frame; async work and changed methods need another frame before observation. Fixed `frame N` is not an async completion guarantee. |
| Tool output is too coarse for large pages | Cli/HeadlessLoop return the whole printed tree after click/type/key/frame. No scoped diff, structured node values or output budget. Quiet mode removes diagnostic echo, not tree volume. |
| Native package portability failed in a consumer | CuiSharp 0.1.0's Linux .so required GLIBC_2.43 and failed on Ubuntu 24.04 (glibc 2.39). Rebuilding the pristine pinned CUI source fixed the consumer. Source-built CI is not a clean packaged-consumer compatibility test. |
| Some application workarounds should be framework APIs | FYM needed raw native interop for file dialogs and title-bar icons. Exact semantic accent customization was not exposed publicly, so the app used an exact brand-colour strip plus the preset control accent. The recommended Run/hosted API should cover these without per-app workarounds. |

Source anchors: `src/CuiSharp.Tools/{Cli,HeadlessClient,AppLocator}.cs`,
`src/CuiSharp/Hosting/{HeadlessLoop,HeadlessApp,Mutation,CuiApplication}.cs`, `CuiApp.cs`,
`src/CuiSharp/build/CuiSharp.targets`, `.github/workflows/ci.yml`.

Probe scope: isolated temporary app, synthetic counter only, CuiSharp 0.1.0 on Windows,
SDK 10. Not production data. Compatible body edits succeeded after the readiness gate;
initial missing updates are evidence of a readiness hazard, not proof hot reload is unsupported.
Human-readable watch messages vary (this SDK reports "changes applied", not "reload succeeded").
Do not use those strings as a production protocol or treat a banner as proof of new pixels.

## 2. Delivery order

### P0 — Reliability before speed

Small independent fixes, each with regression tests:

1. **Mutation contract/lifetime**
   - Honour the documented pool-execution contract, or explicitly approve another contract.
     Keep apply on the owning UI frame thread; never introduce async void or stored Ui.
   - Add disposed and invocation-generation guards. A completed-but-not-applied operation
     must not clear a newer operation or publish stale state after a restart/dispose.
   - Prove synchronous-prefix work is off-frame, apply is on-frame, dispose suppresses apply,
     cancelled work cannot resurrect a page, and repeated operations settle correctly.
   - Preserve 0 B steady-state drawing; event/operation allocations are measured separately.
2. **Packaged native compatibility**
   - Declare the minimum Linux ABI and pin the build baseline; do not silently inherit ubuntu-latest.
   - Pack actual per-RID artifacts with source revision/compiler/baseline provenance.
   - Test the produced package in a clean consumer on the oldest supported Linux image,
     both JIT and AOT, without rebuilding its native dependency or patching a global cache.
   - Resolve exact RIDs, reject unsupported/missing selected-RID archives clearly, and verify
     static linkage plus one executable. Current targets clean Windows stray files only;
     the missing-archive check examines all platforms, not only the selected one.
   - Add Linux AOT to CI. Include tools, bindings and Directory.Build.targets in CI path filters.
3. **Protocol bounds/diagnostics**
   - Bound header lines, payload sizes and retained stderr. Drain stdout/stderr concurrently;
     AppLocator currently drains them sequentially before waiting for the build process.
   - Correct command-timeout diagnostics; ReadLine currently reports readiness timeout text
     even when waiting for a command answer. Allow explicit cancellation and bounded teardown.
   - Fail on ambiguous project selection; resolve configuration/framework/RID/TargetPath
     explicitly instead of choosing the newest executable-looking DLL in Release recursively.
4. **Documentation accuracy**
   - CuiApp's class comment describes a managed loop; Run currently uses the native callback
     specifically to preserve resizing behavior. Correct the comment, not the resize design.
   - The authoring guide calls options init-properties although generated options use fields,
     and shows InputResult as old bit flags. Compile representative published examples in CI.

Gate: regression tests that reproduce the failures, normal repository verification, and clean
packaged-consumer checks. This is the first implementation checkpoint, not an MCP rewrite.

### P1 — One persistent session, usable before MCP

Proposed interface (not commands that exist today):

```text
dotnet cui dev --project <app.csproj> --configuration Debug --headless
```

- One controller owns the build/watch process and app channel. No concurrent second build
  into a Windows DLL directory being used by the app.
- Start with existing stdio/streaming; the probe proves this path is viable. Spike reconnects,
  restarts, watcher readiness and diagnostic multiplexing before choosing another transport.
  If necessary use an opt-in local named pipe / Unix socket, not a new TCP server/daemon.
- Separate the app's protocol from watcher/compiler diagnostics. Do not attach by guessing a PID.
- Identify observations by session, process generation, frame number and reload revision.
  Version the handshake and report capabilities; supported controls vary by mode/backend.
- Expose status and explicit restart. Restart resets component state by default and invalidates
  node handles; optional restoration belongs to an explicit app state service, never reflection.
- Use session-specific build outputs so dev, tests and AOT artifacts cannot overwrite each other.

Gate: one session handles 100 actions, produces verified state changes, survives a deliberate
app failure/restart, rejects stale handles and leaves no owned child processes after stop.

**Immediate workflow, available now:**

```text
dotnet cui --stdin --project <app.csproj>
click Add
click Add
frame 2
png frame.png
```

For deterministic actions this already avoids paying for a new build/startup every command.
It does not yet supply reload coordination or async waiting.

### P2 — Hot reload as a verifiable transaction

- Let **dotnet watch** compile/apply compatible C# edits; do not write a second compiler,
  custom IL patcher or component-reflection/state copier.
- Add a JIT-only metadata-update notification and safe frame-boundary refresh. Report readiness
  only after the watcher is armed and the app protocol is ready.
- A code update is complete only after a newly rendered frame carries the new reload revision.
  A compiler message, idle child, unchanged tree or successful input delivery is not sufficient.
- Keep the production native Run loop and resize callbacks. For a sleeping development window,
  spike dev-only scheduled wakeups first; cross-thread native wakeup, if needed, belongs upstream
  and requires approval under D2. Do not add continuous production redraw to make reload work.
- Compile errors retain the last good frame, explicitly tagged as stale. Incompatible edits get
  a structured restart-required status; noninteractive policy must be explicit, not silent.
- Native C changes, referenced native binaries and Native AOT builds require restart/rebuild.
  No claim that Native AOT itself supports hot reload.

Acceptance matrix: method-body edit, compatible layout/text change, compiler error + recovery,
SDK-defined rude edit + restart, dependency change, async operation during update, native change,
headless and real native window, Windows DLL lock handling. Test supported SDKs separately.
Keep a typed counter/input/page selection across compatible edits and verify both tree and pixels.

Gate: at least 20 warm compatible edits; same process and expected state retained; no stale
revision accepted. Record p50/p95 edit-to-verified-frame latency, not just compilation latency.
A provisional target is p95 <= 2 s on a named development machine, not a guarantee yet.

### P3 — Token-efficient observation, action and waiting

Prefer compact **semantic evidence**, then pixels when the task needs them.

| Operation | Proposed agent-facing behavior |
|---|---|
| observe | Scoped, paginated JSON tree. Stable session-scoped node handles, kind, label, state/value and actionable bounds. Actionable-only and shallow depth by default; explicit full-tree mode. |
| observe since frame | Delta of added/changed/removed nodes. Include base frame + generation; invalid base requires a fresh observation, not a silently wrong diff. |
| act | Typed click/type/key or bounded action batch. Preserve multiline and leading/trailing text (current line command trimming cannot do that). Return only changed scope + failed assertions by default. |
| wait | Drive frames and await a requested predicate: node/value/visibility, mutation settlement or reload revision, with deadline/cancellation. No LLM polling loop or guessed frame count. A live feed need not become globally idle. |
| capture | Optional PNG/image content for the same observed frame/revision; crop to a node/rectangle, cap dimensions and clear hover when requested. Whole-window capture stays available. |
| diagnostics | Bounded faults, compiler errors and frame stats; expanded logs only on request. No environment/command-line dumps. |

Initial output budgets (configurable targets): <= 40 visible actionable nodes, depth <= 3,
<= 8 KiB observation/diff payload, <= 20 diagnostic lines / 8 KiB. Return truncation and a
continuation cursor explicitly. Do not equate UTF-8 bytes to exact model tokens.

Reuse unchanged observations and captures, but never suppress a meaningful state change or
pretend a cached pre-reload image is new. Use separate semantic and image hashes. Animation,
live data and full-page tables need scoped observation and explicit frame identity.

Gate: compare a repeatable gallery/form workflow against today's whole-tree responses.
Target >= 70% fewer returned text bytes and >= 50% fewer model/tool round trips, with identical
asserted outcomes. These are targets to measure, not achieved figures.

### P4 — MCP and task-scoped API help on the same controller

- Build the already-planned `dotnet cui mcp` on P1/P3. One long-lived server/app, not a new
  HeadlessClient per tools/call. CLI and MCP share controller, waiting, capture and errors.
- Start with a small tool vocabulary: observe, act, wait, capture, session status/restart,
  API lookup. Avoid dozens of redundant schemas taking up the model's context.
- Honour MCP negotiation, structured errors and cancellation. Reuse existing no-dependency
  plan only with protocol-conformance tests; avoiding a package is not a reason for partial MCP.
- Images use image content blocks; shell users still receive artifact paths. Auto-capture on
  unexpected faults can be opt-in. Do not stream every frame to the model.
- Add generated, versioned task-scoped API lookup: exact signature, fields/enums, required
  usings, one compiling example and the relevant analyzer fix. Example proposal:
  `dotnet cui api Input`. Avoid forcing an agent to reread all llms.txt/options/source files.
- Put a <100-line quick-start beside the detailed authoring guide, and provide recipes for
  forms, tables, charts and async work. Keep generator-managed content generated.

Gate: scripted MCP client drives a form, waits for async success, gets a compact delta and
an image from the same frame, finds the correct API example and reports errors without crashing.
Then run one optional live agent trial; provider integration is not required by the framework.

### P5 — Remove application workarounds

Public, supported APIs for native file/folder dialogs, exact brand tokens and window icon/
window operations in the hosted Run path. Add an explicit headless dialog provider instead of
silently interpreting a missing native window as user cancellation. Keep native operations on
the correct thread; test the OS dialog boundary separately from headless pixel tests.

Defer multi-window, a general designer, a new binding DSL and built-in LLM/provider features.
CJK/font fallback and accessibility remain important but are separate native/upstream projects,
not claims that a semantic automation tree already supplies screen-reader support.

## 3. Measurement and safeguards

- Benchmark cold startup, warm actions, compatible edit, compiler-error recovery and restart.
  Record app launches, response bytes, observation/capture counts, round trips, p50/p95 latency,
  draw allocations, CPU while idle/dev/live, failures and stale observations rejected.
- Capture compiler/runtime versions, native source revision and declared ABI baseline, not
  personal paths, environment values, secrets or real customer data.
- Use isolated synthetic fixtures. UI actions can have real side effects: production data and
  destructive operations require explicit user choice; the tool cannot infer safety from a label.
- Default artifact writes stay in the selected workspace/session. Logging is bounded and local.
  Review/redact UI text/images before sharing; screenshots are not inherently non-sensitive.
- Measure AOT size/trim warnings and 0 B steady-state frames after runtime-affecting changes.
  Dev instrumentation must be opt-in and removable from production/AOT artifacts.

## 4. What to implement first

1. Mutation scheduler/lifetime regressions and fixes, plus native package baseline/consumer gates.
2. Persistent CLI session controller with readiness, generations and bounded diagnostics.
3. The hot-reload spike and observable frame revision; then wait + scoped semantic deltas.
4. MCP adapter and generated API lookup. Expand only after the measured loop is reliable.

Every runtime step still passes the repository's existing build/test/format/regen/template/AOT
and performance gates. This proposal neither marks those gates passed nor changes D1/D2/D7.
