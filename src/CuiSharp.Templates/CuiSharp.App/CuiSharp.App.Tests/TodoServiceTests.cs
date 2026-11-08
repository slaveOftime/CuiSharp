using CuiSharp.App.Services;

namespace CuiSharp.App.Tests;

/// <summary>
/// Tests for the business rules. No window, no dependency injection, no CuiSharp:
/// this is the fastest and most valuable kind of test in the project, which is why
/// the rules live in <c>Services/</c> rather than inside a page.
/// </summary>
public class TodoServiceTests
{
    [Fact]
    public void Add_ignores_a_blank_title()
    {
        var todos = new TodoService();

        Assert.Null(todos.Add("   "));
        Assert.Null(todos.Add(""));
        Assert.Equal(3, todos.Items.Count);   // the three seeded items are untouched
    }

    [Fact]
    public void Add_trims_the_title_and_returns_the_new_item()
    {
        var todos = new TodoService();

        TodoItem? item = todos.Add("  write tests  ");

        Assert.NotNull(item);
        Assert.Equal("write tests", item.Title);
        Assert.False(item.Done);
        Assert.Equal(item.Id, todos.Items[^1].Id);
    }

    [Fact]
    public void Toggle_flips_one_item_and_reports_the_new_state()
    {
        var todos = new TodoService();
        TodoItem first = todos.Items[0];

        Assert.True(todos.Toggle(first.Id));
        Assert.True(todos.Items[0].Done);

        Assert.False(todos.Toggle(first.Id));
        Assert.False(todos.Items[0].Done);
    }

    [Fact]
    public void Items_stays_correct_when_a_row_removes_itself()
    {
        // What a "delete" button in the row loop does, and the reason Items is a
        // read-only list rather than a span: the list the view is iterating may
        // change while it is iterated.
        var todos = new TodoService();
        var seen = new List<int>();

        for (var i = 0; i < todos.Items.Count; i++)
        {
            TodoItem item = todos.Items[i];
            seen.Add(item.Id);
            if (seen.Count is 1) todos.ClearCompleted();   // removes nothing
            if (item.Id == todos.Items[0].Id) todos.Toggle(item.Id);
        }

        Assert.Equal(3, seen.Count);
        Assert.Equal(3, todos.Items.Count);
        Assert.Equal(2, todos.OpenCount);
    }

    [Fact]
    public void Toggle_returns_null_for_an_unknown_id()
    {
        var todos = new TodoService();

        Assert.Null(todos.Toggle(9999));
    }

    [Fact]
    public void OpenCount_follows_the_items()
    {
        var todos = new TodoService();
        var before = todos.OpenCount;

        TodoItem? added = todos.Add("count me");
        Assert.Equal(before + 1, todos.OpenCount);

        todos.Toggle(added!.Id);
        Assert.Equal(before, todos.OpenCount);

        todos.ClearCompleted();
        Assert.Equal(before, todos.OpenCount);
    }

    [Fact]
    public void ClearCompleted_removes_only_the_done_items()
    {
        var todos = new TodoService();
        var total = todos.Items.Count;
        todos.Toggle(todos.Items[0].Id);

        Assert.Equal(1, todos.ClearCompleted());
        Assert.Equal(total - 1, todos.Items.Count);
        Assert.DoesNotContain(todos.Items, item => item.Done);
    }

    [Fact]
    public async Task SaveAsync_completes_and_honours_cancellation()
    {
        var todos = new TodoService();

        await todos.SaveAsync();

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => todos.SaveAsync(cancel.Token));
    }
}
