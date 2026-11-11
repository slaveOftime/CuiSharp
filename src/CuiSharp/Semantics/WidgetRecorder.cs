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

    /// <summary>
    /// True when the recorded id is one CUI laid this very widget out under — a container id its
    /// begin call returned, an id derived the way CUI derives it, or a kind proven to publish its
    /// own id as the last id. False means the id may be someone else's, so the rect is a hint
    /// about where to look, not a fact about this widget.
    /// </summary>
    public readonly bool HasOwnRect;

    /// <summary>
    /// True when CUI draws this widget on a floating layer of its own — a dialog, popover, menu,
    /// tooltip, toast or tour. The recorded nesting still shows where the app drew it, but its
    /// geometry belongs to its own layer, not to the containers it is nested under.
    /// </summary>
    public readonly bool Detached;

    /// <summary>
    /// Which native layout layer the widget was drawn on: the frame's own is 0, and each open
    /// floating scope — named or not — raises it. Two nodes share a layout only when they
    /// share a layer, so a dialog's buttons are never their nesting column's content.
    /// </summary>
    internal readonly int Layer;

    internal SemanticWidget(
        string kind, string text, RectF rect, int depth, bool container, uint id,
        bool hasOwnRect, bool detached, int layer)
    {
        Kind = kind;
        Text = text;
        Rect = rect;
        Depth = depth;
        IsContainer = container;
        Id = id;
        HasOwnRect = hasOwnRect;
        Detached = detached;
        Layer = layer;
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
/// Off by default, and off costs one inlined null test: the wrappers call <see cref="Note(nint, string, UiText)"/>,
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

    /// <summary>How many surfaces on this thread are recording; > 0 means <see cref="Note(nint, string, UiText)"/> works.</summary>
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
    /// Called by the wrapper of a widget CUI builds only when it is given items (a toggle group).
    /// <paramref name="itemCount"/> is what native's "no items, no widget" return turns on, and it
    /// is the only evidence that this call built anything: the last id proves nothing, because
    /// <c>cui_make_id</c> hashes only the text after <c>###</c>, so a widget drawn before this one
    /// can leave behind exactly the id this widget's name hashes to.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Note(nint ui, string kind, UiText text, int itemCount)
    {
        FrameRecorder? frame = _current;
        if (frame is null) return;
        frame.Note(ui, kind, text, itemCount);
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
    /// <remarks>
    /// <paramref name="nativeId"/> is the id CUI returned for this container when its begin
    /// reports one; <paramref name="deriveNamedId"/> marks a container whose id CUI derives
    /// purely from the name (cui_make_id) although its begin returns void.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Push(nint ui, string kind, string? id, uint? nativeId = null, bool deriveNamedId = false)
    {
        FrameRecorder? frame = _current;
        if (frame is null) return;
        frame.Push(ui, kind, id, nativeId, deriveNamedId);
    }

    /// <summary>A container opened under a markup id, which is the name most of them take.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Push(nint ui, string kind, UiText id, uint? nativeId = null, bool deriveNamedId = false)
    {
        FrameRecorder? frame = _current;
        if (frame is null) return;
        frame.Push(ui, kind, id, nativeId, deriveNamedId);
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
        internal readonly bool OwnRect;
        internal readonly bool Detached;
        internal readonly int Layer;

        internal Pending(
            string kind, nint pointer, int length, string? text, uint id, int depth, bool container,
            bool ownRect, bool detached, int layer)
        {
            Kind = kind;
            Pointer = pointer;
            Length = length;
            Text = text;
            Id = id;
            Depth = depth;
            Container = container;
            OwnRect = ownRect;
            Detached = detached;
            Layer = layer;
        }

        /// <summary>Already a managed string, or a copy of the frame arena.</summary>
        internal string Decode() => Text ?? (Length == 0 ? "" : FromUtf8(Pointer, Length));
    }

    private readonly List<Pending> _pending = [];
    private readonly List<SemanticWidget> _entries = [];
    private readonly List<SemanticWidget> _controls = [];
    private int _depth;

    /// <summary>A recording path that does not know how many items native was given.</summary>
    private const int UnknownItemCount = -1;

    // The native layout layer the widgets drawn right now belong to: a floating scope raises
    // it for everything drawn inside, named or not, because cui__float_begin detaches the
    // whole layer whether or not the app gave the scope a name. Each open depth remembers
    // the layer outside it, so Pop restores it even for a scope that recorded no entry. The
    // table has one entry per depth CUI nests layouts (CUI_PARENT_STACK), is allocated with
    // the recorder, and so costs a recording frame nothing.
    private int _layer;
    private readonly int[] _layerAtDepth = new int[GeometryProvenance.MaxNativeLayoutDepth];

    // Deeper than the table, and the recorder cannot save what to go back to, so it claims no
    // layer at all: every scope below moves to a number nothing else has ever used. A lost
    // boundary then costs an ancestor a union, it never lends a floating body to a flow's bounds.
    private int _unknownLayer;

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
        _layer = 0;
        _unknownLayer = 0;
    }

    internal void Begin()
    {
        _pending.Clear();
        // A scope the caller leaked would otherwise indent the rest of the app.
        _depth = 0;
        _layer = 0;
        _unknownLayer = 0;
        _entries.Clear();
        _controls.Clear();
    }

    /// <summary>
    /// Opens a scope's layer bookkeeping: a floating kind raises the layer for its whole body
    /// and returns true, so even an unnamed dialog is a boundary the frame ends at. Every depth
    /// CUI can nest is tracked; past that the scope gets a layer of its own, because the honest
    /// answer there is "not the flow's" rather than a restored value the recorder lost.
    /// </summary>
    private bool OpenLayer(string kind)
    {
        bool floating = GeometryProvenance.IsFloating(kind);
        if (_depth < _layerAtDepth.Length)
        {
            _layerAtDepth[_depth] = _layer;
            if (floating) _layer++;
        }
        else
        {
            _layer = --_unknownLayer;
        }

        return floating;
    }

    internal void Push(nint ui, string kind, string? id, uint? nativeId = null, bool deriveNamedId = false)
    {
        bool floating = OpenLayer(kind);
        if (!string.IsNullOrEmpty(id))
        {
            // The container's own native id when its begin reported one (a box - even an
            // explicit 0 there stays 0, so no stale last_id is ever borrowed), else the pure
            // cui_make_id of the app's name when that is how CUI derives it (a named scroll),
            // else the last_id the container set for itself - which is this container's own id
            // only for the kinds GeometryProvenance has read and proven (a card).
            uint native = nativeId ?? (deriveNamedId
                ? Native.MakeId(ui, Utf8.Get(id))
                : Native.LastId(ui));
            bool own = nativeId is not null || deriveNamedId || GeometryProvenance.HasOwnLastId(kind);
            _pending.Add(new Pending(
                kind, default, 0, id, native, _depth, container: true,
                own, floating, _layer));
        }
        _depth++;
    }

    internal void Push(nint ui, string kind, UiText id, uint? nativeId = null, bool deriveNamedId = false)
    {
        bool floating = OpenLayer(kind);
        nint pointer = id.GetPointer(out int length);
        if (length > 0)
        {
            uint native = nativeId ?? (deriveNamedId ? Native.MakeId(ui, pointer) : Native.LastId(ui));
            bool own = nativeId is not null || deriveNamedId || GeometryProvenance.HasOwnLastId(kind);
            _pending.Add(new Pending(
                kind, pointer, length, null, native, _depth, container: true,
                own, floating, _layer));
        }
        _depth++;
    }

    internal void Pop()
    {
        if (_depth == 0) return;
        _depth--;
        if (_depth < _layerAtDepth.Length) _layer = _layerAtDepth[_depth];
        else _layer = --_unknownLayer;
    }

    internal void Note(nint ui, string kind, UiText text) => Note(ui, kind, text, UnknownItemCount);

    internal void Note(nint ui, string kind, UiText text, int itemCount)
    {
        nint pointer = text.GetPointer(out int length);
        if (length == 0) return;
        // A group native skipped for want of items published no id, so the last id is whoever
        // drew before it - and it can be exactly this group's own hash, since cui_make_id folds
        // "label###id" down to the "###id" part. Only the count the wrapper handed native says
        // whether this call built the widget; a call that records without one claims nothing.
        bool own = GeometryProvenance.HasOwnLastId(kind)
            && (!GeometryProvenance.PublishesIdOnlyWhenBuilt(kind) || itemCount > 0);
        Add(ui, kind, pointer, length, null, container: false,
            own, GeometryProvenance.IsFloating(kind));
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
        bool floating = GeometryProvenance.IsFloating(kind);
        // The native strip builders return before creating a node or publishing an id when
        // given no items, and build at most 32 of them when given more: the count the wrapper
        // passed is the evidence, since no id on record can tell this call from the last one.
        bool built = items.Length > 0;
        Add(ui, kind, pointer, length, null, container: true,
            GeometryProvenance.HasOwnLastId(kind) && built, floating);
        _depth++;
        for (int k = 0; k < items.Length; k++)
        {
            // The item ids are CUI's own derivation (the mirrored CuiHash), not a last_id
            // anyone could have left behind, so each built item owns the rect read under its id.
            _pending.Add(new Pending(
                item, 0, 0, items[k], CuiHash.Of(k, parentId), _depth, container: false,
                ownRect: built && k < GeometryProvenance.MaxBuiltItems, floating, _layer));
        }

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
            var entry = new SemanticWidget(
                widget.Kind, widget.Decode(), rect, widget.Depth, widget.Container, widget.Id,
                widget.OwnRect, widget.Detached, widget.Layer);
            _entries.Add(entry);
            if (!widget.Container) _controls.Add(entry);
        }

        _pending.Clear();
    }

    private static unsafe string FromUtf8(nint pointer, int length) =>
        System.Text.Encoding.UTF8.GetString((byte*)pointer, length);

    private void Add(nint ui, string kind, nint pointer, int length, string? text, bool container, bool ownRect, bool detached) =>
        _pending.Add(new Pending(kind, pointer, length, text, Native.LastId(ui), _depth, container, ownRect, detached, _layer));
}

