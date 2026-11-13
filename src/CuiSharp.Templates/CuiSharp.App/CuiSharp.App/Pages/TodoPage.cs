using CuiSharp;
using CuiSharp.App.Services;
using CuiSharp.Hosting;

namespace CuiSharp.App.Pages;

/// <summary>
/// Todo screen: state in fields, async save via <see cref="Mutation"/>, and a <see cref="Draw"/>
/// pass that rebuilds the view every frame.
/// </summary>
public sealed class TodoPage(TodoService todos) : Component
{
    private static readonly string[] Filters = ["All", "Open", "Done"];

    private readonly Mutation _save = new(todos.SaveAsync);

    private string _draft = string.Empty;
    private int _filter;

    public override string Title => "Todos";

    protected override void Draw(Ui ui)
    {
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

            if (todos.OpenCount is 0)
            {
                ui.Badge("All clear", new() { Tone = Tone.Success });
            }
            else
            {
                ui.Badge($"{todos.OpenCount} open", new() { Tone = Tone.Accent });
            }

            ui.Spacer();
            ui.Segmented("filter", Filters, ref _filter, new() { Size = ControlSize.Sm });
        }
    }

    private void Composer(Ui ui)
    {
        using (ui.Row(new() { Gap = 8 }))
        {
            var result = ui.Input("draft", ref _draft, new() { Placeholder = "What needs doing?" });
            var add = ui.Button("Add", new() { Variant = ButtonVariant.Primary, Icon = Icons.Plus });

            if (add || result.Submitted)
            {
                Add();
            }
        }
    }

    private void Items(Ui ui)
    {
        var shown = 0;
        var items = todos.Items;

        using (ui.Scroll(new() { Id = "items", Height = Size.Fill, Gap = 4, Pad = 2 }))
        {
            // Indexed rather than foreach: enumerating IReadOnlyList<T> boxes its enumerator every frame.
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (IsHidden(item))
                {
                    continue;
                }

                shown++;

                // Key each row by item.Id so duplicate titles never collide in CUI's widget state.
                using (ui.Id(item.Id))
                using (ui.Row(new() { Gap = 10, Align = Align.Center, PadY = 5 }))
                {
                    var done = item.Done;
                    if (ui.Checkbox(item.Title, ref done))
                    {
                        todos.Toggle(item.Id);
                    }

                    ui.Spacer();

                    if (item.Done)
                    {
                        ui.Badge("Done", new() { Tone = Tone.Success, Size = ControlSize.Sm });
                    }
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
            if (_save.Error is { } error)
            {
                ui.Badge(error.Message, new() { Tone = Tone.Danger });

                if (ui.Button("Retry save", new() { Variant = ButtonVariant.Danger }))
                {
                    _save.Run();
                }
            }
            else if (_save.IsRunning)
            {
                ui.Button("Saving", new() { Loading = true });
            }
            else if (ui.Button("Save", new() { Icon = Icons.Save }))
            {
                _save.Run();
            }

            var clear = new ButtonOpts { Variant = ButtonVariant.Ghost, Disabled = todos.DoneCount is 0 };
            if (ui.Button("Clear completed", clear))
            {
                todos.ClearCompleted();
            }
        }
    }

    private void Add()
    {
        if (todos.Add(_draft) is null)
        {
            return;
        }

        _draft = string.Empty;
        _filter = 0;
    }

    /// <summary>Whether the current filter hides <paramref name="item"/>.</summary>
    private bool IsHidden(TodoItem item) => _filter switch
    {
        1 => item.Done,
        2 => !item.Done,
        _ => false,
    };
}
