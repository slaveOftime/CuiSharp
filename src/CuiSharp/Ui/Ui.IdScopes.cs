namespace CuiSharp;

/// <summary>
/// Pushes an id onto CUI's id stack: every child created inside gets its ids
/// mixed with this one, so identical labels in different rows stay distinct.
/// Use <c>using</c>; disposal pops, including on exceptions.
/// </summary>
public ref struct IdScope : IDisposable
{
    private nint _ui;

    internal IdScope(nint ui) { _ui = ui; }

    /// <summary>Pops the id. Disposing the same instance again does nothing.</summary>
    public void Dispose()
    {
        nint ui = _ui;
        if (ui == 0) return;
        _ui = 0;
        Interop.Native.PopId(ui);
    }
}

/// <summary>The key for one item in <see cref="Ui.Keyed{T}"/>.</summary>
public delegate int KeyFn<T>(in T item);

/// <summary>
/// Enumerates items, pushing one id scope per item, so the same markup can draw
/// every row while each row keeps its own widget state.
/// <para>
/// The items are an array, not a span: the enumerator holds them for the length
/// of the loop, and a span would tie that loop to the span's own lifetime.
/// <para>
/// The key selector is a delegate: a capturing lambda allocates once per call,
/// so prefer a static lambda or a method group in the frame loop.
/// </para>
/// </para>
/// </summary>
/// <example><code>
/// foreach (var person in ui.Keyed(People, static (in Person p) => p.Id))
///     ui.ListItem(person.Name);
/// </code></example>
public ref struct KeyedScope<T>
{
    private readonly T[] _items;
    private readonly KeyFn<T> _key;
    private readonly nint _ui;
    private int _index;
    private bool _pushed;

    internal KeyedScope(nint ui, T[] items, KeyFn<T> key)
    {
        _ui = ui;
        _items = items;
        _key = key;
        _index = -1;
        _pushed = false;
        Current = default!;
    }

    /// <summary>The item for the current iteration.</summary>
    public T Current { get; private set; }

    public readonly KeyedScope<T> GetEnumerator() => this;

    /// <summary>Pops the previous item's id and pushes this one's.</summary>
    public bool MoveNext()
    {
        Pop();

        _index++;
        if ((uint)_index >= (uint)_items.Length) return false;

        Current = _items[_index];
        T item = Current;
        Interop.Native.PushIdInt(_ui, _key(in item));
        _pushed = true;
        return true;
    }

    /// <summary>Pops the last item's id scope, if one is still open.</summary>
    public void Dispose() => Pop();

    private void Pop()
    {
        if (!_pushed) return;
        _pushed = false;
        Interop.Native.PopId(_ui);
    }
}
