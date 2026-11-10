using System.Runtime.CompilerServices;
using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>
/// One thing a frame recorded: a control, or a container the app gave an id, with the text that
/// names it and where it was laid out.
/// </summary>
/// <remarks>
/// This is what a test finds a control by. A label is often the only stable name an app gives a
/// widget, so <see cref="Text"/> is the id when the widget takes one and the label when it does
/// not.
/// </remarks>
public readonly struct SemanticWidget
{
    /// <summary>The wrapper's name: <c>Button</c>, <c>Input</c>, <c>Card</c>.</summary>
    public readonly string Kind;

    /// <summary>The widget's id, or its label when it has no id.</summary>
    public readonly string Text;

    /// <summary>Where it was laid out, in points, in the frame that produced it.</summary>
    public readonly RectF Rect;

    /// <summary>
    /// How many containers were open around it: 0 for a widget at the top of the frame, 1 for
    /// one inside a card, and so on.
    /// </summary>
    public readonly int Depth;

    /// <summary>True for a container that recorded itself because the app named it.</summary>
    public readonly bool IsContainer;

    /// <summary>
    /// The id CUI laid this widget out under. Stable while the widget exists, and unrelated to
    /// the text, so it is what a test can hold on to when the label is translated.
    /// </summary>
    public readonly uint Id;

    internal SemanticWidget(string kind, string text, RectF rect, int depth, bool container, uint id)
    {
        Kind = kind;
        Text = text;
        Rect = rect;
        Depth = depth;
        IsContainer = container;
        Id = id;
    }

    /// <summary>The point at the middle: where a simulated click goes.</summary>
    public (float X, float Y) Center => (Rect.X + (Rect.W / 2f), Rect.Y + (Rect.H / 2f));

    /// <summary>True when the point is inside it.</summary>
    public bool Contains(float x, float y) =>
        x >= Rect.X && y >= Rect.Y && x < Rect.X + Rect.W && y < Rect.Y + Rect.H;

    /// <summary>The kind and the text, e.g. <c>Button "Save"</c>.</summary>
    public override string ToString() => Text.Length == 0 ? Kind : $"{Kind} \"{Text}\"";
}

/// <summary>
/// The entry point every wrapper calls to record what it drew, and the switch that turns that
/// recording on for a surface.
/// </summary>
/// <remarks>
/// <para>
/// Off by default, and off costs one inlined null test: the wrappers call <see cref="Note"/>,
/// which returns before it touches its arguments while no surface is recording. Attaching a
/// recorder to a <see cref="CuiSurface"/> turns it on for the frames that surface draws and for
/// nothing else — two surfaces on one thread keep their own frames.
/// </para>
/// <para>
/// CUI records a widget's rectangle when the frame's layout runs, so a widget reports its text
/// while the frame is built and the rectangle is read at <c>EndFrame</c>. That is also why the
/// first frame a test draws already has usable rectangles.
/// </para>
/// </remarks>
public static class WidgetRecorder
{
    [ThreadStatic] private static FrameRecorder? _current;

    /// <summary>How many surfaces on this thread are recording; > 0 means <see cref="Note"/> works.</summary>
    [ThreadStatic] private static int _recording;

    /// <summary>True while some surface on this thread records what it draws.</summary>
    public static bool Enabled => _recording > 0;

    /// <summary>
    /// Called by every generated wrapper (and by the hand-written ones) with the text that
    /// identifies the widget.
    /// </summary>
    /// <remarks>
    /// The kind is an interned literal the wrapper passes. Nothing is copied here: the text is
    /// frame memory, and it is decoded when the frame ends, so recording never makes a frame
    /// allocate.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Note(nint ui, string kind, UiText text)
    {
        FrameRecorder? frame = _current;
        if (frame is null) return;
        frame.Note(ui, kind, text);
    }

    /// <summary>
    /// Called by the wrappers whose widget draws one control per item (a tab strip, a segmented
    /// control): the strip is a container and each item is a control a test can click.
    /// </summary>
    /// <param name="ui">The frame.</param>
    /// <param name="kind">The wrapper: <c>Tabs</c>, <c>Segmented</c>.</param>
    /// <param name="item">What one item is called: <c>Tab</c>, <c>Segment</c>.</param>
    /// <param name="id">The widget's id, which its items' ids are derived from.</param>
    /// <param name="items">The item labels, in order.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void NoteItems(nint ui, string kind, string item, UiText id, ReadOnlySpan<string> items)
    {
        FrameRecorder? frame = _current;
        if (frame is null) return;
        frame.NoteItems(ui, kind, item, id, items);
    }

