using System.Diagnostics;
using CuiSharp.Interop;

namespace CuiSharp.Hosting;

/// <summary>
/// One control of a frame, as something to act on: hover it, click it, read where it is.
/// </summary>
/// <remarks>
/// A target is only good for the frame that produced it — the next frame may lay the widget out
/// somewhere else, or not draw it at all. Ask the <see cref="HeadlessApp"/> again after the
/// interface changes.
/// </remarks>
public readonly struct WidgetTarget
{
    private readonly HeadlessApp _app;

    internal WidgetTarget(HeadlessApp app, SemanticNode node)
    {
        _app = app;
        Kind = node.Kind;
        Text = node.Text;
        Rect = node.Rect;
    }

    /// <summary>The wrapper that drew it: <c>Button</c>, <c>Input</c>, <c>ListItem</c>.</summary>
    public string Kind { get; }

    /// <summary>The id, or the label when the widget takes no id.</summary>
    public string Text { get; }

    /// <summary>Where it was laid out in the frame it was found in.</summary>
    public RectF Rect { get; }

    /// <summary>Moves the pointer to the middle of the widget and draws a frame.</summary>
    public WidgetTarget Hover()
    {
        _app.MoveTo(Rect.X + (Rect.W / 2f), Rect.Y + (Rect.H / 2f));
        return this;
    }

    /// <summary>
    /// Clicks the widget: pointer over it, a press frame, a release frame. The widget reports
    /// the click on the release, so the frame after this one shows the result.
    /// </summary>
    public WidgetTarget Click()
    {
        Hover();
        _app.Press();
        _app.Release();
        return this;
    }

    /// <summary>Clicks the widget and returns the app, whose last frame reacted to it.</summary>
    public HeadlessApp ClickAndFrame()
    {
        Click();
        return _app.Frame();
    }

    /// <inheritdoc />
    public override string ToString() => $"{Kind} \"{Text}\" at {Rect}";
}

/// <summary>
/// An app drawn without a window: frames you drive, controls you find by label, and the pixels
/// they produced.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here needs a window, a message loop or a real clock: frames happen when the caller
/// asks for one, which is what makes a test deterministic and an agent able to screenshot a
/// frame. <c>CuiSharp.Testing</c> names this <c>TestApp</c> for tests; the agent protocol in
/// <see cref="HeadlessLoop"/> drives the same class.
/// </para>
/// <para>
/// It records the widgets each frame draws, which is how <c>Find</c> works: a control's label
/// (or id) is the only stable name an app gives it.
/// </para>
/// </remarks>
/// <example><code>
/// using var app = HeadlessApp.Start(new CounterPage(), 800, 600);
/// app.FindButton("Add").Click();
/// Assert.Equal(1, page.Count);
/// app.SavePng("frame.png");
/// </code></example>
public class HeadlessApp : IDisposable
{
    private readonly CuiSurface _surface;
    private readonly Component? _root;
    private readonly byte[] _pixels;
    private readonly List<string> _faults = [];
    private bool _disposed;

    /// <summary>Owns a surface; a derived type supplies the constructor it needs.</summary>
    /// <param name="surface">The surface to draw into, already created at the right size.</param>
    /// <param name="root">The component to draw, or null when a draw callback is used.</param>
    /// <param name="draw">Drawn when there is no component.</param>
    protected HeadlessApp(CuiSurface surface, Component? root, DrawFn? draw = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        _surface = surface;
        _root = root;
        _draw = draw ?? NoDraw;
        _pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        // Recording is on for the life of the app: it is how Find sees a frame.
        _surface.RecordWidgets = true;
    }

    /// <summary>The surface underneath: pixels, viewport and the software rasterizer.</summary>
    public CuiSurface Surface => _surface;

    /// <summary>Logical width in points.</summary>
    public int Width => _surface.Width;

    /// <summary>Logical height in points.</summary>
    public int Height => _surface.Height;

    /// <summary>Exceptions a component threw while drawing; an empty list is a healthy frame.</summary>
    public IReadOnlyList<string> Faults => _faults;

