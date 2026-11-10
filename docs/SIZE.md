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

A minimal app — a column, a heading, a button, a line of text — publishes at **1 189 376 B**:

| Part | Bytes | Share of the executable |
|---|---|---|
| CUI native, linked out of `cui.lib` | ~303 000 | 25% |
| CuiSharp's own managed code | ~42 000 | 3.5% |
| .NET runtime and framework | ~845 000 | 71% |

The last row is the one that decides everything else: a hello world with no CuiSharp reference at
all is **870 912 B**, so the runtime you already pay for is most of a small app and CUI's whole
contribution is about 320 KB on top of it. Half of that is native, half is framework code the
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

`PublishAot` alone would be the wrong signal: the file-based app shape sets it for `dotnet run`
too, and compiling the channel out there would take the protocol away from the shortest way of
trying an app. The profile wants `PublishAot` **and** a `RuntimeIdentifier`.

What it is worth, same app, same native library:

| App | Before the profile | Shipped |
|---|---|---|
| minimal app | 1 498 112 | 1 189 376 (−20.6%) |
| gallery, 28 pages | 2 581 504 | 2 294 784 (−11.1%) |
| `dotnet new cui-app`, published | — | 2 266 624 |
| `dotnet new cui-mini`, published | — | 1 159 680 |

Nearly all of it is the headless channel: `System.IO`, `Console`, `Encoding` and the reflection
over an enum the command lexer reads arrive with it, and a switch only removes them when the
trimmer knows the answer at build time. The rest is `StackTraceSupport` and
`UseSystemResourceKeys`, which rewrite framework exception messages, plus telemetry and
hot-reload metadata that a shipped app never reads.

`dotnet cui` passes `-p:CuiSharpHeadless=true` to the builds it runs itself, and says so when a
**published** app has no channel: publishing one for a tool to inspect needs the same property.

## What a feature costs

Difference from the minimal app on the shipped width — the same frame plus one widget family:

| Added | Cost |
|---|---|
| a table with eight rows | +7 168 B (0.6%) |
| a line chart, 200 points | +31 232 B (2.6%) |
| an inline edit, a checkbox, a slider | +34 304 B (2.9%) |
| Markdown and a code block | +60 928 B (5.1%) |

**A feature costs tens of kilobytes, not hundreds.** An app that uses charts and a table pays about
4% over one that does not, which is why the tiers below are about the floor and not about widgets.

## And what the native library costs

The archive is 1 372 KB of sections across 58 translation units; the biggest are the embedded font
blob (94 KB of `.rdata`), then `cui_viz` 83, `cui_pickers` 56, `cui_emoji` 56, `cui_image` 49,
`cui_devtools` 49, `cui_flame` 44, `cui_text` 44.

But the archive is not what ships. CUI compiles with `/Gy /Gw` and the ILC link runs
`/OPT:REF /OPT:ICF`, so `cui.lib` is a catalogue and the linker keeps only the functions the app
reaches. A minimal app links **303 KB of it, from 16 translation units** — 22% of the archive — and
that set is already the minimum: `cui_font_data` 94, `cui_text` 39, `cui_render_sw` 32,
`cui_layout` 21, `cui_core` 14, `cui_draw` 13, `cui_overlay` 11, `cui_theme` 10, `cui_render_gl` 9,
`cui_widgets` 9, `cui_app_win32` 8.5, `cui_nodes` 7, `cui_app` 5, `cui_inflate` 4, plus
`cui_pickers` 16 and `cui_dock` 3 which nothing asked for (see `ScopeEnds` below).

So a second package that linked only the basic widgets would not have made a binary smaller. CUI
1.3 added twenty components and about 5 000 lines of C; the minimal app above did not grow a byte,
because nothing calls them. The two things that *would* have to change are the only two that are
still open.

## Still open

- **`ScopeEnds.Close` names every container.** `LayoutScope.Dispose` calls one `switch` over all
  39 `*_end` functions, so every app that opens one container roots all of them, and the compiler
  links the translation unit each one lives in. Limiting the switch to the four kinds a minimal app
  can open measures **−24 064 B**; a reduced archive cannot even link until it is limited, which is
  how the dependency was found. The fix belongs in `tools/bindgen.cs`: a wrapper that exists should
  be the only thing that roots its end function.
- **A size budget per shape.** `tools/budget-check.ps1` gates the gallery executable; the shapes
  above are only printed. A ceiling per row is what turns "upstream was busy" into a failing check
  instead of a release note.
- **The fonts.** 94 KB, 8% of a minimal app, is the one large unconditional blob in the archive.
  `CUI_DEFAULT_FONTS=OFF` removes it and the Inter/Geist/Lucide glyphs with it; CUI has no
  system-font fallback, so an app that takes that deal ships or loads its own faces. That is an
  app's decision (branding, licensing), not a tier.

## Reproducing the numbers

`tools/size-report.ps1` builds `tools/size-matrix` (five app shapes, one per widget family) and
prints the tables above. It imports `build/CuiSharp.Size.targets` rather than restating the
switches, so it measures the profile that ships. The native library must already be built
(`pwsh native/build-native.ps1`); the script does not rebuild it.

The archive breakdowns come from a link map: publish with `-p:IlcGenerateMapFile=true` and read
`obj/Release/net10.0/<rid>/native/<assembly>.map.xml` for the managed side, or add
`<LinkerArg Include="/MAP:$(MSBuildProjectDirectory)\aot.map" />` and read the `cui:*` objects for
the native side.
