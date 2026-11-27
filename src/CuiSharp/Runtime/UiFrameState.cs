using System.Runtime.InteropServices;
using CuiSharp.Hosting;
using CuiSharp.Interop;

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
    [ThreadStatic] private static UiFrameState? _drawing;
    internal static UiFrameState? Drawing => _drawing;
    private UiFrameState? _drawBelow;
    private bool _drawingThis;

    // Installed by the first background operation this host starts. The interface leaves the
    // dispatcher and its queues out of a plain window's AOT image.
    internal IFrameWork? Work;
    internal nint WakeWindow;
    internal WindowTitleBar? TitleBar;
    internal bool WantsFrames;
    internal List<Exception>? Faults;
    internal bool IsLive => WantsFrames || Work is { NextFrameSeconds: >= 0f };

    internal readonly List<PaintFn> Painters = new();
    internal FrameCallbacks.WidgetSlots? Callbacks;

    // cui_reorder_begin keeps the order array until reorder_end, past the
    // P/Invoke pin. The frame arena copy is stable for the whole frame, and the
    // managed array is rewritten when the list closes.
    internal int[]? ReorderOrder;
    internal unsafe int* ReorderStorage;

    /// <summary>Per-id UTF-8 buffers for the pooled <c>ref string</c> text inputs.</summary>
    internal readonly TextFieldPool Text = new();

    // Context-owned, not frame-local: both successful and unsuccessful font searches are memoized.
    internal SystemFontFallbackState? SystemFontFallbacks;

    /// <summary>
    /// Whether the frame drawing right now has called <c>Ui.MarkReady</c>. Cleared at every
    /// <see cref="Reset"/>, so a frame that goes back to loading is not ready on the strength of
    /// an earlier frame's mark.
    /// </summary>
    internal bool FrameReady;

    /// <summary>
    /// Whether the latest completed frame was ready. Published at the very end of
    /// <c>EndFrame</c> — after CUI's deferred painters and the recorder are done — so a mark
    /// made from a painter counts for the frame being painted, and mid-frame a reader still
    /// sees the previous completed frame's verdict.
    /// </summary>
    internal bool LastFrameReady;

    /// <summary>
    /// The name table this surface published. Kept for two reasons: it is what keeps the table's
    /// unmanaged block alive while CUI holds the pointer, and it is the one that is asked for the
    /// new block when a name changed.
    /// </summary>
    internal Localization? Localization;

    /// <summary>The block native was last handed, so re-syncing an unchanged table is a no-op.</summary>
    internal nint LocalizationBlock;

    // A table that was disposed while published: native still reads its block, so the pointer is
    // cleared at the next boundary, and only then can the block go.
    private bool _localizationClear;

    // Blocks native may still be reading — replaced or orphaned during the frame just gone. They
    // are freed at the boundary that follows it, never inside the frame that dropped them.
    private readonly List<nint> _retiredBlocks = [];

    /// <summary>
    /// Frame start: tells the context which table to read, then frees the blocks only the frame
    /// that already presented could have been reading. Everything before this point in the frame
    /// sees the same table the rest of it does.
    /// </summary>
    internal void SyncLocalization(nint ctx)
    {
        if (_localizationClear)
        {
            Native.SetStrings(ctx, 0);
            _localizationClear = false;
        }

        if (Localization is { } table)
        {
            nint block = table.Publish();
            if (block != LocalizationBlock)
            {
                LocalizationBlock = block;
                Native.SetStrings(ctx, block);
            }
        }

        FreeRetired();
    }

    /// <summary>Keeps a replaced block until the frame that could still read it has painted.</summary>
    internal void Retire(nint block) => _retiredBlocks.Add(block);

    /// <summary>
    /// Takes the block of a table that is going away. Native reads it until the next boundary, so
    /// the pointer is cleared there and the block freed with the rest.
    /// </summary>
    internal void Orphan(Localization table, nint block)
    {
        if (!ReferenceEquals(Localization, table)) return;

        Localization = null;
        LocalizationBlock = 0;
        _localizationClear = true;
        if (block != 0) _retiredBlocks.Add(block);
    }

    /// <summary>
    /// Lets the published table go when the surface is disposed, so the app may dispose it too.
    /// The context is already gone here, so there is nothing to tell native.
    /// </summary>
    internal void Release()
    {
        if (_drawingThis) ExitDraw();
        CloseWork();
        Localization?.Detach(this);
        Localization = null;
        LocalizationBlock = 0;
        _localizationClear = false;
        FrameReady = false;
        LastFrameReady = false;
        SystemFontFallbacks?.ReleaseAfterContextDestroyed();
        SystemFontFallbacks = null;
        Callbacks?.Release();
        Callbacks = null;
        FreeRetired();
    }

    internal void Reset()
    {
        FrameReady = false;
        TitleBar?.Reset();
        Painters.Clear();
        Callbacks?.Reset();
        ReorderOrder = null;
        unsafe { ReorderStorage = null; }
        Text.BeginFrame();
    }
    internal PaintScope EnterPaint() => new(this);

    internal DrawScope EnterDraw(nint context, bool window)
    {
        // A modal OS resize can re-enter the same window callback. Only the outer draw owns
        // the boundary: no re-entrant completion delivery into a callback already on the stack.
        if (_drawingThis) return default;
        _drawingThis = true;
        _drawBelow = _drawing;
        _drawing = this;
        WantsFrames = false;
        try
        {
            Work?.Drain();
            return new DrawScope(this, context, window);
        }
        catch
        {
            ExitDraw();
            throw;
        }
    }

    internal void CloseWork() => Work?.Close();

    private void ExitDraw()
    {
        // Explicit surfaces may finish out of order. Unlink only this one so ending the
        // first cannot replace the active second, or resurrect a frame that already ended.
        if (_drawing == this) _drawing = _drawBelow;
        else
        {
            for (UiFrameState? above = _drawing; above is not null; above = above._drawBelow)
            {
                if (above._drawBelow != this) continue;
                above._drawBelow = _drawBelow;
                break;
            }
        }

        _drawBelow = null;
        _drawingThis = false;
    }

    internal readonly struct DrawScope(UiFrameState frame, nint context, bool window) : IDisposable
    {
        public void Dispose()
        {
            if (frame is null) return;
            frame.ExitDraw();
            if (context == 0) return;

            // A manually driven surface only requests animation frames, never wall-clock
            // worker waits. Read demand once; a worker cannot touch the native context.
            float seconds = window ? frame.Work?.NextFrameSeconds ?? -1f : -1f;
            if (frame.WantsFrames || seconds == 0f) Native.RequestRedraw(context);
            else if (seconds > 0f) Native.RequestWakeup(context, seconds);
        }
    }

    private unsafe void FreeRetired()
    {
        if (_retiredBlocks.Count == 0) return;
        foreach (nint block in _retiredBlocks) NativeMemory.Free((void*)block);
        _retiredBlocks.Clear();
    }

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

// The core owns the draw boundary, but only the optional hosting layer implements its work.
internal interface IFrameWork
{
    float NextFrameSeconds { get; }
    void Drain();
    void Close();
}