    /// <summary>
    /// Whether the latest completed frame declared its data ready with
    /// <see cref="Ui.MarkReady"/>. A frame that did not mark itself is not ready, whatever the
    /// frame before it claimed.
    /// </summary>
    public bool Ready => _surface.LastFrameReady;

    /// <summary>
    /// The gap between the frames of a bounded wait. Frames are free but not instant, and a wait
    /// that never sleeps turns into a busy spin between real draws; a few milliseconds keeps the
    /// core honest without making a satisfied wait slow.
    /// </summary>
    private static readonly TimeSpan WaitPace = TimeSpan.FromMilliseconds(2);

    /// <summary>
    /// Draws real frames until one completes that called <see cref="Ui.MarkReady"/>, or until
    /// <paramref name="timeout"/> has passed on the wall clock. Every frame drains completed
    /// background work first, so data that arrives on a pool thread becomes visible on the frame
    /// that draws after it — which is what a wait is for. The latest completed frame may answer
    /// without another one being drawn: a wait that needs no frames reports <see cref="WaitOutcome.Frames"/>
    /// of 0 — and a window already spent when the first frame would have started is a miss that
    /// drew nothing. A draw cannot be interrupted, but a condition that turns true only after the
    /// window has passed is reported as a miss, not as a late success.
    /// </summary>
    /// <param name="timeout">How long to keep drawing. Must be finite and positive.</param>
    /// <param name="cancellationToken">
    /// Ends the wait early: it is asked before anything draws, after every draw (ahead of the
    /// frame's own verdict), and after pacing before the next frame begins.
    /// </param>
    public WaitOutcome WaitReady(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Wait(() => Ready, timeout, cancellationToken);

    /// <summary>
    /// Draws real frames until one draws a control that answers <paramref name="query"/> (the
    /// same vocabulary as <see cref="Find(string)"/>), or until <paramref name="timeout"/> passes.
    /// The latest completed frame is asked first, so a wait for what is already on screen draws
    /// no frames at all.
    /// </summary>
    /// <param name="query">The label, kind-and-label or path to wait for, taken verbatim.</param>
    /// <param name="timeout">How long to keep drawing. Must be finite and positive.</param>
    /// <param name="cancellationToken">
    /// Ends the wait early: it is asked before anything draws, after every draw (ahead of the
    /// frame's own verdict), and after pacing before the next frame begins.
    /// </param>
    public WaitOutcome WaitFor(string query, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(query);
        return Wait(() => TryFind(query, out _), timeout, cancellationToken);
    }

    /// <summary>
    /// The wait both public waits share: ask the completed frame, then draw, look, stop by the
    /// clock. The condition is read only between completed frames, so it never observes a
    /// half-drawn frame, and nothing here touches a <see cref="Ui"/> from another thread —
    /// background results arrive because the next frame drains them, not because this loop polls
    /// them.
    /// </summary>
    private WaitOutcome Wait(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // The clock starts before the first look: asking the latest completed frame is real
        // work, and a window spent by it is a miss that drew nothing — never a zero-frame success.
        var clock = Stopwatch.StartNew();
        return Wait(condition, timeout, cancellationToken, () => clock.Elapsed, Thread.Sleep);
    }

    /// <summary>
    /// The loop with its clock and its pacing handed to it. The public waits read the wall clock
    /// and sleep for real; a test hands the same loop a clock and a pace it controls, so the
    /// instant a pace lands on the deadline — cancellation inside it included — is staged rather
    /// than raced. Nothing else differs: the token is asked before every frame and before an
    /// expired window is reported, and the deadline is asked before every frame, the loop's
    /// first included.
    /// </summary>
    internal WaitOutcome Wait(
        Func<bool> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Func<TimeSpan> elapsed,
        Action<TimeSpan> pace)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        // The token first: a wait that is already abandoned must not draw even one frame.
        cancellationToken.ThrowIfCancellationRequested();

        // A frame already completed may answer without another being drawn; the window still
        // governs, so a look that spent it reports a miss, not a zero-frame success.
        if (condition()) return new WaitOutcome(elapsed() < timeout, 0, elapsed());

        int frames = 0;
        while (true)
        {
            // Both bounds are asked before every frame, this loop's first included: an
            // abandoned wait draws nothing, and neither does one whose window passed while the
            // last verdict was believed or a pace was being taken. The token is asked first,
            // so a cancellation that arrived during the final pace outranks a clock that
            // expired in the same instant.
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan spent = elapsed();
            if (spent >= timeout) return new WaitOutcome(false, frames, spent);

            Frame();
            frames++;

            // The token is asked before the frame's verdict is believed: a callback that
            // abandoned the wait and marked ready in the same breath has abandoned it.
            cancellationToken.ThrowIfCancellationRequested();

            bool satisfied = condition();
            spent = elapsed();

            // A synchronous draw cannot be interrupted, but an answer that turns true only
            // after the window has passed is a miss, not a success reported after expiry.
            if (satisfied) return new WaitOutcome(spent < timeout, frames, spent);

            // Pace toward the next frame without pacing past the deadline; the loop head then
            // asks the token before it reports the spent window, and draws nothing.
            TimeSpan remaining = timeout - spent;
            if (remaining > TimeSpan.Zero) pace(remaining < WaitPace ? remaining : WaitPace);
        }
    }

