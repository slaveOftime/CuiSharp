# AGENTS.md — CuiSharp.App

See [README.md](README.md) for the file map, the run commands and how to drive the app from a
terminal.

## Add a screen

Four edits, and the compiler names what is left:

1. `Pages/<Name>Page.cs` — a `Component` whose state is fields and whose `Draw(Ui ui)` draws them.
2. `Program.cs` — `builder.Services.AddSingleton<<Name>Page>();`
3. `App.cs` — the page in the primary constructor, then in `_pages` and `_titles`.
4. `*.Tests` — every `new App(...)` in the tests takes the new page (the compiler lists them).

## Rules

1. **`Draw(Ui ui)` is synchronous and allocates 0 B in steady state.** Never `await`, block (`.Result`, `.Wait()`, `Thread.Sleep`), use LINQ, allocate per-frame arrays/closures, or format strings before passing them (`$"..."` in a `UiText` argument formats into the frame arena). Iterate `IReadOnlyList<T>` by index (`for`), not `foreach`.
2. **State lives in component fields.** Mutate fields directly; the next frame draws the change. The theme is the exception, because CUI owns it: `ui.Theme` / `ui.ToggleTheme()`.
3. **Scopes & widget IDs.** Dispose every layout/container scope with `using`. Guard conditional scopes (`OpenScope` from `Dialog`, `Menu`, `Table`, …) with `if (scope)`. Inside a loop, wrap repeating rows in `using (ui.Id(item.Id))`.
4. **Async work uses `Mutation` / `Mutation<T>`.** Hold it in a `readonly` field, start with `Run()`, read `IsRunning` and `Error` in `Draw`, and let the `apply` callback change state on the UI thread before the next frame.
5. **Self-updating screens set `protected override bool WantsFrames => true`.** Otherwise the window sleeps between inputs.
6. **Name the container that bounds a region** (`header`, `page`, `footer`, `preferences`), so `dotnet cui tree` nests its controls under it and a query can reach it. Do not reuse a control's word: a `Tab "Settings"` outranks a `Column "settings"` in a query, so bare queries no longer identify the region reliably. Use distinct region names.

## Tests

`TestHarness.AssertZeroAllocations(page)` measures the page's own draw, so a page that allocates
fails it — fix the page, never the gate. Pass `prepare: app => …` to measure a state a user reaches
(the gate draws the untouched page otherwise, and an allocation that only appears after an edit
would pass). Keep that state during measurement; use `verify: app => …` to assert it afterward. Input is `app.FindById("draft").Click()` then `app.Type("Milk")` — the click is what
focuses the field; `Key(Key.Enter)` submits. Assert on the model (`todos.Items`) or on the recorded
frame (`app.Tree`), never on how the page is written.

## Style

Modern C#: `var` when the type is apparent, collection expressions (`[]`, `[..]`), switch
expressions for branching on a value, primary constructors for pages and services. Every
`if`/`else`/`for`/`foreach`/`while` body is braced, and a method returns early rather than nesting.

## Verification

`dotnet test` and `dotnet build -warnaserror` after every change. When the question is what the app
really draws, `dotnet cui tree -p CuiSharp.App` is one build and one answer, and
`dotnet cui session -p CuiSharp.App < cmds.txt` keeps a multi-step flow in one process.
