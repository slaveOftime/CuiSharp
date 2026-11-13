namespace CuiSharp.App.Services;

/// <summary>One item on the list.</summary>
public sealed record TodoItem(int Id, string Title, bool Done);

/// <summary>
/// Domain rules for the todo list, independent of CuiSharp so tests run without a window.
/// </summary>
public sealed class TodoService
{
    private readonly List<TodoItem> _items = [];
    private int _nextId = 1;

    public TodoService()
    {
        Add("Sketch the onboarding flow");
        Add("Review the theme tokens");
        Add("Ship release 0.1");
    }

    /// <summary>Every item, oldest first.</summary>
    public IReadOnlyList<TodoItem> Items => _items;

    /// <summary>How many items are done.</summary>
    public int DoneCount => _items.Count - OpenCount;

    /// <summary>How many items are still open.</summary>
    public int OpenCount
    {
        get
        {
            var open = 0;
            for (var i = 0; i < _items.Count; i++)
            {
                if (!_items[i].Done)
                {
                    open++;
                }
            }

            return open;
        }
    }

    /// <summary>Adds an item. Blank titles are ignored and return null.</summary>
    public TodoItem? Add(string title)
    {
        var trimmed = title?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var item = new TodoItem(_nextId++, trimmed, Done: false);
        _items.Add(item);
        return item;
    }

    /// <summary>Flips an item and returns its new state, or null when the id is unknown.</summary>
    public bool? Toggle(int id)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (_items[i].Id != id)
            {
                continue;
            }

            var updated = _items[i] with { Done = !_items[i].Done };
            _items[i] = updated;
            return updated.Done;
        }

        return null;
    }

    /// <summary>Removes every completed item and returns how many were removed.</summary>
    public int ClearCompleted() => _items.RemoveAll(item => item.Done);

    /// <summary>
    /// Asynchronous save operation. Captures a snapshot before awaiting so pool continuation code
    /// never reads mutable UI-thread state.
    /// </summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = _items.ToArray();
        await Task.Delay(700, cancellationToken).ConfigureAwait(false);

        if (snapshot.Length is 0)
        {
            throw new InvalidOperationException("Nothing to save.");
        }
    }
}
