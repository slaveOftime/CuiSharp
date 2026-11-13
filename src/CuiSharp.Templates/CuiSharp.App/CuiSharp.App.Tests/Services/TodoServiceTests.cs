using CuiSharp.App.Services;

namespace CuiSharp.App.Tests.Services;

/// <summary>
/// Unit tests for <see cref="TodoService"/> domain rules.
/// </summary>
public class TodoServiceTests
{
    [Fact]
    public void AddIgnoresBlankTitles()
    {
        var todos = new TodoService();

        Assert.Null(todos.Add("   "));
        Assert.Null(todos.Add(""));
        Assert.Equal(3, todos.Items.Count);
    }

    [Fact]
    public void AddTrimsTitleAndAppendsOpenItem()
    {
        var todos = new TodoService();

        var item = todos.Add("  write tests  ");

        Assert.NotNull(item);
        Assert.Equal("write tests", item.Title);
        Assert.False(item.Done);
        Assert.Equal(item.Id, todos.Items[^1].Id);
    }

    [Fact]
    public void ToggleFlipsItemStateAndReturnsNullWhenUnknown()
    {
        var todos = new TodoService();
        var first = todos.Items[0];

        Assert.True(todos.Toggle(first.Id));
        Assert.True(todos.Items[0].Done);

        Assert.False(todos.Toggle(first.Id));
        Assert.False(todos.Items[0].Done);

        Assert.Null(todos.Toggle(9999));
    }

    [Fact]
    public void ItemsStaysConsistentWhenModifiedDuringIndexedIteration()
    {
        var todos = new TodoService();
        var seen = new List<int>();

        for (var i = 0; i < todos.Items.Count; i++)
        {
            var item = todos.Items[i];
            seen.Add(item.Id);

            if (seen.Count is 1)
            {
                todos.ClearCompleted();
            }

            if (item.Id == todos.Items[0].Id)
            {
                todos.Toggle(item.Id);
            }
        }

        Assert.Equal(3, seen.Count);
        Assert.Equal(3, todos.Items.Count);
        Assert.Equal(2, todos.OpenCount);
    }

    [Fact]
    public void OpenAndDoneCountsTrackItemState()
    {
        var todos = new TodoService();
        var before = todos.OpenCount;

        var added = todos.Add("count me");

        Assert.Equal(before + 1, todos.OpenCount);
        Assert.Equal(0, todos.DoneCount);

        todos.Toggle(added!.Id);

        Assert.Equal(before, todos.OpenCount);
        Assert.Equal(1, todos.DoneCount);

        todos.ClearCompleted();

        Assert.Equal(before, todos.OpenCount);
        Assert.Equal(0, todos.DoneCount);
    }

    [Fact]
    public void ClearCompletedRemovesOnlyDoneItems()
    {
        var todos = new TodoService();
        var total = todos.Items.Count;

        todos.Toggle(todos.Items[0].Id);

        Assert.Equal(1, todos.ClearCompleted());
        Assert.Equal(total - 1, todos.Items.Count);
        Assert.DoesNotContain(todos.Items, item => item.Done);
    }

    [Fact]
    public async Task SaveAsyncCompletesAndHonoursCancellation()
    {
        var todos = new TodoService();

        await todos.SaveAsync();

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => todos.SaveAsync(cancel.Token));
    }
}