    /// <summary>A container opened; the widgets drawn from here on sit inside it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Push(nint ui, string kind, string? id)
    {
        FrameRecorder? frame = _current;
        if (frame is null) return;
        frame.Push(ui, kind, id);
    }

    /// <summary>A container opened under a markup id, which is the name most of them take.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Push(nint ui, string kind, UiText id)
    {
        FrameRecorder? frame = _current;
        if (frame is null) return;
        frame.Push(ui, kind, id);
    }

    /// <summary>
    /// A container closed. Counted even when nothing is recording: a scope may be disposed by a
    /// path that never opened it, and the counter is per frame anyway.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Pop()
    {
        FrameRecorder? frame = _current;
        if (frame is null) return;
        frame.Pop();
    }

    /// <summary>Starts or stops recording for <paramref name="recorder"/>'s surface.</summary>
    internal static void Attach(FrameRecorder recorder, bool enabled)
    {
        if (recorder.Attached == enabled) return;
        recorder.Attached = enabled;
        _recording += enabled ? 1 : -1;
        if (enabled) return;

        // A surface that stopped recording has no frame to report: keeping the old one would let
        // a test read controls from a frame it never recorded.
        recorder.Reset();
        if (ReferenceEquals(_current, recorder)) _current = null;
    }

    /// <summary>
    /// Makes <paramref name="recorder"/> the one the wrappers talk to, and starts its frame. The
    /// previous one comes back at <see cref="EndFrame"/>, so a surface inside a surface still
    /// records into the surface that is drawing.
    /// </summary>
    internal static FrameRecorder? BeginFrame(FrameRecorder recorder)
    {
        if (!recorder.Attached) return null;
        FrameRecorder? previous = _current;
        _current = recorder;
        recorder.Begin();
        return previous;
    }

    /// <summary>Publishes the frame that just ended and hands the wrappers back to the caller.</summary>
    internal static void EndFrame(FrameRecorder recorder, FrameRecorder? previous, nint ui)
    {
        if (!ReferenceEquals(_current, recorder)) return;
        recorder.End(ui);
        _current = previous;
    }
}

/// <summary>
/// One surface's recording of one frame: the widgets it drew and the order they nest in.
/// </summary>
/// <remarks>
/// Owned by a <see cref="CuiSurface"/>, not by the thread, so two apps in one test do not read
/// each other's frames and disposing one does not switch the other off.
/// </remarks>
internal sealed class FrameRecorder
{
    /// <summary>
    /// One widget, with its text still unpicked. The text of a widget is frame memory, so it is
    /// decoded once at <see cref="End"/>: decoding inside the frame would make the frame allocate,
    /// and a page whose own numbers read 0 B has to read 0 B here too.
    /// </summary>
    private readonly struct Pending
    {
        internal readonly string Kind;
        internal readonly nint Pointer;
        internal readonly int Length;
        internal readonly string? Text;
        internal readonly uint Id;
        internal readonly int Depth;
        internal readonly bool Container;

        internal Pending(string kind, nint pointer, int length, string? text, uint id, int depth, bool container)
        {
            Kind = kind;
            Pointer = pointer;
            Length = length;
            Text = text;
            Id = id;
            Depth = depth;
            Container = container;
        }

        /// <summary>Already a managed string, or a copy of the frame arena.</summary>
        internal string Decode() => Text ?? (Length == 0 ? "" : FromUtf8(Pointer, Length));
    }

    private readonly List<Pending> _pending = [];
    private readonly List<SemanticWidget> _entries = [];
    private readonly List<SemanticWidget> _controls = [];
    private int _depth;

    /// <summary>Whether this surface records; kept by <see cref="WidgetRecorder"/>.</summary>
    internal bool Attached { get; set; }

    /// <summary>Every entry of the frame that ended last, containers included, in drawing order.</summary>
    internal IReadOnlyList<SemanticWidget> Entries => _entries;

    /// <summary>Only the controls of the frame that ended last.</summary>
    internal IReadOnlyList<SemanticWidget> Controls => _controls;

    /// <summary>Forgets what was recorded, so nothing stale is offered as a frame.</summary>
    internal void Reset()
    {
        _pending.Clear();
        _entries.Clear();
        _controls.Clear();
        _depth = 0;
    }

