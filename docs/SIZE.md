# What a binary costs

A CuiSharp app publishes as one NativeAOT executable. This page says what is inside it, which
lever moves the size, and why a smaller "mini" *package* would not be one of them. Every number is
measured by `tools/size-report.ps1` on one Windows machine (win-x64, .NET SDK 10.0.14, CUI 1.3.1,
`OptimizationPreference=Size`, stripped); treat absolute values as indicative and the shape of the
differences as the finding.

```sh
pwsh tools/size-report.ps1 -All              # every shape, three profiles
pwsh tools/size-report.ps1 -All -Csv .tmp/size.csv
```

## Where the bytes go

A minimal app — a column, a heading, a button, a line of text:

| Part | Bytes | Share |
|---|---|---|
| .NET AOT runtime, GC and CRT (a hello world with no CuiSharp) | 870 912 | 58% |
| CuiSharp managed code | ~42 000 | 3% |
| .NET framework code pulled in by using CuiSharp | ~300 000 | 20% |
| CUI native (`cui.lib`, linked) | ~303 000 | 19% |
| **total** | **1 499 648** | |

The interesting row is the last-1: **of the 948 KB the app contributes on top of the floor, only
41 KB is CuiSharp's own code.** The rest is .NET framework the library's *shape* drags in — number
formatting, string formatting, enum reflection, and (before it is trimmed) the headless channel's
IO stack.

## The three widths

Same app, same native library, three publish profiles:

| Width | What it adds | MIN | Gallery (28 pages) |
|---|---|---|---|
| plain | the template's profile, as shipped | 1 499 648 | 2 581 504 |
| tuned | + NativeAOT switches that change nothing an app relies on | 1 371 136 | 2 424 832 |
| mini | tuned, with `CuiSharp.Headless` trimmed out | 1 189 888 | n/a (the gallery is driven headless) |

`tuned` is −128 512 B even on the gallery (−6%), and `tuned` → `mini` is a further −181 248 B
(−13%) on a windowed app, because a feature switch that is compiled in drags the whole
`System.IO` / `Console` / `Encoding` stack behind it even when nothing calls it.

The switches in `tuned` (`tools/size-matrix/Shapes.csproj`, `CuiSizeTuned`): `UseSystemResourceKeys`,
`EventSourceSupport=false`, `MetadataUpdaterSupport=false`, `StackTraceSupport=false`,
`HttpActivityPropagationSupport=false`, `EnableUnsafeBinaryFormatterSerialization=false`,
`XmlResolverIsNetworkingEnabledByDefault=false`, `UseNativeHttpHandler=true`. They are build
properties, not API: nothing in the library changes.

## What a feature costs

Difference from MIN on the same width — the same frame plus one widget family:

| Added | plain | mini |
|---|---|---|
| a table with eight rows | +8 192 B | +6 656 B |
| a line chart, 200 points | +18 432 B | +30 720 B |
| an inline edit, a checkbox, a slider | +34 816 B | +33 792 B |
| Markdown and a code block | +61 952 B | +60 416 B |

**A feature costs tens of kilobytes, not hundreds.** So an app that uses charts and a table pays
about 4% over one that does not.

## And what the native library costs

The archive is 1 372 KB of sections across 58 translation units; the biggest are the embedded font
blob (94 KB of `.rdata`), then `cui_viz` 83, `cui_pickers` 56, `cui_emoji` 56, `cui_image` 49,
`cui_devtools` 49, `cui_flame` 44, `cui_text` 44.

But the archive is not what ships. CUI compiles with `/Gy /Gw` and the ILC link runs `/OPT:REF
/OPT:ICF`, so `cui.lib` is a catalogue and the linker keeps only the functions the app reaches.
A minimal app links **303 KB of it, from 16 translation units** — 22% of the archive — and that set
is already the minimum: `cui_font_data` 94, `cui_text` 39, `cui_render_sw` 32, `cui_layout` 21,
`cui_core` 14, `cui_draw` 13, `cui_overlay` 11, `cui_theme` 10, `cui_render_gl` 9, `cui_widgets` 9,
`cui_app_win32` 8.5, `cui_nodes` 7, `cui_app` 5, `cui_inflate` 4, `cui_pickers` 16 and `cui_dock` 3
(the last two are collateral, below).

