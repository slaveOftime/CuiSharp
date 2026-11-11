namespace CuiSharp;

/// <summary>An always-open container. Use <c>using</c> to close it, including on exceptions.</summary>
public unsafe ref struct LayoutScope : IDisposable
{
    private nint _ui;

    // The end function comes from the wrapper that opened the container, not from a table of
    // every container this library knows. A table would be one method that names all of them, so
    // every app that opens a box would link every container's end function and the translation
    // unit it lives in. Taken at the call site, an end function is reachable exactly when the
    // wrapper that calls it is — which is the rule the rest of the binding already follows.
    private readonly delegate*<nint, void> _end;

    /// <param name="ui">The frame the container belongs to.</param>
    /// <param name="end">The native call that closes this container, e.g. <c>&amp;Native.BoxEnd</c>.</param>
    /// <param name="name">The wrapper that opened it, for a recorded tree.</param>
    /// <param name="id">The name the app gave it, if any: only a named container is a node.</param>
    /// <param name="nativeId">The id CUI returned for this container, when its begin reports one.</param>
    /// <param name="deriveNamedId">True when CUI derives the id purely from the name (cui_make_id) but returns void.</param>
    internal LayoutScope(nint ui, delegate*<nint, void> end, string name, string? id = null, uint? nativeId = null, bool deriveNamedId = false)
    {
        _ui = ui;
        _end = end;
        // Recorded widgets are a tree, and the tree is the nesting the app wrote: a widget
        // drawn inside this container is one level deeper than the container itself.
        WidgetRecorder.Push(ui, name, id, nativeId, deriveNamedId);
    }

    /// <param name="ui">The frame the container belongs to.</param>
    /// <param name="end">The native call that closes this container.</param>
    /// <param name="name">The wrapper that opened it, for a recorded tree.</param>
    /// <param name="id">The markup id the app gave it: the name a test finds it by.</param>
    /// <param name="nativeId">Explicit id for a container that sets last_id only when it closes.</param>
    internal LayoutScope(nint ui, delegate*<nint, void> end, string name, UiText id, uint? nativeId = null)
    {
        _ui = ui;
        _end = end;
        WidgetRecorder.Push(ui, name, id, nativeId);
    }

    /// <summary>Closes this container. Disposing the same instance again does nothing.</summary>
    public void Dispose()
    {
        nint ui = _ui;
        if (ui == 0) return;
        _ui = 0;
        WidgetRecorder.Pop();
        _end(ui);
    }
}

/// <summary>A conditional container. Use <c>using var scope = …; if (scope) { … }</c>.</summary>
public unsafe ref struct OpenScope : IDisposable
{
    private LayoutScope _scope;

    internal OpenScope(nint ui, delegate*<nint, void> end, string name, string? id, bool open, uint? nativeId = null, bool deriveNamedId = false)
    {
        Open = open;
        _scope = open ? new LayoutScope(ui, end, name, id, nativeId, deriveNamedId) : default;
    }

    internal OpenScope(nint ui, delegate*<nint, void> end, string name, UiText id, bool open, uint? nativeId = null)
    {
        Open = open;
        _scope = open ? new LayoutScope(ui, end, name, id, nativeId) : default;
    }

    /// <summary>Whether the native container opened and its body should be drawn.</summary>
    public bool Open { get; }

    /// <summary>Tests whether the body should be drawn.</summary>
    public static implicit operator bool(OpenScope scope) => scope.Open;

    /// <summary>Closes the container only if it opened.</summary>
    public void Dispose() => _scope.Dispose();
}
