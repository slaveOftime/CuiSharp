---
name: cui-dotnet
description: Build native desktop UI in C# with CuiSharp, an immediate-mode binding over CUI, a C99 UI library. Use for any C# desktop window, form, dashboard, table, chart, dialog, custom control, or headless UI test that should avoid XAML, WinForms, WPF, Avalonia or a web view.
---

# CuiSharp — writing UI in C#

CuiSharp draws its own widgets through OpenGL or a software rasterizer: no XAML, no data binding,
no visual tree. `net10.0` and up.

Use it when the app is a desktop window with forms, tables, dashboards or charts, and you want one
small native executable and no managed UI framework. Do not use it for a website, a mobile app, or
a UI that needs an accessibility tree (CUI publishes none).

## Contents

1. [The one rule](#the-one-rule) · [A whole app](#a-whole-app) · [Cheat sheet](#cheat-sheet)
2. [Frame-local types](#frame-local-types-and-text) · [Containers](#containers-are-using-scopes) ·
   [Sizing](#sizing) · [Options](#options-are-structs-of-fields) · [Colours](#colours-come-from-the-theme)
3. [State in `ref`](#state-goes-in-ref) · [Conditional containers](#containers-that-may-be-closed) ·
   [Custom controls](#custom-controls) · [Long series](#long-series-and-live-data)
4. [Pitfalls](#pitfalls) · [Async work](#async-work) · [Renderer](#where-it-renders) · [Keyboard](#keyboard) ·
   [Theming](#theming) · [Translation](#translating-the-date-names) · [Design rules](#design-rules)
5. [How to work on a UI](#how-to-work-on-a-ui) · [Testing](#testing-without-a-window) ·
   [Driving a real app](#driving-a-running-app) · [Widget behaviour](#widget-behaviour) ·
   [Files](#files)

## The one rule

**Rebuild the entire UI every frame, top to bottom, in call order.**

No handles, no properties to set later, no invalidation. To change something, change a variable and
draw again. Conditional UI is an `if`; a list is a `for`.

## A whole app

For a file-based C# app: install `CuiSharp.Templates`, then `dotnet new cui-mini -n demo1`
and `dotnet run demo1.cs`. This creates only `demo1.cs` in the current directory, without a new
folder. Add `-o <folder>` only for a separate output directory.
The generated file uses `#:package CuiSharp@*`; pass
`--CuiSharpVersion <version>` to the template to pin it. Use `cui-app` instead for a solution with
components, services and tests.

```csharp
using CuiSharp;

int clicks = 0;
return CuiApp.Run(new CuiAppOptions { Title = "Notes", Width = 900, Height = 600 }, ui =>
{
    using (ui.Column(new Box { Pad = 24, Gap = 12 }))
    {
        ui.Heading("Notes", 1);
        foreach (var note in notes)
            ui.ListItem(note.Title, new ListItemOpts { Subtitle = note.When });
        if (ui.Button("Add note", new ButtonOpts { Variant = ButtonVariant.Primary }))
            notes.Add(new Note("Untitled", "just now"));
    }
});
```

`CuiApp.Run` creates the window, owns the message loop and calls the lambda once per frame until
the window closes. An idle window uses no CPU. For DI, pages and async work, run a hosted app
instead:

```csharp
var builder = CuiApplication.CreateBuilder(args);
builder.Services.AddSingleton<TodoService>();
builder.Services.AddSingleton<TodoPage>();
builder.Window.Title = "Todos";
using var app = builder.Build();
return app.Run<TodoPage>();
```

## Cheat sheet

| I want to… | Call |
|---|---|
| open a window, one callback per frame | `CuiApp.Run(options, ui => …)` |
| a DI-hosted app with pages | `CuiApplication.CreateBuilder(args).Build().Run<TRoot>()` |
| stack children / place them side by side | `using (ui.Column(box))`, `ui.Row(box)`, `ui.Box(box)` |
| a card, a scroll area, a grid | `using (ui.Card(new() { Id = "x" }))`, `ui.Scroll(...)`, `box.Dir = Dir.Grid` |
| a button that reports a click | `if (ui.Button("Save")) Save();` |
| a text field | `ui.Input("id", ref name, new InputOpts { Label = "Name" })` |
| a checkbox, switch, slider, tabs | `ui.Checkbox(l, ref b)`, `ui.Switch`, `ui.Slider(l, ref f, 0, 100)`, `ui.Tabs(id, names, ref i)` |
| a chart over a live feed | `ui.LineChart(id, series, new ChartOpts { Height = 300 })` (decimates for you) |
| a million rows | `ui.VirtualList(id, count, rowHeight, height, out first, out last)` |
| a custom drawing | `ui.Canvas(id, w, h, (paint, box) => …)` or an `extension(Ui ui)` control |
| a dialog / menu / popover | `using var d = ui.Dialog("Title", ref open); if (d) { … }` |
| a shortcut | `if (!ui.WantsKeyboard && ui.Shortcut(Mods.Primary, Key.K)) …` |
| async work from a click | `_mutation.Run()` and draw `_mutation.IsRunning` |
| a page that changes by itself | `WantsFrames => true` on the component, or `ui.RequestRedraw()` |
| to look at the frame without a window | `TestApp.Start(page)` · `dotnet cui tree` |
| per-widget scratch that survives frames | `ui.State<T>(id)` |

## Frame-local types and text

`Ui`, `LayoutScope`, `OpenScope` and `UiText` are `ref struct`. They cannot be fields, captured by a
lambda, or used across an `await`. Store app state, never UI.

Every text argument takes `UiText`, so one method serves strings, UTF-8 and interpolation. Numeric
interpolation formats into the frame arena: no managed string, no boxing.

```csharp
ui.Text("Saved");
ui.Text("Saved"u8);
ui.Text($"Clicked {count,6:D3} times");
```

Never keep a `UiText` for the next frame. There is no `Textf` method.

## Containers are `using` scopes

Every `_begin`/`_end` pair is a stack-only scope, so `using` closes it even when the body throws.
`LayoutScope` always opens; `OpenScope` opens conditionally.

```csharp
using (ui.Column(new Box { Pad = 16, Gap = 8 }))
using (ui.Row(new Box { Gap = 8 }))
{
    ui.Button("Cancel");
    ui.Button("Save", new ButtonOpts { Variant = ButtonVariant.Primary });
}
```

`Box` carries layout for `Column`, `Row` and `Box`: `Width`, `Height`, `MinWidth`/`MaxWidth`,
`Pad`, `PadX`/`PadY`, `PadTop`/`Right`/`Bottom`/`Left`, `Gap`, `Align`, `Justify`, `Wrap`, `Bg`,
`Border`, `Radius`, `Dir`, `Columns` + `MinColumnWidth`, `ScrollX`/`ScrollY`, `Clip`, `Opacity`,
`Elevation`.

## Sizing

```csharp
new Box { Width = Size.Px(240) }        // fixed
new Box { Width = Size.Fill }           // fill the parent
new Box { Width = Size.Percent(50) }    // share of the parent
```

A component that sizes itself (`Button`, `Badge`, `Heading`) takes no size. A component that fills
space (`Table`, `Chart`, `VirtualList`, `Canvas`) needs one: `Height = Size.Px(300)` or
`Size.Fill`.

## Options are structs of fields

One struct per component, named `<Component>Opts`, all-defaulted to CUI's own defaults. Its members
are public fields, so the object initializer is the whole API — there are no `init` accessors and
nothing to construct with a builder:

```csharp
ui.Button("Save", new ButtonOpts { Variant = ButtonVariant.Primary, Icon = Icons.Check });
```

## Colours come from the theme

Never hardcode a colour: tokens make light and dark work without a second palette.

```csharp
Color.Background, Surface, SurfaceMuted, Foreground, TextSecondary, TextMuted, Border,
      Accent, Danger, Success, Warning, Info
Color.Series(n)        // the six chart colours
Color.Rgb(0x7747ff)    // only for a user-chosen colour
```

## State goes in `ref`

```csharp
bool notifications = true;
float volume = 64;
int tab = 0;

ui.Switch("Notifications", ref notifications);
ui.Slider("Volume", ref volume, 0, 100, new SliderOpts { ShowValue = true });
ui.Tabs("tabs", tabNames, ref tab);
```

`Button`, `Checkbox`, `Switch`, `Chip`, `ListItem`, `MenuItem` return `bool`: true on the frame the
user acts. Editable text is a plain `string` by `ref` — CuiSharp keeps one unmanaged buffer per
widget id, encodes only when you assigned a different string, and decodes only on frames CUI reports
an edit.

```csharp
private string _name = "Maya Okafor";       // plain state

InputResult result = ui.Input("name", ref _name, new InputOpts { Label = "Full name" });
if (result.Changed) Save(_name);      // also .Submitted, .Focused, .Blurred, .Any
```

`TextArea`, `Search`, `TagInput` and `OtpInput` work the same way.

A widget that both moves state through a `ref` and reports what the user did returns the action as
an enum, never as an `int` that reads like the state: `ui.Tour(id, steps, ref step)` returns
`TourAction`, `ui.NowPlaying` returns `PlayerEvents`, `ui.ChatComposer` returns `ComposerEvents`,
`ui.ResponseActions` returns `ResponseEvents`, `ui.Comment` returns `CommentEvents`. Assign the enum
only if you want the action; the `ref` is the state.

## Grid state and value selection

Keep one `GridState` per grid, sized to the model. Its selection array must have at least one
entry per row; replace or resize it when the model changes, outside `Draw`.

```csharp
private readonly GridState _grid = new(100);

// Inside Draw: build only the visible range.
using var grid = ui.DataGrid("people", Columns, 100, _grid, new GridOpts { Height = 320 });
if (grid)
    for (int row = _grid.First; row < _grid.Last; row++)
    {
        ui.DataGridRow(row);
        ui.DataGridCellText(people[row].Name);
    }
```

`First`, `Last`, `Sort` and keyboard/header updates to `Selected`, `Cursor` and `Anchor` are
available as soon as the grid opens; row clicks are written back when the scope closes. The
callback form `ui.DataGrid(id, columns, count, state, DrawRows, options)` closes before it returns,
even if the body throws. Hoist the callback rather than making a closure per frame. The six-`ref`
grid API has been removed because its native storage did not survive rows.

A segmented control can also take a value: `if (ui.Segmented("mode", Modes, _mode) is { } next)
_mode = next;`. It returns `null` when nothing changed; the `ref int` form remains available.
Input record arrays (`QueryField.Choices`, `ApiEndpoint.Params`, `UptimeService.Days`,
`StackFrame.Source`, `FilterField.Options`) need no companion count: their native counts always
follow the current array length (null and empty mean zero). Counts on output records remain data.

## Containers that may be closed

Menus, popovers, dialogs, sheets, tables, trees and accordions return `OpenScope`. Guard the body
with `if (scope)`, or use the callback overload, which runs only when open and closes even on a
throw.

```csharp
using (var menu = ui.Menu("Options", new MenuOpts { Icon = Icons.Settings }))
{
    if (!menu) return;
    ui.MenuItem("Rename", new MenuItemOpts { Shortcut = "F2" });
    if (ui.MenuItem("Delete", new MenuItemOpts { Danger = true })) confirm = true;
}

using var dialog = ui.Dialog("Delete project?", ref confirm);
if (dialog)
    using (ui.DialogFooter())
    {
        if (ui.Button("Cancel")) confirm = false;
        if (ui.Button("Delete", new ButtonOpts { Variant = ButtonVariant.Danger })) Delete();
    }
```

`ui.Menu("Options", static body => body.MenuItem("Save"))` is the callback form, for when the body
is one expression.

## Custom controls

Anything CUI lacks becomes an extension member on `Ui`, called exactly like a built-in: no base
class, no registration, no allocation per frame.

```csharp
internal static class MyControls
{
    extension(Ui ui)
    {
        public void Spark(float[] values) =>        // an array: a paint lambda cannot capture a Span
            ui.Canvas("spark", Size.Fill, Size.Px(60), (paint, box) =>
            {
                for (int i = 1; i < values.Length; i++)
                    paint.DrawLine(box.X + i - 1, box.Y + values[i - 1],
                                   box.X + i, box.Y + values[i], 1.5f, Color.Accent);
            });
    }
}

ui.Spark(Monthly.Revenue);
```

A paint delegate cannot capture a `ReadOnlySpan`, so a control that needs values on a later frame
copies them into the frame arena (`Utf8.AllocFrame<T>`) or takes an array. Check the built-in first:
`ui.Sparkline(values, width, height, color)`, `ui.Gauge`, `ui.Candlestick`, `ui.Knob` and friends
cover most of it.

Primitives inside a canvas: `DrawRect` (fill, border, gradient), `DrawShadow`, `DrawLine`,
`DrawPolyline`, `DrawPolygon`, `DrawCircle`, `DrawArc`, `DrawText`, `DrawIcon`, `DrawImage`,
`using (ui.Clip(rect))`, `ui.MeasureText(text, size)`.

## Long series and live data

**Rows are fine at any count**: `VirtualList` builds only the visible window, so a million rows cost
about 12 µs — the same as a thousand.

**A chart turns every point into geometry**: a million raw points is 46 ms per frame. Charts decimate
for you — `LineChart`/`BarChart` collapse anything longer than `ChartOpts.MaxPoints` (default: twice
the viewport width) into min/max pairs in the frame arena — so pass the whole feed.

```csharp
private readonly float[] _window = new float[SeriesDecimator.RequiredCapacity(400)];
private readonly Series[] _series = [new Series { Values = _window }];

int n = SeriesDecimator.Decimate(rawSamples, _window, 400);
_series[0].Count = n;                      // 0 means "all of _window"
ui.LineChart("live", _series, new ChartOpts { Height = 300 });
```

Reuse the buffer and set `Count`; slicing a buffer allocates every frame. A 600-point window over a
million-sample feed holds 144 fps with 0 B of managed allocation per frame.

## Pitfalls

| Symptom | Cause | Fix |
|---|---|---|
| A scope's children are drawn outside it | the scope is a `ref struct`, so `using var s = …` without a block still works — but dropping the `using` closes it immediately | keep the `using` (CUI002 catches a dropped scope) |
| A popover body draws while closed | `OpenScope` was ignored | guard with `if (scope)`, or use the callback overload (CUI003) |
| Focus and scroll jump between rows | the id came from the row position | `using (ui.Id(item.Id))` or `ui.Keyed(items, key)` (CUI004) |
| The window stops updating | the page changes on its own but never asks for frames | `WantsFrames => true`, or `ui.RequestRedraw()` |
| The UI freezes on a button press | something blocked in the frame | no `.Result`, `.Wait()`, `Thread.Sleep` in `Draw` (CUI001) |
| Frame rate sags | allocation per frame: LINQ, `new`, string concat | interpolate into a widget; hoist arrays with `static readonly` (CUI007) |
| An async handler deadlocks or the app crashes at shutdown | `ConfigureAwait(false)` or `async void` in a component | await normally in `Mutation`/`Run` (CUI005, CUI006) |
| A tour never advances, or Done leaves the card on screen | the widget's return was assigned to the `ref` it also writes | `ui.Tour(…, ref step)` writes the step; its `TourAction` result is what the user pressed |

IDE quick fixes are available for scope disposal (`CUI002`), conditional-body guards (`CUI003`),
repeated-widget keys (`CUI004`) and an awaited BCL `ConfigureAwait` (`CUI005`). Scope/id fixes are
individual, not fix-all, and are not offered if they introduce a compiler error. The id fix uses
that widget's receiver and the foreach item or a changing integer for-loop counter; a using
container's children remain inside it. Nested loops, while/do loops and ambiguous keys need an
explicit `ui.Id(item.Id)` or `ui.Keyed(items, key)` rewrite. Prefer stable model keys when rows can
move or be removed. Display text and existing-id targets are not collisions. The remaining rules
still need a manual rewrite.

Verifying:
- **Abandoned array literals cost arena space, not correctness.** Every array handed to CUI is copied
  into the frame arena, so `new[] { "a", "b" }` inside a frame is safe, just bytes copied per frame.
- **Strings are copied per frame, not interned.** Passing a `string` is fine; mutating one you already
  passed changes nothing until the next frame.
- **Never block in the draw callback.** A frame must return; CUI sleeps for you.

## Async work

`Draw` is synchronous: nothing in it can `await`. Start the task, publish the result into a field,
read the field next frame. `Mutation` is that shape, ready-made, and it keeps the loop awake while
the task runs:

```csharp
public sealed class Editor(Notes notes) : Component
{
    private readonly Mutation<Note> _save = new(token => notes.SaveAsync(token), _ => Saved = true);

    protected override void Draw(Ui ui)
    {
        if (ui.Button("Save", new() { Loading = _save.IsRunning })) _save.Run();
        if (_save.Error is { } error) ui.Badge(error.Message, new() { Tone = Tone.Danger });
    }
}
```

In a bare `CuiApp.Run` callback, the same rules by hand: start the task, keep a field, and call
`ui.RequestRedraw()` while it is in flight, because a sleeping window cannot be woken by a pool
thread. The task never touches `Ui`; it assigns a field, and the UI thread reads it next frame.
Assignment of a reference or an enum is atomic, so no lock is needed. For streaming input, a
background producer writes into a field or a `ConcurrentQueue`, `Draw` drains it, and
`RequestRedraw` keeps frames coming.

## Where it renders

| `CuiAppOptions.Renderer` | Behaviour |
|---|---|
| `Renderer.Auto` (default) | OpenGL 3.3 if the driver offers it, else the software rasterizer |
| `Renderer.OpenGL` | demand a GL context |
| `Renderer.Software` | force the CPU rasterizer (works in VMs and over remote desktop) |

Your draw code, layout, text shaping and geometry building always run on the CPU; the renderer only
fills triangles. So the frame budget is a CPU problem first, and `app.IsSoftwareRenderer` /
`app.Backend` tell you what you got. `CuiSurface` (tests, benchmarks, snapshots) always uses the
software rasterizer, which is what makes the numbers reproducible.

## Keyboard

Every component is keyboard operable already: Tab moves focus, arrows move within a control, Enter
activates, Esc closes overlays. Do not re-implement navigation.

```csharp
if (!ui.WantsKeyboard && ui.Shortcut(Mods.Primary, Key.K))
    paletteOpen = !paletteOpen;      // Ctrl+K, Cmd+K on macOS
```

## Theming

```csharp
CuiApp.Run(new CuiAppOptions { Theme = Theme.Dark, Accent = Accent.Violet, FollowSystemTheme = true }, Draw);
ui.Theme = Theme.Dark;                // from any frame; CUI animates the change
ui.Accent = Accent.Blue;
ui.ToggleTheme();                     // what a Ctrl+J dark-mode command is
```

## Translating the date names

The date picker, the calendars, the gantt and the file browser read their month, weekday and
"Today" text from one table. Fill in only what you translate; every entry left null falls back
to English, and `null` itself restores English:

```csharp
using var spanish = new Localization
{
    Months = ["enero", "febrero", "marzo", "abril", "mayo", "junio",
              "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"],
    MonthsShort = ["ene", "feb", "mar", "abr", "may", "jun", "jul", "ago", "sep", "oct", "nov", "dic"],
    Weekdays = ["lunes", "martes", "miércoles", "jueves", "viernes", "sábado", "domingo"],
    Today = "Hoy",
};

ui.SetLocalization(spanish);   // once; CUI reads the same table, and a name edited later, every frame
```

CUI keeps the pointer it is given and reads it every frame, so the table cannot live in the frame
arena: `Localization` owns one unmanaged block, rebuilt only when a name changes, and the surface
keeps the object alive after the call. One call is enough — change a name on the table and the next
frame shows it. The surface publishes a replaced block at its frame boundary and frees the one it
replaced after the frame that was drawing it, so disposing the table while it is published stays
safe. Weeks start on Monday, as in every CUI calendar. A component's own option (such as the month
calendar's `MonthNames`) wins over the table.

A table belongs to one surface: publishing the same one to a second app throws, because only the
owner is told when a block is replaced. Give each window its own table.

## Design rules

Follow these or the result looks generic:

- One primary action per screen; everything else secondary, outline or ghost.
- Group with proximity and a hairline first; a card only at a real boundary. No cards inside cards.
- Sentence case. A button is a verb and an object: "Create project", not "Submit".
- Colour carries meaning only when reporting state, always with a label or an icon.
- Never truncate what someone must read to act: wrap it or let it scroll.
- Spacing has rhythm: tight inside a group, clearly looser between groups.
- Report a result in place ("Saved") rather than in a toast.

## How to work on a UI

Write the test first, then make it pass. It is the fastest loop and the only one that leaves
evidence behind.

1. **Model and layout belong in a test.** `TestApp.Start(page, 800, 600)` draws frames on demand, so
   a click is deterministic, nothing sleeps, and the whole check costs milliseconds. Set the state
   you need directly (`page.Draft = "Milk"`) instead of clicking your way to it.
2. **Look at the pixels when the question is visual.** A semantic tree cannot tell you that a label
   overflows, that two things overlap, or that the contrast is unreadable. `app.SavePng(...)` from a
   test, or `dotnet cui png out.png` from the shell, then look at the file.
3. **`dotnet cui` is for reaching the app the tests cannot.** It runs the real entry point, its real
   startup code and its real size, with no edit to the app:

   ```sh
   dotnet cui tree          # what the first frame drew
   dotnet cui click Save    # the frame that reacted
   dotnet cui png shot.png  # the frame as an image
   ```

   Use it to see what a build actually starts up as, or to reproduce a bug in the shipped binary.
   Do not build a workflow on it: a command costs a process, and a suite of them cannot be rerun by
   anyone else.
4. **Edit and look, repeatedly.** For a running window, `dotnet watch` in one terminal and edit in
   the other; the app restarts or reloads and you look at it. That is a human loop — an agent should
   prefer the test, because a test says pass or fail without anybody looking.

The rule underneath all four: **one reproduction per task, in a file.** If a check mattered once, it
belongs in the test suite, not in a shell history.

## Testing without a window

`CuiSharp.Testing` drives the real component with no window. Frames happen when the test asks, so a
click is deterministic and nothing sleeps or polls.

```csharp
using var app = TestApp.Start(new TodoPage(), 800, 600);
app.FindButton("Save").Click();          // pointer over it, press frame, release frame
app.FindInput("draft").Click();
app.Type("Milk");
Assert.Equal("Milk", page.Draft);
Assert.Empty(app.Faults);
Assert.True(app.PaintedAnything());
```

- `Find`/`FindButton`/`FindInput`/`TryFind` search the last frame by a query; `app.Tree.Find(...)` and
  `app.Print()` show the nesting.
- A query is a label (`Save`), a kind and a label (`Button "Save"`), or a path
  (`Card "editor" > Save`). It ranks: the label *is* the query, then starts with it, then contains
  it — and a control beats a heading that repeats the text.
- `MoveTo`, `Press`, `Release`, `Type`, `Key`, `Focus`, `Frame(n)` are the whole input surface; each
  draws the frames the input needs.
- Time is not free: `Frame(n)` does not move the clock, so `app.Advance(TimeSpan, frames)` is how an
  animation gets somewhere, and `app.Settle()` draws until nothing wants another frame instead of
  the `sleep` loop every snapshot test used to write by hand. `app.Animating` is the flag behind it.
- Name a control by id, not by the words on it, or the test breaks when the UI is translated:
  `app.FindById("draft")`, `app.FindItem("pages", 1)` (the second tab, whatever it says), and
  `id:draft` in a query string. `Find`/`FindButton` still rank by label for exploration.
- `app.SavePng()` takes the pointer off the UI first, so a tooltip the last click left behind is not
  in the picture; `app.ParkPointer()` does the same without saving.
- A tab strip and a segmented control record each item, so `Find("Segment", "Dark")` and
  `click Markets` address the item, not the middle of the strip. Any other multi-item widget records
  the widget, and a test clicks it once.
- `app.Surface.LastFrame` is what that frame cost (`DrawMs`, `AllocatedBytes`) and whether it was
  live, without reading the process-wide `FrameStats`.
- `RawPixels`, `PaintedAnything`, `SavePng` assert on what was painted. Assert behaviour on the model
  and layout on the pixels.

## Frame numbers

The host measures every frame around `Draw`, so a footer costs nothing and an allocation shows up as
a number instead of a stutter:

```csharp
ui.Badge(FrameStats.IsLive ? $"{FrameStats.Fps:0} fps" : "idle", new() { Dot = FrameStats.IsLive });
ui.Secondary($"draw {FrameStats.DrawMs:0.00} ms · alloc {FrameStats.AllocatedBytes} B");
```

- `AllocatedBytes` is the app's own draw; it reads 0 and any other value is something in `Draw`.
- `FrameMs` is wall clock, so an idle app reports the gap since it last woke. `Fps` averages runs of
  frames only, and `IsLive` (a component's `WantsFrames`, or work in flight) says whether there is a
  rate at all. Show `idle` rather than a meaningless number.
- `CUI_TRACE=trace.txt` writes the same numbers to a file every 60 frames, for a `WinExe` with no
  console.

## Driving a running app

`dotnet cui` drives the app process itself: it builds the project in Release, runs it headless and
speaks one command per line. No window, no code added to the app.

```sh
dotnet cui tree                     # the controls of the last frame, nested
dotnet cui find Save                # one control: kind, label, rectangle
dotnet cui find id:draft            # or by the id the app gave it, in any language
dotnet cui click Save               # click it, print the frame that reacted
dotnet cui click Button "Save type" # be specific when a label is ambiguous
dotnet cui click editor > Save      # or name the path to the control
dotnet cui type Milk                # type into the focused control
dotnet cui key Tab                  # a key by name
dotnet cui png frame.png            # the frame as a PNG, to look at
dotnet cui --stdin < commands.txt   # many commands against one build, in order
```

`--stdin` is the cheapest way to explore an app you have not read: one build, one startup, a page of
answers. State carries across the commands because it is one process — but a run leaves nothing
behind, so anything that should be checked twice belongs in a test instead.

- `click` answers with the frame *after* the click; a transition may need `frame N` first.
- `--frames N` draws N frames before the command; `--app bin/App.exe` inspects a published build;
  `--no-build` reuses the last build; `--` passes the rest to the app.
- The app's own output — `Console.WriteLine`, a logger, a stack trace — reaches your stderr, so stdout
  is exactly the answer you asked for.
- Text in a script the bundled Latin faces do not cover draws nothing at all, with no box and no
  warning. `ui.UseSystemFontFallback("中文")` finds the face this machine has for it and registers it
  as a fallback; call it once at startup, and check the language you ship.
- The app serves this itself (`CUISHARP_HEADLESS=1`), so any app works.
  `<RuntimeHostConfigurationOption Include="CuiSharp.Headless" Value="false" Trim="true" />` removes
  it from a binary that wants the bytes back.

## Widget behaviour

Every component's behaviour — selection, focus, keyboard, what a return value means, when state
survives a frame — is in `widgets.md` and `widgets-more.md`, beside this guide. Read the page for the
widget instead of guessing from a signature: a data grid keeps its selection in your variables, a
dialog is modal, a dropzone returns the paths it received.

## Components

- **Text** — `Heading(text, 1..4)`, `Label`, `Secondary`, `Muted`, `Text(text, TextOpts)`, `Code`, `Link`
- **Actions** — `Button`, `IconButton`, `Chip`, `ButtonGroup()`, `HoldButton`, `CopyButton`
- **Selection** — `Checkbox(label, ref bool)`, `Switch`, `Radio(label, ref int, option)`, `RadioGroup`, `RadioCards`, `Slider(label, ref float, min, max)`, `RangeSlider`, `Rating`, `Segmented`
- **Input** — `Input(id, ref string, InputOpts)`, `TextArea`, `Search`, `Number`, `Select`, `DatePicker`, `TagInput`, `OtpInput`, `ColorPicker`
- **Navigation** — `Tabs`, `NavItem`, `NavSection`, `Breadcrumb`, `Pagination`, `Stepper`
- **Overlays** — `Menu`, `MenuItem`, `Popover`, `ContextMenu`, `Dialog`, `DialogFooter`, `Sheet`, `Toast`, `CommandPalette`, `Tooltip`
- **Feedback** — `Alert`, `Progress`, `ProgressRing`, `Meter`, `Spinner`, `Skeleton`, `Badge`, `Avatar`, `AvatarGroup`, `TimelineItem`, `EmptyState`
- **Data** — `Table(id, ReadOnlySpan<TableColumn>, ref TableSort, TableOpts)` + `TableRow`/`TableCell`, `Tree`, `ListItem`, `Accordion`, `VirtualList`, `Metric`, `TreeTable`, `DataGrid`
- **Charts** — `LineChart`, `BarChart`, `DonutChart`, `Sparkline`, `Candlestick`, `Gauge`, `RadialGauge`
- **Studio** — `Knob(id, ref float, min, max)`, `Fader(id, ref float, min, max)`, `LevelMeter(id, values)`
- **Calendar** — `MonthCalendar(id, ref Date, events, ref Date selected, MonthOpts)` returning `MonthResult`
- **Workspace** — `Terminal(id, terminal, TerminalOpts)`, `LogView(id, log, LogViewOpts)`, `HexView`, `DiffView`, `CodeEditor`, `FileBrowser`, `FilePicker`, `ImageViewer`, `Cropper`
- **Developer tools** — `TraceView(id, tracks, spans, ref TraceViewState, ref int selected)`, `Waterfall(id, requests, ref int selected)`, `GraphView(id, nodes, edges, ref int selected)`, `CommitGraph`, `Pipeline(id, stages, jobs, ref int selected)`, `Problems(id, items)`, `KvEditor(id, pairs, ref int count, capacity)`, `QueryBuilder(id, fields, nodes, ref int count, capacity)`, `SchedulePicker(id, ref string cron)`, `Keymap`, `TestExplorer`, `StackTrace`, `Blame`, `Coverage`, `RegexTester(id, ref pattern, patternCap, ref text, textCap, ref RegexFlags)`, `RequestBar(id, ref HttpMethod, ref url)` + `ResponseStatus`, `ApiExplorer`, `FeatureFlags(id, flags, envs)`, `Uptime`, `TimeHistogram` — each returns a typed result record, and the arrays it edits (`KvEditor`, `QueryBuilder`, `FeatureFlags`, the editors) come back through the `Span` you pass
- **Without a frame** — the pure helpers the widgets use, callable from a component or a test: `CuiRegex.TrySearch(…)` with its groups, `CuiCron.TryNext(…)`, `CuiHttp.ParseQuery`/`SetQuery`/`Reason`/`MethodName`, `CuiQuery.Text`/`Match`/`OperatorName`, `CuiFiles.ListDirectory`/`SortFlameSpans`/`SortTraceSpans`, `CuiMath.EvaluateCurve`/`EvaluateGradient`/`DiffLines`
- **AI interfaces** — `ChatComposer`, `ModelPicker`, `PromptSuggestions`, `Reasoning`, `ResponseActions`, `Citation`, `ContextMeter`, `AgentPlan`, `AgentStatus`
- **Onboarding** — `Tour(id, steps, ref int step, TourOpts)`; build each `TourStep.Target` from `ui.LastId` right after the control
- **Custom drawing** — inside `ui.Canvas(...)`, see [Custom controls](#custom-controls)
- **Per-id state** — `ui.State<T>(id)`, `ui.State<T>(id, onFree)` when it owns something CUI should release
- **Gates and clocks** — `using (ui.Disabled(flag)) { … }`, `ui.Time`, `ui.DeltaTime`, `ui.FrameCount`, `ui.MousePosition`, `ui.MouseDown`, `ui.MouseClicked`, `ui.KeyDown`, `ui.KeyPressed`, `ui.RectOf(id)`
- **Icons** — 168 constants generated from `cui_icons.h`: `Icons.Check`, `Icons.Plus`, `Icons.Search`, …

Each generated component has one line of documentation with its exact signature in `llms.txt`, and a
per-section page under `api/`.

## Files

Everything below is in this folder, beside this guide; nothing here points outside it.

| Need | File |
|---|---|
| this guide: the rules, the workflow, the pitfalls | `SKILL.md` |
| every signature, one line each | `llms.txt` |
| option structs and their fields | `api/Options.md` |
| records used as array items | `api/Records.md` |
| the whole API paged, one file per section | `api/` |
| what each widget does, and its keyboard | `widgets.md`, `widgets-more.md` |
| hosting: components, mutations, DI | `hosting.md` |
| which RIDs the package carries, and the glibc floor | `native-abi.md` |

Outside this folder the repository also has a complete solution or file-based C# app to start from (`dotnet new cui-app` / `cui-mini`), a
working page per component (`samples/CuiSharp.Gallery/`), and the measured cost per frame
(`docs/PERFORMANCE.md`).