    /// <summary>What a bounded wait cost: whether the frame arrived, and how much it took.</summary>
    public readonly struct WaitOutcome
    {
        internal WaitOutcome(bool satisfied, int frames, TimeSpan elapsed)
        {
            Satisfied = satisfied;
            Frames = frames;
            Elapsed = elapsed;
        }

        /// <summary>Whether a frame answering the wait appeared within the timeout.</summary>
        public bool Satisfied { get; }

        /// <summary>How many real frames the app drew while waiting; 0 when the completed frame
        /// answered, or when the window was through before the wait's first frame.</summary>
        public int Frames { get; }

        /// <summary>The wall clock spent drawing them.</summary>
        public TimeSpan Elapsed { get; }

        /// <inheritdoc />
        public override string ToString() => Satisfied
            ? $"satisfied after {Frames} frames in {Elapsed.TotalSeconds:0.###}s"
            : $"not satisfied after {Frames} frames in {Elapsed.TotalSeconds:0.###}s";
    }

    /// <summary>The component being drawn, if this app draws one.</summary>
    public Component? Root => _root;

    private static readonly DrawFn NoDraw = _ => { };

    private readonly DrawFn _draw;

    /// <summary>Starts an app that draws <paramref name="draw"/> for every frame.</summary>
    /// <param name="draw">Drawn once per frame.</param>
    /// <param name="width">Logical width in points.</param>
    /// <param name="height">Logical height in points.</param>
    /// <param name="scale">Device pixels per point.</param>
    /// <param name="theme">The theme the window would have used.</param>
    /// <param name="accent">The accent the window would have used.</param>
    public static HeadlessApp Start(
        DrawFn draw,
        int width = 1024,
        int height = 768,
        float scale = 1f,
        Theme theme = Theme.Light,
        Accent accent = Accent.Neutral)
    {
        ArgumentNullException.ThrowIfNull(draw);
        var app = new HeadlessApp(CuiSurface.Create(width, height, scale, theme: theme, accent: accent), root: null, draw);
        app.Frame();
        return app;
    }

    /// <summary>Starts an app around one component, initializing it the way the host does.</summary>
    /// <param name="root">The component to draw.</param>
    /// <param name="width">Logical width in points.</param>
    /// <param name="height">Logical height in points.</param>
    /// <param name="scale">Device pixels per point.</param>
    /// <param name="theme">The theme the window would have used.</param>
    /// <param name="accent">The accent the window would have used.</param>
    public static HeadlessApp Start(
        Component root,
        int width = 1024,
        int height = 768,
        float scale = 1f,
        Theme theme = Theme.Light,
        Accent accent = Accent.Neutral)
    {
        ArgumentNullException.ThrowIfNull(root);
        var app = new HeadlessApp(CuiSurface.Create(width, height, scale, theme: theme, accent: accent), root);
        // The component initializes on its first draw, exactly as ui.Component() would do.
        app.Frame();
        return app;
    }

