# What a binary costs

A CuiSharp app publishes as one NativeAOT executable. This page says what is inside it, which
lever moves the size, and what the package now does about it. Every number is measured by
`tools/size-report.ps1` on one Windows machine (win-x64, .NET SDK 10.0.14, CUI 1.3.1, stripped);
treat absolute values as indicative and the shape of the differences as the finding.

```sh
pwsh tools/size-report.ps1 -All              # every shape, both widths
pwsh tools/size-report.ps1 -All -Csv .tmp/size.csv
```

## Where the bytes go

A minimal app — a column, a heading, a button, a line of text — publishes at **1 165 312 B**:

| Part | Bytes | Share of the executable |
|---|---|---|
| CUI native, linked out of `cui.lib` | ~280 000 | 24% |
| CuiSharp's own managed code | ~42 000 | 3.6% |
| .NET runtime and framework | ~845 000 | 72% |

The last row is the one that decides everything else: a hello world with no CuiSharp reference at
all is **870 912 B**, so the runtime you already pay for is most of a small app and CUI's whole
contribution is about 295 KB on top of it. Half of that is native, half is framework code the
library's shape drags in — the headless channel's IO stack, number and string formatting, enum
reflection.

## What the package ships

`build/CuiSharp.Size.targets` decides what a published binary contains. Its switches apply to a
**self-contained AOT publish** and nothing else, because that is the only build whose size is a
product: `dotnet run` keeps the headless channel and the framework's exception messages.

| Property | Default | What it does |
|---|---|---|
| `CuiSharpSizeProfile` | `true` for an AOT publish | the diagnostic switches below, as a set |
| `CuiSharpHeadless` | `false` for an AOT publish, `true` otherwise | the protocol `dotnet cui` drives an app with |
| `CuiSharpVerboseErrors` | `false` | keeps framework exception messages and stack traces |

The set also turns `DebuggerSupport` off, which drops the debug header an AOT binary otherwise
exports on every platform. It is not part of `CuiSharpVerboseErrors`: that one is about what an
exception says, not about whether one can be stepped through on a machine that is not the user's.

`PublishAot` alone would be the wrong signal: the file-based app shape sets it for `dotnet run`
too, and compiling the channel out there would take the protocol away from the shortest way of
trying an app. The profile wants `PublishAot` **and** a `RuntimeIdentifier`.

What it is worth, same app, same native library. The "before" column is the app as 0.3.1 published
it, so it predates both changes in this release; `tools/size-report.ps1 -Widths before` measures only
the switches, and prints 1 474 048 B for `MIN`, because the `ScopeEnds` fix is in the source either
way:

| App | Before the profile | Shipped |
|---|---|---|
| minimal app | 1 498 112 | 1 165 312 (−22.2%) |
| gallery, 28 pages | 2 581 504 | 2 292 736 (−11.2%) |
| `dotnet new cui-app`, published | — | 2 244 608 |
| `dotnet new cui-mini`, published | — | 1 163 264 |

Nearly all of it is the headless channel: `System.IO`, `Console`, `Encoding` and the reflection
over an enum the command lexer reads arrive with it, and a switch only removes them when the
trimmer knows the answer at build time. The rest is `StackTraceSupport` and
`UseSystemResourceKeys`, which rewrite framework exception messages, plus telemetry and
hot-reload metadata that a shipped app never reads.

The two template rows carry the app's own name and version inside the executable, so `-n` moves them
by a few hundred bytes; the mini template sets the properties that keep that publish to one file.

`dotnet cui` passes `-p:CuiSharpHeadless=true` to the builds it runs itself, and says so when a
**published** app has no channel: publishing one for a tool to inspect needs the same property.

## What a feature costs

Difference from the minimal app on the shipped width — the same frame plus one widget family:

| Added | Cost |
|---|---|
| a table with eight rows | +7 168 B (0.6%) |
| a line chart, 200 points | +30 720 B (2.6%) |
| an inline edit, a checkbox, a slider | +33 792 B (2.9%) |
| Markdown and a code block | +61 440 B (5.3%) |

**A feature costs tens of kilobytes, not hundreds.** An app that uses a table and a chart pays about
3% over one that does not, which is why the work in this release was about the floor and not about
widgets.

## And what the native library costs

