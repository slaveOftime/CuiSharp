namespace CuiSharp.App.Services;

/// <summary>One item on the list.</summary>
public sealed record TodoItem(int Id, string Title, bool Done);

/// <summary>
/// Business logic. It has no reference to CuiSharp, so it runs, and is tested,
/// without a window.
/// <para>
/// The rules are the interesting part of an app; the drawing is not. Keeping them
/// here means <c>dotnet test</c> covers them in milliseconds.
/// </para>
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

    /// <summary>
    /// Every item, oldest first. A read-only view of the list, so iterating it in
    /// <c>Draw</c> allocates nothing and stays correct if the list is edited.
    /// </summary>
    public IReadOnlyList<TodoItem> Items => _items;

    /// <summary>How many items are done. <see cref="Items"/>.Count minus <see cref="OpenCount"/>.</summary>
    public int DoneCount => _items.Count - OpenCount;

    /// <summary>How many items are still open.</summary>
    public int OpenCount
    {
        get
        {
            var open = 0;
            for (var i = 0; i < _items.Count; i++)
            {
                if (!_items[i].Done) open++;
            }
            return open;
        }
    }

    /// <summary>Adds an item. Blank titles are ignored and return null.</summary>
    public TodoItem? Add(string title)
    {
        var trimmed = title?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;

        var item = new TodoItem(_nextId++, trimmed, Done: false);
        _items.Add(item);
        return item;
    }

    /// <summary>Flips an item and returns its new state, or null when the id is unknown.</summary>
    public bool? Toggle(int id)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (_items[i].Id != id) continue;

            var updated = _items[i] with { Done = !_items[i].Done };
            _items[i] = updated;
            return updated.Done;
        }
        return null;
    }

    /// <summary>Removes every completed item and returns how many went.</summary>
    public int ClearCompleted() => _items.RemoveAll(i => i.Done);

    /// <summary>
    /// A typical asynchronous business call — a POST to a server. Replace the delay
    /// with a real <c>HttpClient</c> call and nothing else changes.
    /// </summary>
    /// <remarks>
    /// The snapshot is taken <em>before</em> the first <c>await</c>, which means it
    /// runs on the caller's thread while the UI is not drawing. Everything after the
    /// <c>await</c> runs on a pool thread and must not touch this class's state.
    /// Copy first, then await: that is the rule for any service an async call reads.
    /// </remarks>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        TodoItem[] snapshot = [.. _items];
        await Task.Delay(700, cancellationToken).ConfigureAwait(false);
        if (snapshot.Length is 0) throw new InvalidOperationException("Nothing to save.");
    }
}
