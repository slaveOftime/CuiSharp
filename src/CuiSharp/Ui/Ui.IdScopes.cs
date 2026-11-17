using System.Runtime.InteropServices;

namespace CuiSharp;

/// <summary>
/// Pushes an id onto CUI's id stack: every child created inside gets its ids
/// mixed with this one, so identical labels in different rows stay distinct.
/// Use <c>using</c>; disposal pops, including on exceptions.
/// </summary>
public ref struct IdScope : IDisposable
{
    private nint _ui;
    private readonly bool _automation;

    internal IdScope(nint ui, bool automation = false)
    {
        _ui = ui;
        _automation = automation;
    }

    /// <summary>Pops the id. Disposing the same instance again does nothing.</summary>
    public void Dispose()
    {
        nint ui = _ui;
        if (ui == 0) return;
        _ui = 0;
        if (_automation) WidgetRecorder.PopAutomation(ui);
        Interop.Native.PopId(ui);
    }
}

/// <summary>
/// A stable automation id for the widgets drawn inside it. Unlike <see cref="Ui.Id(UiText)"/>
/// this does not change CUI's native identity, so it can name a button whose label is already
/// unique without changing its state key.
/// </summary>
public ref struct AutomationScope : IDisposable
{
    private nint _ui;

    internal AutomationScope(nint ui) { _ui = ui; }

    /// <summary>Pops the automation id. Disposing twice does nothing.</summary>
    public void Dispose()
    {
        nint ui = _ui;
        if (ui == 0) return;
        _ui = 0;
        WidgetRecorder.PopAutomation(ui);
    }
}

/// <summary>The key for one item in <see cref="Ui.Keyed{T}(T[], KeyFn{T})"/>.</summary>
public delegate int KeyFn<T>(in T item);

/// <summary>
/// Enumerates items, pushing one id scope per item, so a natural <c>foreach</c> can draw every
/// row while each row keeps its own widget state and allocates 0 B.
/// <para>
/// Accepts <see cref="ReadOnlySpan{T}"/>, arrays, <see cref="List{T}"/> and
/// <see cref="IReadOnlyList{T}"/>. Lists are indexed directly rather than through
/// <see cref="IEnumerable{T}.GetEnumerator"/>, so enumerating an <see cref="IReadOnlyList{T}"/>
/// never boxes an enumerator on the frame heap.
/// </para>
/// <para>
/// When a key selector is passed, prefer a <c>static</c> lambda (<c>static item =&gt; item.Id</c>)
/// so the compiler caches the delegate once for the process. When omitted, the item's index is
/// used as its key.
/// </para>
/// </summary>
/// <example><code>
/// foreach (var item in ui.Keyed(todos.Items, static item => item.Id))
///     ui.Checkbox(item.Title, ref done);
/// </code></example>
public ref struct KeyedScope<T>
{
    private readonly ReadOnlySpan<T> _span;
    private readonly IReadOnlyList<T>? _list;
    private readonly Func<T, int>? _selectKey;
    private readonly KeyFn<T>? _key;
    private readonly nint _ui;
    private int _index;
    private bool _pushed;

    internal KeyedScope(nint ui, ReadOnlySpan<T> items, Func<T, int>? key = null)
    {
        _ui = ui;
        _span = items;
        _list = null;
        _selectKey = key;
        _key = null;
        _index = -1;
        _pushed = false;
        Current = default!;
    }

    internal KeyedScope(nint ui, T[] items, KeyFn<T> key)
    {
        _ui = ui;
        _span = items;
        _list = null;
        _selectKey = null;
        _key = key;
        _index = -1;
        _pushed = false;
        Current = default!;
    }

    internal KeyedScope(nint ui, IReadOnlyList<T> items, Func<T, int>? key = null)
    {
        _ui = ui;
        if (items is T[] array)
        {
            _span = array;
            _list = null;
        }
        else if (items is List<T> list)
        {
            _span = CollectionsMarshal.AsSpan(list);
            _list = null;
        }
        else
        {
            _span = default;
            _list = items;
        }

        _selectKey = key;
        _key = null;
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
        if (_list is not null)
        {
            if ((uint)_index >= (uint)_list.Count) return false;
            Current = _list[_index];
        }
        else
        {
            if ((uint)_index >= (uint)_span.Length) return false;
            Current = _span[_index];
        }

        T item = Current;
        int key = _selectKey is not null
            ? _selectKey(item)
            : _key is not null
                ? _key(in item)
                : _index;

        Interop.Native.PushIdInt(_ui, key);
        WidgetRecorder.PushAutomation(_ui, key);
        _pushed = true;
        return true;
    }

    /// <summary>Pops the last item's id scope, if one is still open.</summary>
    public void Dispose() => Pop();

    private void Pop()
    {
        if (!_pushed) return;
        _pushed = false;
        WidgetRecorder.PopAutomation(_ui);
        Interop.Native.PopId(_ui);
    }
}