    internal void Begin()
    {
        _pending.Clear();
        // A scope the caller leaked would otherwise indent the rest of the app.
        _depth = 0;
        _entries.Clear();
        _controls.Clear();
    }

    internal void Push(nint ui, string kind, string? id)
    {
        if (!string.IsNullOrEmpty(id)) Add(ui, kind, default, 0, id, container: true);
        _depth++;
    }

    internal void Push(nint ui, string kind, UiText id)
    {
        nint pointer = id.GetPointer(out int length);
        if (length > 0) Add(ui, kind, pointer, length, null, container: true);
        _depth++;
    }

    internal void Pop()
    {
        if (_depth > 0) _depth--;
    }

    internal void Note(nint ui, string kind, UiText text)
    {
        nint pointer = text.GetPointer(out int length);
        if (length == 0) return;
        Add(ui, kind, pointer, length, null, container: false);
    }

    /// <summary>
    /// Records a widget that draws one control per item — a tab strip, a segmented control — as
    /// a container holding one node per item.
    /// </summary>
    /// <remarks>
    /// The items are what a click lands on, so a test or an agent has to be able to name one:
    /// <c>click Markets</c> rather than "click the middle of the strip". Their ids are the ones
    /// CUI derives for them (<see cref="CuiHash"/>), which is how their rectangles are read back.
    /// </remarks>
    internal void NoteItems(nint ui, string kind, string item, UiText parent, ReadOnlySpan<string> items)
    {
        nint pointer = parent.GetPointer(out int length);
        if (length == 0) return;

        uint parentId = Native.MakeId(ui, pointer);
        Add(ui, kind, pointer, length, null, container: true);
        _depth++;
        for (int k = 0; k < items.Length; k++)
            _pending.Add(new Pending(item, 0, 0, items[k], CuiHash.Of(k, parentId), _depth, container: false));
        _depth--;
    }

    /// <summary>
    /// Publishes the frame that just ended, reading each widget's rectangle from the layout CUI
    /// has now finished.
    /// </summary>
    internal void End(nint ui)
    {
        if (_pending.Count == 0) return;

        foreach (Pending widget in _pending)
        {
            RectF rect = UiMarshal.FromNative(Native.GetRect(ui, widget.Id));
            var entry = new SemanticWidget(widget.Kind, widget.Decode(), rect, widget.Depth, widget.Container, widget.Id);
            _entries.Add(entry);
            if (!widget.Container) _controls.Add(entry);
        }

        _pending.Clear();
    }

    private static unsafe string FromUtf8(nint pointer, int length) =>
        System.Text.Encoding.UTF8.GetString((byte*)pointer, length);

    private void Add(nint ui, string kind, nint pointer, int length, string? text, bool container) =>
        _pending.Add(new Pending(kind, pointer, length, text, Native.LastId(ui), _depth, container));
}

/// <summary>
/// The one piece of CUI's internals the managed side has to mirror: the id a widget derives for
/// its <c>k</c>th item, so a recorded item can be read back like any other widget.
/// </summary>
/// <remarks>
/// <para>
/// CUI names an item of a tab strip or a segmented control <c>cui__hash(&amp;k, sizeof k, id)</c>,
/// and does not expose it. Recording the item means computing the same number, so the hash is
/// ported here, byte for byte: FNV-1a over the four bytes of the index, then the same avalanche.
/// </para>
/// <para>
/// A port that drifts would not fail loudly — it would stop finding items — so
/// <c>WidgetRecordingTests</c> clicks a recorded item on a real surface and asserts that the
/// widget moved. If upstream changes the hash, that test fails rather than the feature rotting.
/// </para>
/// </remarks>
internal static class CuiHash
{
    /// <summary>The id CUI gives item <paramref name="index"/> of the widget identified by <paramref name="parent"/>.</summary>
    internal static uint Of(int index, uint parent)
    {
        // An int is four little-endian bytes, and that is what the native hash reads.
        uint h = 2166136261u ^ parent;
        for (int i = 0; i < 4; i++)
        {
            h ^= (byte)(index >> (i * 8));
            h *= 16777619u;
        }

        h ^= h >> 16; h *= 0x7feb352du;
        h ^= h >> 15; h *= 0x846ca68bu;
        h ^= h >> 16;
        return h == 0 ? 1 : h;
    }
}
