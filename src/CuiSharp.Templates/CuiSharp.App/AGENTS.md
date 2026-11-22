# AGENTS.md — CuiSharp.App

Start with [the Todo walkthrough](README.md#follow-one-interaction-add-a-todo): follow
`TodoComposer.Draw` → `AddRequested` → `TodoPage.Draw` → `TodoService.Add`, then run
`todo.steps.txt` and the matching headless test.

## State and frames

- **Local state** belongs to the control that edits it. A button can increment its own counter
  directly; the Todo composer owns the draft. Across an ownership boundary, report what the user
  asked for (`AddRequested`), let the owner accept or reject it, and only then clear the draft.
  The page draws **applied state** from `TodoService.Items`.
- **The host owns frames.** A window gets frames for input. Call `ui.RequestRedraw()` during
  drawing when you need a follow-up frame, as Todo does after drawing the input. A rendered
  component uses `WantsFrames` while it is live or animating, not to subscribe to external events.
- **External notifications are not animation.** Changing a field on a worker does not request a
  frame. Use `Mutation<T>` for app-started background work with a UI-thread apply callback, or
  `Progressive<T>` for snapshots while work runs. The host requests frames while such work is
  pending; on Windows it can also post a wake to the window on completion. Do not assume arbitrary
  external events wake a sleeping window across backends. `HeadlessApp.Frame()` forces a draw and
  does not test that wake path. Never call `Ui` from a worker thread.

## Drawing and selectors

- Draw synchronously; never store `Ui` or a scope, or `await` or block in `Draw`. Use `using` for
  scopes and guard conditional scopes with `if (scope)`.
- Name meaningful regions (`page`, `todo-composer`) so the tree shows where controls live. Keep
  widget keys stable for native state. Key dynamic rows by item ID (`ui.Keyed` or `ui.Id`); the
  row's title need not be unique. `ui.Automation` gives a stable test selector, not a native key.
  Translate visible labels, not keys or automation IDs.
- Keep steady draw at 0 B: avoid LINQ and temporary arrays/closures, use `ui.Keyed` or index lists,
  and pass interpolation directly to `UiText`. Creating an item or editing text can allocate.
  `TestHarness.AssertZeroAllocations` measures draw, not input setup or tree snapshots. Use
  `prepare` to reach a visible state (e.g. a filled draft) and `verify` to check it after measurement.

To add a screen, create a `Component` in `Pages/`, register it as a singleton in `Program.cs`,
and add a `(title, factory)` entry to the `App` registration. Keep the title separate so the
shell can draw every tab without constructing inactive pages. The first visit creates the page;
later visits reuse it. Run `dotnet build -warnaserror` and `dotnet test`. Use the model and
`app.Tree` to assert behavior; use pixels to verify appearance.
