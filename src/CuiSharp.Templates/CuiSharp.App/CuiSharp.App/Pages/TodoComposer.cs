using CuiSharp;

namespace CuiSharp.App.Pages;

internal readonly record struct AddRequested(string Title);

/// <summary>Owns the unfinished draft, not the applied list.</summary>
internal sealed class TodoComposer
{
    private string _draft = string.Empty;
    private bool _rejected;

    public AddRequested? Draw(Ui ui)
    {
        using var composer = ui.Column(new() { Id = "todo-composer", Gap = 4 });
        InputResult input;
        bool clicked;
        using (ui.Row(new() { Gap = 8, Align = Align.End }))
        {
            using (ui.Automation("draft"))
            {
                input = ui.Input("todo-title", ref _draft, new() { Label = "Task", Placeholder = "What needs doing?" });
            }

            using (ui.Automation("add-todo"))
            {
                clicked = ui.Button("Add", new() { Variant = ButtonVariant.Primary, Icon = Icons.Plus });
            }
        }

        if (input.Changed)
        {
            _rejected = false;
        }

        if (_rejected)
        {
            ui.Muted("Enter a task before adding it.");
        }

        return input.Submitted || clicked ? new AddRequested(_draft) : null;
    }

    public void ResolveSubmission(bool accepted)
    {
        _rejected = !accepted;
        if (accepted)
        {
            _draft = string.Empty;
        }
    }
}
