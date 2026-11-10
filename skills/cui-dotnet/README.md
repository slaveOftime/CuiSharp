# The CuiSharp bundle

Everything someone needs to build an app with CuiSharp, in one folder. It is what the NuGet package
ships, so the same paths work whether you are reading the repository or the package:

| File | What it answers |
|---|---|
| `SKILL.md` | **Start here.** How to write a UI, the pitfalls, the workflow, the component list. Hand-written. |
| `llms.txt` | Every widget and every signature on one line each, plus the rules that make code work. Generated. |
| `api/*.md` | One page per section: signatures, options, records. Generated. |
| `widgets.md`, `widgets-more.md` | What each component does — selection, focus, keyboard, return values. CUI's own documentation. |
| `hosting.md` | Components, `Mutation<T>`, dependency injection, the frame model. |
| `native-abi.md` | Which RIDs the package carries, the glibc floor, and the provenance it records. |

Reading order for a first task: `SKILL.md` for the loop and the rules, then `llms.txt` for the
signature you need, then the widget page when the question is *behaviour* rather than shape.

An assistant only needs the first two: point it at this folder and say "read `SKILL.md`".

## How this folder is produced

Never edit the generated files by hand — three of them are copies, and the rest are emitted:

| Files | Produced by | Source of truth |
|---|---|---|
| `llms.txt`, `api/*.md` | `tools/bindgen.cs` | the CUI headers |
| `widgets.md`, `widgets-more.md` | copied by `tools/regen.ps1` | `native/CUI/docs/components*.md` |
| `hosting.md` | copied by `tools/regen.ps1` | `docs/HOSTING.md` |
| `native-abi.md` | copied by `tools/regen.ps1` | `docs/NATIVE-ABI.md` |
| `SKILL.md`, `README.md` | written here | — |

```sh
pwsh tools/regen.ps1            # regenerate everything above
pwsh tools/regen.ps1 -NoDiff    # fail if it was stale (what CI runs)
```

`PackagedDocsTests` fails if a copy drifts from its source, and `DocumentationDriftTests` fails if
`SKILL.md` calls an API `llms.txt` does not list — the two ways this folder could quietly lie.