/// <summary>
/// What the id a wrapper records says about geometry, stamped by the recorder at the moment it
/// records. Everything starts unknown: a kind is <see cref="HasOwnLastId"/> only after the pinned
/// native source has been read and shows the widget laying itself out under the very id it
/// leaves as <c>last_id</c>, and <see cref="IsFloating"/> only after native shows it drawing on a
/// layer of its own (<c>cui__float_begin</c>), detached from the layout of whatever encloses it.
/// </summary>
/// <remarks>
/// <para>
/// The tables key on the kind literal the wrapper itself passes — no app can change it, and a
/// label never promotes or excuses a widget. Unknown is always safe: an unknown rect prints as
/// unverified and counts for nothing, and a floating widget not yet proven stays counted the way
/// it was before anyone had read the proof.
/// </para>
/// <para>
/// Entries were read out of the pinned <c>native/CUI</c> sources: an entry that drifts upstream
/// shows up as a missing or wrong rect in <c>LayoutInspectionTests</c>, never as a silent lie.
/// Growing these tables is a native-evidence task (the <c>sync-cui</c> runbook re-reads them on
/// a submodule bump); dropping an entry when evidence is doubted is always the honest default.
/// </para>
/// </remarks>
internal static class GeometryProvenance
{
    /// <summary>True when this wrapper's widget publishes its own layout id as the last id.</summary>
    internal static bool HasOwnLastId(string kind) => OwnLastId.Contains(kind);