    /// <summary>Draws one frame and returns this app, so calls chain.</summary>
    public HeadlessApp Frame(int count = 1) => Frame(count, 1f / 60f);

    /// <summary>
    /// Moves the clock forward by <paramref name="time"/> over <paramref name="frames"/> frames.
    /// Frames are free but time is not: an animation is only as far along as the clock says, so a
    /// snapshot taken without advancing is a snapshot of an animation that never started.
    /// </summary>
    public HeadlessApp Advance(TimeSpan time, int frames = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frames);
        return Frame(frames, (float)(time.TotalSeconds / frames));
    }

    /// <summary>
    /// Draws frames until nothing wants another one, moving the clock by <paramref name="step"/>
    /// each time. This is the replacement for a <c>sleep</c> loop: an animation that takes half a
    /// second settles in a few milliseconds, and the count says how long it took.
    /// </summary>
    /// <param name="maxFrames">The most frames to draw, so a widget that never settles cannot hang a test.</param>
    /// <param name="step">How much time one frame represents.</param>
    /// <returns>How many frames were needed; <paramref name="maxFrames"/> means it did not settle.</returns>
    public int Settle(int maxFrames = 240, TimeSpan? step = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrames);
        float dt = (float)((step ?? TimeSpan.FromSeconds(1.0 / 60)).TotalSeconds);

        int drawn = 0;
        while (drawn < maxFrames)
        {
            Frame(1, dt);
            drawn++;
            if (!Animating) break;
        }

        return drawn;
    }

    /// <summary>
    /// True when something in the last frame asked for another one: an animation in flight, a
    /// transition, work that finished on a pool thread. <see cref="Settle"/> is the loop around it.
    /// </summary>
    public bool Animating => !_disposed && Native.NeedsRedraw(_surface.Handle) != 0;

    /// <summary>
    /// Takes the pointer off the app and draws the frame that reacts, so a screenshot is of the UI
    /// and not of the tooltip the last click left behind. <see cref="SavePng"/> does this itself.
    /// </summary>
    public HeadlessApp ParkPointer()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // The leave has to be drawn before it can be seen, and the frame that shows it is the
        // one that starts any fade: settling finishes that rather than waiting on the leave.
        _surface.MouseLeave();
        Frame();
        Settle();
        return this;
    }

    /// <summary>Draws <paramref name="count"/> frames of <paramref name="deltaSeconds"/> each.</summary>
    private HeadlessApp Frame(int count, float deltaSeconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        for (int i = 0; i < count; i++)
        {
            _faults.Clear();
            Component.BeginFrame();
            _surface.BeginFrame(deltaSeconds);
            try
            {
                // Render, not Draw: the component's own error boundary reports a throw into
                // the surface, so a failing page shows up in Faults instead of crashing.
                if (_root is not null) _root.Render(_surface.Ui);
                else _draw(_surface.Ui);
            }
            finally
            {
                _surface.EndFrame();
            }

            // A component that wants the next frame says so here too, so Animating means the same
            // thing in a test as in a window. PendingWork is deliberately left out: it is
            // process-wide, and one leaked operation would make every later test look animated.
            if (Component.AnyWantFrames) Native.RequestRedraw(_surface.Handle);

            foreach (Exception fault in _surface.Faults) _faults.Add(fault.Message);
        }

        return this;
    }

    /// <summary>Every control the last frame drew, in drawing order.</summary>
    public IReadOnlyList<SemanticWidget> Widgets => _surface.Widgets;

    /// <summary>
    /// The last frame's controls nested the way the app drew them: a button inside
    /// <c>ui.Card(new() { Id = "editor" })</c> is a child of <c>Card "editor"</c>, even when
    /// unnamed containers sit between them.
    /// </summary>
    /// <remarks>
    /// This is the shape to print when a search fails, and the shape an agent reads to learn
    /// what a frame offers: <c>app.Print()</c>.
    /// </remarks>
    public SemanticNode Tree => SemanticNode.Build(_surface.SemanticFrame);

    /// <summary>
    /// Finds a control by a query: a label (<c>Save</c>), a kind and a label
    /// (<c>Button "Save"</c>), or a path (<c>Card "editor" &gt; Button "Save"</c>).
    /// </summary>
    /// <remarks>
    /// An exact label beats a partial one, and a control beats a heading that happens to repeat
    /// its text: <c>click Save</c> lands on the button, not on the label above it. When a frame
    /// draws two controls that answer equally well, name the kind or the path.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Nothing in the last frame matched.</exception>
    public WidgetTarget Find(string query)
    {
        ArgumentException.ThrowIfNullOrEmpty(query);
        return TryFind(query, out WidgetTarget widget)
            ? widget
            : throw new InvalidOperationException($"No widget matching \"{query}\". The frame drew:\n{Print()}");
    }

    /// <summary>Finds a control of a kind and a label, e.g. <c>Find("Button", "Save")</c>.</summary>
    public WidgetTarget Find(string kind, string text) => Find(WidgetQuery.Of(kind, text));

    private WidgetTarget Find(WidgetQuery query) =>
        Tree.TryFind(query, out SemanticNode node)
            ? new WidgetTarget(this, node)
            : throw new InvalidOperationException($"No widget matching \"{query}\". The frame drew:\n{Print()}");

    /// <summary>True when the last frame drew something that answers the query.</summary>
    public bool TryFind(string query, out WidgetTarget widget)
    {
        if (Tree.TryFind(query, out SemanticNode node))
        {
            widget = new WidgetTarget(this, node);
            return true;
        }

        widget = default;
        return false;
    }

    /// <summary>Finds a button by its label.</summary>
    public WidgetTarget FindButton(string label) => Find("Button", label);

    /// <summary>Finds a text field by its id.</summary>
    public WidgetTarget FindInput(string id) => Find("Input", id);

    /// <summary>
    /// Finds a widget by the exact text the app drew it with — its id, for every widget that has
    /// one — and never by a partial or fuzzy match. This is the query to write when a label can be
    /// translated: <c>FindById("draft")</c> keeps working in another language, while
    /// <c>Find("draft")</c> was only ever guessing.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing in the last frame carries that text.</exception>
    public WidgetTarget FindById(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        foreach (SemanticNode node in Tree.SelfAndDescendants())
        {
            if (string.Equals(node.Text, id, StringComparison.Ordinal)) return new WidgetTarget(this, node);
        }

        throw new InvalidOperationException($"No widget with id \"{id}\". The frame drew:\n{Print()}");
    }

    /// <summary>
    /// The <paramref name="index"/>th item of the multi-item widget drawn with
    /// <paramref name="id"/>: a tab, a segment, a row. The label of an item is translatable, its
    /// position is not, so this is how a test names one without hard-coding the words.
    /// </summary>
    /// <param name="id">The id the widget was drawn with, e.g. <c>pages</c>.</param>
    /// <param name="index">Which item, counting from zero.</param>
    public WidgetTarget FindItem(string id, int index)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        IReadOnlyList<SemanticWidget> frame = _surface.SemanticFrame;
        int start = -1;
        for (int i = 0; i < frame.Count; i++)
        {
            if (frame[i].IsContainer && string.Equals(frame[i].Text, id, StringComparison.Ordinal)) { start = i; break; }
        }

        if (start < 0) throw new InvalidOperationException($"No widget with id \"{id}\". The frame drew:\n{Print()}");

        int depth = frame[start].Depth, seen = 0;
        for (int i = start + 1; i < frame.Count && frame[i].Depth > depth; i++)
        {
            if (frame[i].Depth != depth + 1 || frame[i].IsContainer) continue;
            if (seen++ != index) continue;
            if (Tree.FindById(frame[i].Id) is { } node) return new WidgetTarget(this, node);
            break;
        }

        throw new InvalidOperationException($"\"{id}\" has no item {index}. The frame drew:\n{Print()}");
    }

    /// <summary>Moves the pointer and draws the frame that reacts to the move (hover, tooltip).</summary>
    public HeadlessApp MoveTo(float x, float y)
    {
        _surface.MouseMove(x, y);
        return Frame();
    }

    /// <summary>
    /// Moves the pointer to (<paramref name="x"/>, <paramref name="y"/>) and scrolls there:
    /// the wheel goes to whichever scroll area holds that point. The distance is in wheel
    /// notches and a positive <paramref name="dy"/> scrolls up, as a real wheel does; a negative
    /// one reaches for what is below the fold. Draws the frame that shows the new offset — a
    /// spring-animated scroll needs a few <see cref="Frame(int)"/> calls to settle where it lands.
    /// </summary>
    public HeadlessApp ScrollAt(float x, float y, float dy, float dx = 0)
    {
        _surface.MouseMove(x, y);
        _surface.Scroll(dy, dx);
        return Frame();
    }

    /// <summary>Presses the left button and draws the frame that sees the press.</summary>
    public HeadlessApp Press()
    {
        _surface.MouseDown();
        return Frame();
    }

    /// <summary>Releases the left button and draws the frame that reports the click.</summary>
    public HeadlessApp Release()
    {
        _surface.MouseUp();
        return Frame();
    }

    /// <summary>Types text into whatever has focus, and draws the frame that accepts it.</summary>
    public HeadlessApp Type(string text)
    {
        _surface.Type(text);
        return Frame();
    }

    /// <summary>Presses and releases a key, drawing a frame for each.</summary>
    public HeadlessApp Key(Key key, Mods mods = 0)
    {
        _surface.KeyDown(key, mods);
        Frame();
        _surface.KeyUp(key, mods);
        return Frame();
    }

    /// <summary>Reports the window gaining focus, which a text field needs before it takes keys.</summary>
    public HeadlessApp Focus(bool focused = true)
    {
        _surface.Focus(focused);
        return Frame();
    }

    /// <summary>The last frame's pixels, RGBA.</summary>
    public ReadOnlySpan<byte> RawPixels
    {
        get
        {
            _surface.Render(_pixels, PixelFormat.Rgba);
            return _pixels;
        }
    }

    /// <summary>True when the last frame painted anything at all.</summary>
    public bool PaintedAnything()
    {
        foreach (byte b in RawPixels)
        {
            if (b != 0) return true;
        }

        return false;
    }

    /// <summary>
    /// Writes the last frame as a PNG and returns the path, for a human or an agent to look at.
    /// The pointer is taken off the UI first, so a hover tooltip left by the last click is not in
    /// the picture; pass <paramref name="parked"/> = false to photograph the app as it stands.
    /// </summary>
    public string SavePng(string? path = null, bool parked = true)
    {
        path ??= Path.Combine(Path.GetTempPath(), $"cui-{Guid.NewGuid():N}.png");
        if (parked) ParkPointer();
        ReadOnlySpan<byte> pixels = RawPixels;
        int w = _surface.PixelWidth, h = _surface.PixelHeight;

        // CUI's writer wants a NUL-terminated path and an RGBA buffer, strided by bytes.
        byte[] pathBytes = System.Text.Encoding.UTF8.GetBytes(path + "\0");
        unsafe
        {
            fixed (byte* p = pixels)
            fixed (byte* name = pathBytes)
            {
                if (Native.WritePng((nint)name, (nint)p, w, h, w * 4) == 0)
                    throw new IOException($"Could not write {path}.");
            }
        }

        return path;
    }

    /// <summary>The last frame's tree as text, which is also what <see cref="ToString"/> gives.</summary>
    public string Print(bool rects = false) => Tree.Print(rects);

    /// <summary>
    /// The <see cref="LayoutFormatter"/> view of the last frame: verified rectangles, observed
    /// bounds labelled as the lower bounds they are, and unknown extents named as unknowns.
    /// </summary>
    public string PrintLayout() => LayoutFormatter.Print(Tree);

    /// <inheritdoc />
    public override string ToString() => Print();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _surface.RecordWidgets = false;
        _surface.Dispose();
        _root?.Dispose();
        GC.SuppressFinalize(this);
    }
}
