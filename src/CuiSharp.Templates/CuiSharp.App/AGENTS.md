# AGENTS.md — a CuiSharp app

Rules for working in this app, for whoever — or whatever — edits it next. They are short on
purpose: they are the ones that change the code.

**The framework's own rules are not in this file.** They ship with the tool, so a project does not
have to restate them — and cannot fall out of date with them:

```sh
dotnet tool install -g CuiSharp.Tools
dotnet cui docs                      # topics: writing-ui, testing, debugging, layout, publishing…
dotnet cui docs search "<question>"   # the sections that answer it, ranked
dotnet cui docs read <topic>         # the rules, the pitfalls, the workflow
dotnet cui api <Name>                # one exact signature
```

Offline, from inside the tool: no repository, no build, no network. When this file and the tool
disagree about CuiSharp itself, the tool is right.

## The shape

```
Program.cs          the composition root: register services and pages, describe the window, run
App.cs              the window: page switch, theme switch, the app's own frame numbers
Pages/              one Component per screen: state as fields, drawing in Draw
Services/           the rules (no CuiSharp reference), each with a test
../*Tests/          the tests, including a real frame drawn with no window
```

A screen is a class plus one registration in `Program.cs` plus one entry in `App`'s constructor.
There is nothing else to wire.

## Five rules

1. **`Draw` is synchronous.** It runs every frame. Never `await`, never `.Result`, never `.Wait()`,
   never `Thread.Sleep`.
2. **Long work goes through a `Mutation`.** Create it once as a field; call `Run()` from a button.
   Read `IsRunning` and `Error` in `Draw`; pass an apply callback when the result changes state.
   `Draw` never polls and never awaits.
3. **State lives in fields of the page.** No view models, no `INotifyPropertyChanged`, no binding,
   no `Invalidate()`. Change a field; the next frame draws it. The theme is the one exception,
   because CUI owns it: assign `ui.Theme` and the frame that follows shows it.
4. **Anything that changes on its own needs `WantsFrames => true`.** Otherwise CUI sleeps and the
   page looks frozen until the pointer moves. The footer says `idle` instead of a frame rate for
   exactly this reason.
5. **Per-frame code allocates nothing.** No LINQ, no `new` per frame, no string concatenation —
   interpolate into a widget parameter instead. A row that can repeat needs `using (ui.Id(...))`.

## Containers and ids

Scopes are `using` blocks; interactive widgets take `ref` and report `bool`. An id is a stable
string: `ui.Button("Save")`, `ui.Input("draft", ref _draft)`. Inside a loop, wrap the row in
`using (ui.Id(item.Id))` so two rows cannot share state.

## Before you finish

```sh
dotnet test            # the rules, the ring buffers, and real frames
dotnet build -warnaserror
```

A render test draws a real frame into a pixel buffer and asserts `surface.Faults` is empty, so a
page that throws cannot pass as a page that works.

When the question is what the app actually draws, ask the tool rather than guessing which page to
open — `dotnet cui docs read debugging` is the whole workflow, and these are the commands it uses:

```sh
dotnet cui tree --project .   # the controls of the last frame, nested
dotnet cui click Dark         # a control, by the label or id the tree printed
dotnet cui png shot.png       # the frame as a PNG, for you or for a person
```

A `tree`, a `click` or a `png` is evidence, not a fix: finish by making the test fail without the
change.

## The performance claims are tests

`PageRenderTests` asserts **0 B per frame** for every page; `PageInteractionTests` clicks through
them. If you add a `foreach` over an
interface, a `Select`, a `ToList`, or string building inside `Draw`, that test fails. Fix the code;
do not relax the test.

## Notes

- `CUI_TRACE=trace.txt dotnet run --project .` writes a frame-cost line every 60 frames. A WinExe
  has no console, so this file is where the numbers are.
- The window redraws only when something asks it to: user input, `WantsFrames`, or a mutation that
  is running or just finished.
- `dotnet run --project .` to iterate; `dotnet watch --project .` to edit a page and see it.
