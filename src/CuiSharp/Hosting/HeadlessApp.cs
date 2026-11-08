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
    public HeadlessApp Frame(int count = 1)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        for (int i = 0; i < count; i++)
        {
            _faults.Clear();
            _surface.BeginFrame();
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

    /// <summary>Moves the pointer and draws the frame that reacts to the move (hover, tooltip).</summary>
    public HeadlessApp MoveTo(float x, float y)
    {
        _surface.MouseMove(x, y);
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

    /// <summary>Writes the last frame as a PNG and returns the path, for a human or an agent to look at.</summary>
    public string SavePng(string? path = null)
    {
        path ??= Path.Combine(Path.GetTempPath(), $"cui-{Guid.NewGuid():N}.png");
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
