# CuiSharp.App

Native desktop app in plain C# built on [CuiSharp](https://www.nuget.org/packages/CuiSharp).

```sh
dotnet run --project CuiSharp.App             # opens on Todos
dotnet watch --project CuiSharp.App           # edit with hot reload
dotnet test                                  # service and headless UI tests
```

## Follow one interaction: add a todo

The list is in memory; closing the app resets it.

1. [`Pages/TodoComposer.cs`](CuiSharp.App/Pages/TodoComposer.cs) owns the unfinished draft. Typing
   changes that field, not the list. Enter (`input.Submitted`) and the Add button both return an
   `AddRequested` carrying the draft.
2. [`Pages/TodoPage.cs`](CuiSharp.App/Pages/TodoPage.cs) handles that request and draws the applied
   items. [`TodoService.Add`](CuiSharp.App/Services/TodoService.cs) trims the title and either adds
   an item or rejects a blank title. The composer clears the draft only on acceptance; on rejection
   it keeps the draft and shows feedback. The page requests a follow-up frame because the header
   and input were already drawn when the result arrived.
3. [`PageInteractionTests.cs`](CuiSharp.App.Tests/Pages/PageInteractionTests.cs) drives the same
   input. `EnterAddsTheDraftAndClearsItAfterAcceptance` checks the model and the recorded frame;
   `ARejectedDraftStaysInTheInputAndShowsFeedback` checks that spaces stay in the draft and no item
   is added.

```sh
dotnet test --filter EnterAddsTheDraftAndClearsItAfterAcceptance
dotnet tool install -g CuiSharp.Tools          # once, if not installed
dotnet cui session -p CuiSharp.App --script todo.steps.txt
```

[`todo.steps.txt`](todo.steps.txt) clicks `id:draft`, types `Milk`, and presses Enter. The two
`tree details` outputs show the draft before submission and the applied item after it (excerpts):

```text
    Column "todo-composer" role=container state=enabled
      Input "todo-title" role=textbox id=draft value="Milk" state=enabled
      Button "Add" role=button id=add-todo state=enabled
```

```text
    Column "todo-composer" role=container state=enabled
      Input "todo-title" role=textbox id=draft value="" state=enabled
      Button "Add" role=button id=add-todo state=enabled
    Scroll "items" role=container state=enabled
      …
      Checkbox "Milk" role=checkbox id=#4 value="false" state=enabled
```

- `todo-title` is the input's **widget key**: CUI uses it for native focus and editing state.
  `ui.Id` and `ui.Keyed` scope native identity and also give the recorder IDs (e.g. `#4` above).
- `draft` and `add-todo` are **automation IDs** from `ui.Automation`. Use `id:draft` in a tool
  query or `FindById("draft")` in a test. This scope only names the recorded control; it does not
  change native identity.
- `Task` and `Add` are **visible labels**. Translate the labels, not the IDs. The input records its
  key rather than its visible label; a label-based query like `Button "Add"` is handy for exploration
  but depends on the current wording.

Each headless invocation starts a fresh app. Use a `session` for steps that share state.
`dotnet cui tree --details -p CuiSharp.App` inspects a fresh frame. Add `--no-build` only if you
already have a Release build. For a window you opened with `dotnet run`, `dotnet cui tree -a`
uses the listener enabled in [`Program.cs`](CuiSharp.App/Program.cs). See [AGENTS.md](AGENTS.md)
for frame scheduling and `dotnet cui docs` for the full guide.

<!--#if (!NoMarkets) -->
## Beyond Todo: Markets

Open the **Markets** tab, or inspect it without opening a window. The shell knows each tab's
name at startup but creates its page on first visit, then keeps it so switching tabs preserves state:

```sh
dotnet cui click 'Tab "Markets"' -p CuiSharp.App
```

[`MarketsPage.cs`](CuiSharp.App/Pages/MarketsPage.cs) is the advanced example: a `Mutation` loads
history, a clock drives the live feed, and the table and chart reuse sample buffers. Select a
symbol to change the chart. [`MarketsPageTests.cs`](CuiSharp.App.Tests/Pages/MarketsPageTests.cs)
checks 0 B per steady draw, including with a non-default symbol selected. The first-visit test
reports the draw that constructs the page and starts the history load separately from 31 warmed
live draws. It measures managed draw time and allocations, not the async wait or presentation:

```sh
dotnet test -c Release --filter TheFirstMarketsVisitShowsLoadingThenTheLiveChart --logger "console;verbosity=detailed"
```

For a Todo-only starting point, generate a fresh app with `dotnet new cui-app -n MyTodo --no-markets`.
<!--#endif -->

## Publish and diagnose

```sh
dotnet run --project CuiSharp.App -- --check
dotnet publish CuiSharp.App -c Release -r win-x64
```

The NativeAOT publish omits the driving channels unless you keep them with
`-p:CuiSharpHeadless=true` (for `--app`) or `-p:CuiSharpLive=true` (for `-a`). Replace
`CuiSharp.App/CuiSharp.App.ico` to rebrand the executable and window.
