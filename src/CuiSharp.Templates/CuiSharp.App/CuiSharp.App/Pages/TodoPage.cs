using CuiSharp;
using CuiSharp.App.Services;
using CuiSharp.Hosting;

namespace CuiSharp.App.Pages;

/// <summary>Draws the applied todo list and handles requests from the draft composer.</summary>
public sealed class TodoPage(TodoService todos) : Component
{
    public const string PageTitle = "Todos";

    private static readonly string[] Filters = ["All", "Open", "Done"];

    private readonly TodoComposer _composer = new();

    private int _filter;

    public override string Title => PageTitle;

    protected override void Draw(Ui ui)
    {
        using (ui.Row(new() { Width = Size.Fill, Height = Size.Fill, Justify = Justify.Center }))
        using (ui.Column(new() { Width = Size.Fill, MaxWidth = 640, Height = Size.Fill, Gap = 14 }))
        {
            Header(ui);
            if (_composer.Draw(ui) is { } request)
            {
                var accepted = todos.Add(request.Title) is not null;
                _composer.ResolveSubmission(accepted);
                if (accepted)
                {
                    _filter = 0;
                }

                // The header and input were already drawn; ask the host to show the result next frame.
                ui.RequestRedraw();
            }

            ui.Separator();
            Items(ui);
            ui.Separator();
            ClearCompleted(ui);
        }
    }

    private void Header(Ui ui)
    {
        using (ui.Row(new() { Gap = 10, Align = Align.Center }))
        {
            ui.Heading(PageTitle, 2);

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

    private void Items(Ui ui)
    {
        var shown = 0;

        using (ui.Scroll(new() { Id = "items", Height = Size.Fill, Gap = 4, Pad = 2 }))
        {
            // ui.Keyed indexes IReadOnlyList<T> without boxing an enumerator and scopes each
            // row's widget state to item.Id so duplicate titles never collide.
            foreach (var item in ui.Keyed(todos.Items, static item => item.Id))
            {
                if (IsHidden(item))
                {
                    continue;
                }

                shown++;

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

    private void ClearCompleted(Ui ui)
    {
        using var row = ui.Row(new() { Justify = Justify.End });
        if (ui.Button("Clear completed", new() { Variant = ButtonVariant.Ghost, Disabled = todos.DoneCount is 0 }))
        {
            todos.ClearCompleted();
        }
    }

    /// <summary>Whether the current filter hides <paramref name="item"/>.</summary>
    private bool IsHidden(TodoItem item) => _filter switch
    {
        1 => item.Done,
        2 => !item.Done,
        _ => false,
    };
}
