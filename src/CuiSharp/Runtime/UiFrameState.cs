namespace CuiSharp;

/// <summary>A synchronous frame body. The UI cannot be captured or retained.</summary>
public delegate void DrawFn(Ui ui);

/// <summary>A canvas painter, invoked after layout with a fresh stack-only UI value.</summary>
public delegate void PaintFn(Ui ui, RectF rect);

// Managed state belongs to the runtime, not to the stack-only Ui. Native CUI
// stores each painter's slot until EndFrame/AppFrame runs the paint phase.
internal sealed class UiFrameState
{
    [ThreadStatic] private static UiFrameState? _painting;
    internal static UiFrameState? Painting => _painting;

    internal readonly List<PaintFn> Painters = new();

    // cui_reorder_begin keeps the order array until reorder_end, past the
    // P/Invoke pin. The frame arena copy is stable for the whole frame, and the
    // managed array is rewritten when the list closes.
    internal int[]? ReorderOrder;
    internal unsafe int* ReorderStorage;

    /// <summary>Per-id UTF-8 buffers for the pooled <c>ref string</c> text inputs.</summary>
    internal readonly TextFieldPool Text = new();

    /// <summary>
    /// The name table this surface published. Kept for two reasons: it is what keeps the table's
    /// unmanaged block alive while CUI holds the pointer, and it is the one a new table replaces.
    /// </summary>
    internal Localization? Localization;

    /// <summary>The block native was last handed, so publishing the same table again is a no-op.</summary>
    internal nint LocalizationBlock;

    internal void Reset()
    {
        Painters.Clear();
        ReorderOrder = null;
        unsafe { ReorderStorage = null; }
        Text.BeginFrame();

        // The frame that could still have been reading a replaced table has painted by now.
        Localization?.ReleaseRetired();
    }
    internal PaintScope EnterPaint() => new(this);

    internal readonly ref struct PaintScope : IDisposable
    {
        private readonly UiFrameState? _previous;

        internal PaintScope(UiFrameState state)
        {
            _previous = _painting;
            _painting = state;
        }

        public void Dispose() => _painting = _previous;
    }
}