## Why a "mini package" would not have made the binary smaller

The proposal was a second package that links only the basic controls so an app never pays for the
rest. Measured, that premise does not hold on this architecture, for three separate reasons:

1. **Widget code is already dropped by the linker.** CUI 1.3 added twenty components and about
   5 000 lines of C; the minimal app above did not grow a byte, because nothing calls them.
   Excluding translation units from the archive changes only what reaches the archive, not what
   reaches the executable.
2. **The only way to make an exclusion bite is to break the build.** Subtracting the 58-TU archive
   to the 16 TUs a minimal app uses fails to link, because the managed side roots functions the
   app never calls — see the next section. Everything else that a subset would "save" is what the
   linker already saves.
3. **The one large unconditional blob is the fonts** — 94 KB, 8% of MIN. `CUI_DEFAULT_FONTS=OFF`
   removes it, and so do the Inter/Geist/Lucide glyphs and every icon with them: CUI has no
   system-font fallback, so the app must ship or load its own faces. That is an app's decision
   (branded font, logo, licensing), not a tier.

What a Mini package *would* have cost: a second binding, a second analyzer set, a second docs tree,
a second release artifact and a second ABI to keep in step — for about 2% of the bytes.

## The lever that is real: `ScopeEnds`

`LayoutScope.Dispose` calls `ScopeEnds.Close(ui, kind)`, one `switch` naming all 39 `*_end`
functions. Every app that opens one container — every app — therefore roots all 39 `*End` symbols,
and each of those pulls its own translation unit: `cui_pickers`, `cui_dock` and the rest arrive
without being asked for. Limiting the switch to the four kinds a minimal app can open measures
**−24 064 B**; a subset archive cannot even link until the switch is limited, which is how the
dependency was found.

The fix is in the generator, not the app: a scope should not consult a table that names every
container. Either the generated wrapper passes its own end function (so only a wrapper that exists
roots an end function), or the switch is generated per *API surface* — which is also what a real
"mini" would need, since the table is the one place where the widget set and the binary are
actually coupled.

## Verdict

Do not ship a second package. Ship the same package with:

1. **A size profile** — the switches in `tuned`, on by default for a published app, with the
   template and `docs` explaining the two that change behaviour (`UseSystemResourceKeys` rewrites
   framework exception messages, `StackTraceSupport=false` drops stack traces).
2. **`CuiSharp.Headless` off unless asked for** — one MSBuild property, default `false` in a
   published app, `true` for the gallery and for anything `dotnet cui` builds. Worth 181 KB.
3. **The `ScopeEnds` fix in the generator** — worth 24 KB to every app, and the change that makes a
   widget-set/binary coupling possible at all.
4. **A budget per shape** in `tools/budget-check.ps1`, so the next upstream bump that reaches a
   used widget shows up as a number instead of in a release note.

Together 1–3 take a minimal app from 1 499 648 B to about 1 165 000 B (−22%) and the gallery from
2 581 504 B to about 2 400 000 B, without removing one line of API.

## Reproducing the numbers

`tools/size-report.ps1` builds `tools/size-matrix` (five app shapes, one per widget family) and
prints the tables above. The native library must already be built
(`pwsh native/build-native.ps1`); the script does not rebuild it.

The archive breakdowns come from a link map: publish with `-p:IlcGenerateMapFile=true` and read
`obj/Release/net10.0/<rid>/native/<assembly>.map.xml` for the managed side, or add
`<LinkerArg Include="/MAP:$(MSBuildProjectDirectory)\aot.map" />` and read the `cui:*` objects for
the native side.