    /// <summary>True when CUI draws this wrapper's widget on a layer detached from the flow.</summary>
    internal static bool IsFloating(string kind) => Floating.Contains(kind);

    /// <summary>
    /// True when the native builder skips the whole widget — no node, no published id — unless
    /// it is given items to build. For such a kind the count the wrapper handed native is the
    /// only proof the widget exists: an id on record cannot tell this call from the one before
    /// it, because <c>cui_make_id</c> hashes only the <c>###id</c> part of a name, so another
    /// widget's id can be exactly the one this widget's name hashes to.
    /// </summary>
    internal static bool PublishesIdOnlyWhenBuilt(string kind) => BuiltOnlyWithItems.Contains(kind);

    /// <summary>
    /// The most items the native strip builders ever build, however many are passed.
    /// </summary>
    internal const int MaxBuiltItems = 32;

    /// <summary>
    /// The deepest layout nesting CUI can reach: its parent stack is this deep, so a recorder
    /// table with one entry per depth covers every nesting its layout can build. Past it the
    /// recorder stops claiming a layer it cannot restore.
    /// </summary>
    /// <remarks>cui_internal.h: <c>#define CUI_PARENT_STACK 256</c>, and cui_layout.c cui__open
    /// pushes onto <c>ui->parents</c> only while <c>parent_depth</c> is below it.</remarks>
    internal const int MaxNativeLayoutDepth = 256;