The archive is 1 372 KB of sections across 58 translation units; the biggest are the embedded font
blob (94 KB of `.rdata`), then `cui_viz` 83, `cui_pickers` 56, `cui_emoji` 56, `cui_image` 49,
`cui_devtools` 49, `cui_flame` 44, `cui_text` 44.

But the archive is not what ships. CUI compiles with `/Gy /Gw` and the ILC link runs
`/OPT:REF /OPT:ICF`, so `cui.lib` is a catalogue and the linker keeps only the functions the app
reaches. A minimal app links **280 KB of it, from 14 translation units** — 20% of the archive — and
that set is the minimum: `cui_font_data` 94, `cui_text` 39, `cui_render_sw` 32, `cui_layout` 21,
`cui_core` 14, `cui_draw` 13, `cui_overlay` 11, `cui_theme` 10, `cui_render_gl` 9, `cui_widgets` 9,
`cui_app_win32` 8.5, `cui_nodes` 7, `cui_app` 5, `cui_inflate` 4.

So a second package that linked only the basic widgets would not have made a binary smaller. CUI
1.3 added twenty components and about 5 000 lines of C; the minimal app above did not grow a byte,
because nothing calls them.

## The one place where a widget used to cost its neighbours

`LayoutScope.Dispose` used to call `ScopeEnds.Close(ui, kind)` — one `switch` naming all 39
`*_end` functions. Every app that opened a box therefore rooted every container's end function, and
the linker pulled the translation unit each one lives in: `cui_pickers` (16 KB), `cui_dock` (3 KB)
and the rest arriving in an app that asked for none of them. A reduced archive could not even link
until the switch was limited, which is how the dependency was found.

The generator now takes the end function at the call site — `new LayoutScope(ui.Handle,
&Native.BoxEnd, "Box", o.Id)` — so an end function is reachable exactly when the wrapper that calls
it is, which is the rule the rest of the binding already follows. Worth **−24 064 B** on the minimal
app and **−2 048 B** on the gallery: an app that draws everything was paying for what it used.

## Still open

- **The fonts.** 94 KB, 8% of a minimal app, is the one large unconditional blob in the archive.
  `CUI_DEFAULT_FONTS=OFF` removes it and the Inter/Geist/Lucide glyphs with it; CUI has no
  system-font fallback, so an app that takes that deal ships or loads its own faces. That is an
  app's decision (branding, licensing), not a tier.

## Budgets

`tools/budget-check.ps1 -SizeCsv` reads these ceilings, so the gate and the table cannot disagree.
Each one is a round number about 3% above the measurement, which leaves room for a compiler or
linker patch and none for a new component quietly arriving in every app.

| Shape | Ceiling | What it is |
|---|---|---|
| `hello` | budget 900 000 B | the .NET AOT floor with no CuiSharp reference at all |
| `MIN` | budget 1 200 000 B | a column, a heading, a button, a line of text |
| `TABLE` | budget 1 210 000 B | MIN plus a table with eight rows |
| `CHART` | budget 1 235 000 B | MIN plus a line chart of 200 points |
| `FORM` | budget 1 240 000 B | MIN plus an inline edit, a checkbox and a slider |
| `RICH` | budget 1 265 000 B | MIN plus Markdown and a code block |

Every pull request measures the floor and the gallery (`hello`, `MIN`, and the gallery's own row in
[PERFORMANCE.md](PERFORMANCE.md)); every shape, with `-RequireAll` so a shape that stopped being
measured fails instead of passing quietly, is the on-demand `budget` job.

```sh
pwsh tools/size-report.ps1 -All -Widths shipped -Csv artifacts/size.csv
pwsh tools/budget-check.ps1 -SizeCsv artifacts/size.csv -RequireAll
```

## Reproducing the numbers

`tools/size-report.ps1` builds `tools/size-matrix` (five app shapes, one per widget family) and
prints the tables above. It imports `build/CuiSharp.Size.targets` rather than restating the
switches, so it measures the profile that ships. The native library must already be built
(`pwsh native/build-native.ps1`); the script does not rebuild it.

The archive breakdowns come from a link map: publish with `-p:IlcGenerateMapFile=true` and read
`obj/Release/net10.0/<rid>/native/<assembly>.map.xml` for the managed side, or add
`<LinkerArg Include="/MAP:$(MSBuildProjectDirectory)\aot.map" />` and read the `cui:*` objects for
the native side.
