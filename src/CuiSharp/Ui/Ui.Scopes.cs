namespace CuiSharp;

/// <summary>An always-open container. Use <c>using</c> to close it, including on exceptions.</summary>
public ref struct LayoutScope : IDisposable
{
    private nint _ui;
    private readonly ScopeKind _kind;

    /// <param name="ui">The frame the container belongs to.</param>
    /// <param name="kind">Which native container to close again.</param>
    /// <param name="name">The wrapper that opened it, for a recorded tree.</param>
    /// <param name="id">The name the app gave it, if any: only a named container is a node.</param>
    internal LayoutScope(nint ui, ScopeKind kind, string name, string? id = null)
    {
        _ui = ui;
        _kind = kind;
        // Recorded widgets are a tree, and the tree is the nesting the app wrote: a widget
        // drawn inside this container is one level deeper than the container itself.
        WidgetRecorder.Push(ui, name, id);
    }

    /// <param name="ui">The frame the container belongs to.</param>
    /// <param name="kind">Which native container to close again.</param>
    /// <param name="name">The wrapper that opened it, for a recorded tree.</param>
    /// <param name="id">The markup id the app gave it: the name a test finds it by.</param>
    internal LayoutScope(nint ui, ScopeKind kind, string name, UiText id)
    {
        _ui = ui;
        _kind = kind;
        WidgetRecorder.Push(ui, name, id);
    }

    /// <summary>Closes this container. Disposing the same instance again does nothing.</summary>
    public void Dispose()
    {
        nint ui = _ui;
        if (ui == 0) return;
        _ui = 0;
        WidgetRecorder.Pop();
        ScopeEnds.Close(ui, _kind);
    }
}

/// <summary>A conditional container. Use <c>using var scope = …; if (scope) { … }</c>.</summary>
public ref struct OpenScope : IDisposable
{
    private LayoutScope _scope;

    internal OpenScope(nint ui, ScopeKind kind, string name, string? id, bool open)
    {
        Open = open;
        _scope = open ? new LayoutScope(ui, kind, name, id) : default;
    }

    internal OpenScope(nint ui, ScopeKind kind, string name, UiText id, bool open)
    {
        Open = open;
        _scope = open ? new LayoutScope(ui, kind, name, id) : default;
    }

    /// <summary>Whether the native container opened and its body should be drawn.</summary>
    public bool Open { get; }

    /// <summary>Tests whether the body should be drawn.</summary>
    public static implicit operator bool(OpenScope scope) => scope.Open;

    /// <summary>Closes the container only if it opened.</summary>
    public void Dispose() => _scope.Dispose();
}
