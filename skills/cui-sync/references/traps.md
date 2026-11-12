# Traps

Each of these cost someone an hour, and none of them are visible from the code they break. Read this
when something is odd, or before debugging anything you are about to blame on the generator.

## The generator told the truth and the wrapper lied

- **A new widget that edits an array keeps the read-only mapping.** If the count is adjacent and by
  value, the default read-only array wins over the in/out mapping unless the entry is checked first —
  and a toggle that changes nothing is that bug. `inOutArrays` must be consulted before
  `IsAdjacentCount`.
- **`ref int count` with a by-value write-back is dead code.** A widget that adds or removes entries
  must hand the new length back; the generator once emitted `count = __count;` on a parameter it had
  passed by value, which did nothing and read like it did.
- **A pointer plural that is one number.** `float *pos`, `float *progress` end in an `s` and are a
  single scalar the widget moves. `Parameter()` narrows the plural heuristic to non-const pointers for
  exactly this, so a new one needs nothing — but if it arrives as `ref float` and the C writes through
  it, the wrapper is hiding a bug rather than causing one.

## C ergonomics that look like binding bugs

- **Multi-line declarations** (`cui_waterfall_ex`) and **`#define` macros over `_ex` functions**
  (`cui_kv_editor` is a macro over `cui_kv_editor_ex`): the generator works from the `_ex` function.
  The macro is C's default-argument trick and needs nothing.
- **A comment is not a section.** A title comes only from a line with a run of dashes, because the
  header is full of `/* dt is the time since the previous frame */`.
- **`size_t` and `uintptr_t` are `nuint`, not `ulong`.** Pointer-sized means 32-bit on wasm32; an
  older generator said `ulong` and the ABI test was right to fail.
- **A fixed buffer in a record is text or an array** (`char[N]` → `string?`, `bool[N]` → `bool[]?`),
  copied both ways. `CUI_FLAG_ENVS`-style widths mean the caller can get back a longer array than it
  passed: that is the C struct's width, not a bug.
- **Two comment blocks above one declaration.** libclang attaches only the adjacent one, so the
  paragraph above it is orphaned and unreachable — `cui_code_editor_ex`'s description sits between
  `cui_text_mark`'s own one-liner and the struct it follows. It is in `docs/PLAN.md` §3, not a
  generator bug to chase.

## PowerShell

- **Variable names are case-insensitive**: a local `$notes` *is* the `-Notes` switch. `cui-api-diff.ps1`
  names its local `$section` for that reason; the same trap is documented in `tools/snapshots.ps1`.
- **A function called `Git` recurses into itself** when it calls `git`. `Invoke-Git` is named that way.
- **`[System.IO.File]::ReadAllLines` is not a legal MSBuild property function.** The provenance step
  uses `ReadLinesFromFile`. If `verify-template.ps1` dies at "packing CuiSharp", that is where to look.

## Gates that fail for a reason that is not yours

- **A test that fails only while another build runs.** `DeveloperToolTests.TheAnalysisPagesCostNothing
  PerFrame` once failed during an `api-surface.ps1` run that rebuilt the solution, then passed in
  isolation and in two full runs. If that returns, investigate instead of widening the threshold.
- **`cui.dll` is locked** while an app is loaded: the native rebuild fails with a sharing violation
  until you close the app. On Windows this is the most common false "sync is broken".
- **A regenerated file that is "different" after a no-op sync** means the generator is not
  deterministic — `regen.ps1 -NoDiff` exists to catch it, and `BindgenChecks` has a determinism check
  that runs the whole plan twice.
- **`api-surface.ps1` rewrites the baseline.** It is a review gate: read the diff before you commit
  it, and use the set difference in `SKILL.md` step 4 rather than reading 1 000 lines.

## Things that are true by design, so do not "fix" them

- **Charts, texture handles, text-buffer inputs and reorder are hand-written.** `coverage.txt` names
  the member and the file for each; `EXCLUDED` and `MANUAL` are decisions, not gaps.
- **`cui_set_strings` / `cui_get_strings` stay `EXCLUDED`.** CUI keeps the pointer and reads it every
  frame; the `Localization` owner that makes it safe is written up in `docs/PLAN.md`.
- **A section titled as a group** ("Instruments: dot-matrix chart, event track, readout, dot-matrix
  display") is one extension class with four widgets: the section is the file, not the component.
- **An option struct with no `why` on its rename** means it took its default name (`CardOpts`), which
  is correct until a reviewer decides the shorter one reads better.

## Before you call a sync done

```sh
pwsh skills/cui-sync/scripts/sync-status.ps1    # exit 0: no PENDING, numbers match coverage.txt
git status --short                              # generated files committed, nothing stray
```

The numbers in `README.md` and `bindings/README.md` are the ones a reader trusts; `sync-status.ps1`
compares them with `coverage.txt`, so a stale sync is a failing command rather than a wrong claim
someone notices a release later.