    private static readonly HashSet<string> OwnLastId =
    [
        // cui_widgets.c cui__button_impl: the button node is created under id and the call
        // ends ui->last_id = id.
        "Button", "IconButton",
        // cui_widgets.c cui_link: the text node carries the id and last_id is set to it.
        "Link",
        // cui_widgets.c cui_card_begin: the card node is created under make_id(name) and
        // the call ends with if (id) ui->last_id = id.
        "Card",
        // cui_controls.c cui__toggle_row: the row node is created under the control's id and
        // published as last_id; checkbox, switch and radio are its three callers.
        "Checkbox", "Switch", "Radio",
        // cui_controls.c cui_chip_ex: the chip node is created under its id, last_id = id.
        "Chip",
        // cui_actions.c cui_toggle_group_ex: the group row node is make_id(id_str), and the
        // call ends ui->last_id = gid - but only when it built one, which only the item count
        // the wrapper passed can say (see BuiltOnlyWithItems).
        "ToggleGroup",
        // cui_controls.c cui__segments: the strip row node is make_id(id_str) — the same id
        // the item hashes derive from — and the call ends ui->last_id = id - but only when
        // it built one (see BuiltOnlyWithItems).
        "Segmented",
    ];

    // cui_actions.c cui_toggle_group_ex and cui_controls.c cui__segments both begin with
    // `if (count <= 0) return false;`, before any node is created or last_id touched: an
    // empty group records under whoever drew before it, and must not claim that rect.
    private static readonly HashSet<string> BuiltOnlyWithItems = ["Segmented", "ToggleGroup"];

    private static readonly HashSet<string> Floating =
    [
        // cui_overlay.c cui__modal_begin: dialogs and sheets float a full-screen layer.
        "Dialog", "Sheet",
        // cui_overlay.c and cui_disclosure.c cui__popup_begin: popups open their own layer.
        "Popover", "Menu", "ContextMenu", "HoverCard",
        // cui_chrome.c cui_menu_bar_menu_begin: the menu under a bar trigger is a popup layer;
        // the trigger itself is drawn by native and never recorded as its own node.
        "MenuBarMenu",
        // cui_actions.c cui_user_menu_begin: the account popup is a popup layer of its own.
        "UserMenu",
        // cui_treetable.c cui_path_bar_menu_begin: the overflow menu of a collapsed segment.
        "PathBarMenu",
        // cui_overlay.c: toasts and tooltips are drawn at frame end on layers of their own;
        // cui_tour.c draws its scrim and card inline, but each on a cui__float_begin layer.
        "Toast", "Tooltip", "Tour",
    ];
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
