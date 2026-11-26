---
name: cui-sync
description: Sync CuiSharp's pinned CUI submodule, review and regenerate its bindings, then verify the native build, API, gallery and release evidence. Use for a CUI bump, a new upstream API, a bindgen diagnostic or PENDING in coverage.txt.
---

# Sync CUI

Work from a clean branch. `native/CUI` is pristine upstream; never patch it or hand-edit generated files. Read [bindings.md](references/bindings.md) only for the diagnostic or mapping in front of you, and [traps.md](references/traps.md) when a test or tool misbehaves.

## 1. Identify the delta, then plan before building

1. Record `git ls-tree HEAD native/CUI` and check `git -C native/CUI status --short`. Fetch tags and the default branch from the upstream URL in `.gitmodules` (a submodule's local `origin` can be stale). Verify the remote ref, not just a local `git describe`. Use the latest release unless the request specifies a branch/commit; if the branch is ahead, report which you selected.
2. `pwsh skills/cui-sync/scripts/cui-api-diff.ps1 -From <old-pin> -To <target>` lists declarations, not changed lines. `-Commits` and `-Notes` are opt-in; quote or read only the relevant notes. A forward struct declaration is not a new layout, and a moved declaration is not a new function.
3. Check out the resolved target in the submodule, then run **`pwsh tools/regen.ps1 -Plan`**. It compares fields against `bindings/api.json` and reports unresolved bindings without replacing output or building native. Inspect the named C declarations and the native call/retention site, not whole headers, `.g.cs` files or the JSON diff. Fix root option/record errors first; a cascade of missing wrappers often has one cause.

## 2. Decide bindings, then generate once

- **One status per function.** `GENERATED` is a generated wrapper. `MANUAL` names the member and file; use `manualWidgets` only, even for helpers with no `cui_ctx`. `EXCLUDED` means no managed wrapper exists; review *new functions inside existing excluded sections* rather than treating inherited reasons as proof. `RAW-SKIP` records unsupported declarations such as variadics. A plan with any `PENDING` must exit nonzero before overwriting working bindings.
- Derive lengths from spans or arrays; never guess pointer shape or retention. Check the actual ABI (`bool` is a byte; `size_t` is native-width; C `long` varies between Windows and Unix). A skipped platform-only record must be unused by managed callers. Register anonymous enums by their **first complete C enumerator name**.
- Extend a working mapping before copying a generated wrapper into a manual file. `axisLabels` pairs CUI's axis-label callback with its own `void *user` field; generated options bind the callback through the host's frame slots. For other callbacks, verify call timing, pointer lifetime, exception containment and steady-state allocation before adopting that pattern.
- Run `-Plan` until resolved. Build native once (`pwsh native/build-native.ps1` on Windows; CI builds Linux in its baseline image) and check `native/native-provenance.txt` against the selected pin. Run `pwsh tools/regen.ps1 -NoNative -NoTest` to emit, check generator contracts, build the core, export the catalog, and build the solution **in that order**.

## 3. Review what users get

- `pwsh tools/api-surface.ps1 -Against <base-ref>`: inspect removals by owning type, including constructors and static extension forms. Preserve existing signatures when the new option can be additive. Do not approve a disappearing wrapper by editing a generator test to match hand-written source text.
- Add behavior tests for pointer/count changes and callbacks. A menu needs an observed open/action, a drop needs the actual release-frame result, a formatter needs a callback invocation or changed pixels. Prove the fixture can fail; capture function results instead of discarding them inside a draw lambda. Check 0 B across warmed draw **and paint** when native calls managed code after draw.
- Update the touched gallery page and authoring guide; snapshot just the changed pages with `pwsh tools/snapshots.ps1 -Pages <keys>` and `-Theme dark -Suffix -dark`. Check a PNG and drive one interaction separately. Don't redraw every screenshot solely for a badge. A missing heading means navigation/lookup failed until evidence says otherwise, not that the app crashed.
- Update counts in `README.md` and `bindings/README.md` from `sync-status.ps1` (raw non-variadic functions and variadic skips are distinct). Keep the build version, template default, gallery badge, changelog and generated catalog in agreement. Use the requested release version, otherwise follow the changelog policy. An `unreleased` section is intentionally rejected by the publishing script until dated.

## 4. Gate and report

Run serially against a single build/output tree. Save long output in `artifacts/`; check the command's exit code **before** filtering or tailing.

```sh
pwsh tools/regen.ps1 -NoNative -NoDiff   # contract checks, catalog freshness, warn-as-error build, full tests
dotnet format CuiSharp.slnx --verify-no-changes
pwsh tools/verify-template.ps1
dotnet publish samples/CuiSharp.Gallery -c Release -r win-x64
dotnet run -c Release --project src/CuiSharp.Benchmarks -- --fps 900  # save stdout as artifacts/fps.txt
pwsh tools/size-report.ps1 -All -Widths shipped -Csv artifacts/size.csv
pwsh tools/budget-check.ps1 -Benchmark artifacts/fps.txt -AotBytes <measured-bytes> -SizeCsv artifacts/size.csv -RequireAll
pwsh skills/cui-sync/scripts/sync-status.ps1
git diff --check
```

Report pin, statuses, intentional API removals, observed behavior, AOT bytes/IL warnings, frame allocation/time and any untested platforms. Budget increases are review decisions: show measured bytes and the actual gate unit; label causes as hypotheses unless measured. On CI failure read the assertion/stack, not the previous progress line; distinguish a passing rerun from a proven fix. Verify the final diff has no probes, native edits or unrelated screenshots before pushing.
