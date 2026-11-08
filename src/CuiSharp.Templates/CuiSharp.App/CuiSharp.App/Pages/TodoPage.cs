using CuiSharp;
using CuiSharp.App.Services;
using CuiSharp.Hosting;

namespace CuiSharp.App.Pages;

/// <summary>
/// The smallest page: state as fields, one mutation, and a <see cref="Draw"/> that rebuilds the
/// screen every frame. No view model, no binding, no notifications.
/// </summary>
public sealed class TodoPage(TodoService todos) : Component
{
    private static readonly string[] Filters = ["All", "Open", "Done"];

    // Created once, at construction. Error and IsRunning are current on read, so Draw never
    // polls and never awaits.
    private readonly Mutation _save = new(todos.SaveAsync);

    private string _draft = string.Empty;
    private int _filter;

    public override string Title => "Todos";

    protected override void Draw(Ui ui)
    {
        // Centred, and no wider than a column of text wants to be: a small app should look
        // deliberate on a large screen instead of pinned to one corner. The card fills the
        // height so the list is the only thing that scrolls.
        using (ui.Row(new() { Width = Size.Fill, Height = Size.Fill, Justify = Justify.Center }))
        using (ui.Column(new() { Width = Size.Fill, MaxWidth = 640, Height = Size.Fill, Gap = 14 }))
        using (ui.Card(new() { Gap = 12, Height = Size.Fill }))
        {
            Header(ui);
            Composer(ui);
            ui.Separator();
            Items(ui);
            ui.Separator();
            Actions(ui);
        }
    }

    private void Header(Ui ui)
    {
        using (ui.Row(new() { Gap = 10, Align = Align.Center }))
        {
            ui.Heading("Todos", 2);

            // Interpolated text goes straight into the frame arena, so a live count costs
            // nothing: no cached string, no notification that it changed.
            if (todos.OpenCount is 0) ui.Badge("All clear", new() { Tone = Tone.Success });
            else ui.Badge($"{todos.OpenCount} open", new() { Tone = Tone.Accent });

            ui.Spacer();
            ui.Segmented("filter", Filters, ref _filter, new() { Size = ControlSize.Sm });
        }
    }

    private void Composer(Ui ui)
    {
        using (ui.Row(new() { Gap = 8 }))
        {
            // Editable text is an ordinary string passed by ref: CUI edits the buffer and hands
            // the new value back on the frames it changed.
            InputResult result = ui.Input("draft", ref _draft, new() { Placeholder = "What needs doing?" });

            var add = ui.Button("Add", new() { Variant = ButtonVariant.Primary, Icon = Icons.Plus });
            if (add || result.Submitted) Add();
        }
    }

    private void Items(Ui ui)
    {
        // Reading Items.Count each time is deliberate: a button in the loop can add or remove an
        // item, and the list stays correct while it is iterated.
        int shown = 0;
        IReadOnlyList<TodoItem> items = todos.Items;

        // The list is the only part that scrolls when it grows, so the composer and the actions
        // stay where they were.
        using (ui.Scroll(new() { Id = "items", Height = Size.Fill, Gap = 4, Pad = 2 }))
        {
            for (var i = 0; i < items.Count; i++)
            {
                TodoItem item = items[i];
                if (_filter == 1 && item.Done) continue;
                if (_filter == 2 && !item.Done) continue;

                shown++;

                // Each row owns an id scope: two items with the same title keep separate state,
                // because the widget id is the item's, not the title's.
                using (ui.Id(item.Id))
                using (ui.Row(new() { Gap = 10, Align = Align.Center, PadY = 5 }))
                {
                    bool done = item.Done;
                    if (ui.Checkbox(item.Title, ref done)) todos.Toggle(item.Id);
                    ui.Spacer();
                    if (item.Done) ui.Badge("Done", new() { Tone = Tone.Success, Size = ControlSize.Sm });
                }
            }

            if (shown is 0)
            {
                ui.EmptyState(
                    _filter switch { 1 => "Nothing open", 2 => "Nothing done yet", _ => "Nothing to do" },
                    new()
                    {
                        Description = _filter switch
                        {
                            1 => "Every item is done. Add another, or switch the filter to Done.",
                            2 => "Tick an item and it will appear here.",
                            _ => "Add your first item above.",
                        },
                        Icon = Icons.CircleCheck,
                    });
            }
        }
    }

    private void Actions(Ui ui)
    {
        using (ui.Row(new() { Gap = 8, Justify = Justify.End, Align = Align.Center }))
        {
            // One button that reflects the operation it owns: it runs, it says it is running,
            // it reports the failure and offers the retry.
            if (_save.Error is { } error)
            {
                ui.Badge(error.Message, new() { Tone = Tone.Danger });
                if (ui.Button("Retry save", new() { Variant = ButtonVariant.Danger })) _save.Run();
            }
            else if (_save.IsRunning)
            {
                ui.Button("Saving", new() { Loading = true });
            }
            else if (ui.Button("Save", new() { Icon = Icons.Save }))
            {
                _save.Run();
            }

            if (ui.Button("Clear completed", new()
            {
                Variant = ButtonVariant.Ghost,
                Disabled = todos.DoneCount is 0,
            }))
            {
                todos.ClearCompleted();
            }
        }
    }

    private void Add()
    {
        if (todos.Add(_draft) is null) return;
        _draft = string.Empty;
        _filter = 0;   // a new item is open, so a filter that would hide it is not helpful
    }
}
