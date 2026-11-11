using System.Runtime.InteropServices;
using CuiSharp.Hosting;
using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>Pixel layout for <see cref="CuiSurface.Render"/>.</summary>
public enum PixelFormat
{
    /// <summary>Bytes R, G, B, A.</summary>
    Rgba = 0,

    /// <summary>Bytes B, G, R, A (Win32 DIBs, and what most framebuffers want).</summary>
    Bgra = 1,
}

/// <summary>Which rasterizer a surface uses to turn draw data into pixels.</summary>
public enum RendererKind
{
    /// <summary>Software rasterizer into a pixel buffer (CPU, works everywhere).</summary>
    Software = 0,

    /// <summary>GPU renderer (WebGL2 in the browser) consuming the surface's draw data.</summary>
    Gpu = 1,
}

/// <summary>
/// A CUI context with no window, rendered by the built-in software rasterizer or handed to a
/// GPU renderer as draw data (<see cref="CreateGpu"/>).
/// <para>
/// This is the piece that makes UI code testable: the same <see cref="CuiApp"/>
/// draw callback can run here, produce real pixels, and be asserted on in a unit
/// test or a benchmark — no display, no GPU, no message loop.
/// </para>
/// <para>
/// Frames are driven explicitly, which also makes it a stable way to measure
/// build, layout and rasterization cost separately.
/// </para>
/// </summary>
/// <example>
/// <code>
/// using var surface = CuiSurface.Create(800, 600);
/// surface.BeginFrame();
/// using (surface.Ui.Column(new Box { Pad = 16 })) surface.Ui.Heading("Hi", 2);
/// surface.EndFrame();
/// Assert.StartsWith("P6", surface.RenderToPngHeader());  // or inspect pixels
/// </code>
/// </example>
public sealed unsafe partial class CuiSurface : IDisposable
{
    private nint _sw;
    private nint _ctx;
    private int _width;
    private int _height;
    private float _scale;
    private readonly UiFrameState _frame = new();
    private readonly List<Exception> _faults = [];
    private bool _incremental;
    private bool _recordWidgets;
    private long _frameAllocated;
    private readonly FrameRecorder _recorder = new();

    [ThreadStatic] private static CuiSurface? _current;
    private CuiSurface? _previous;
    private FrameRecorder? _recorderBelow;

    private CuiSurface(nint sw, nint ctx, int width, int height, float scale, RendererKind kind)
    {
        _sw = sw;
        _ctx = ctx;
        _width = width;
        _height = height;
        _scale = scale;
        Kind = kind;
    }

    /// <summary>The native context, for the input surface this type also exposes.</summary>
    internal nint Handle => _ctx;

    /// <summary>A fresh stack-only drawing context for the current frame.</summary>
    public Ui Ui
    {
        get
        {
            ObjectDisposedException.ThrowIf(_ctx == 0, this);
            return new Ui(_ctx, _frame);
        }
    }

    /// <summary>Logical width in points.</summary>
    public int Width => _width;

    /// <summary>Logical height in points.</summary>
    public int Height => _height;

    /// <summary>Device pixels per point.</summary>
    public float Scale => _scale;

    /// <summary>Which rasterizer this surface uses.</summary>
    public RendererKind Kind { get; }

    /// <summary>Framebuffer width in pixels.</summary>
    /// <remarks>
    /// Rounded, not truncated, because that is how CUI computes <c>cui_draw_data.width_px</c>
    /// (<c>cui_core.c</c>: <c>vw * scale + 0.5f</c>). A renderer that sizes its target by
    /// truncating disagrees by a pixel on fractional display scales such as 1.5, and a scaler
    /// comparing the two would refuse every frame.
    /// </remarks>
    public int PixelWidth => (int)(_width * _scale + 0.5f);

    /// <summary>Framebuffer height in pixels.</summary>
    /// <remarks>Rounded like <see cref="PixelWidth"/>.</remarks>
    public int PixelHeight => (int)(_height * _scale + 0.5f);

    /// <summary>
    /// What the last frame this surface drew cost: the same numbers as <see cref="FrameStats"/>, but
    /// owned by this surface, so a test that draws more than one — or a run with tests in parallel —
    /// reads the frame it drew rather than whichever frame finished last.
    /// </summary>
    public FrameSample LastFrame { get; private set; }

    /// <summary>
    /// Whether the latest completed frame declared its app data complete with
    /// <see cref="Ui.MarkReady"/>.
    /// </summary>
    /// <remarks>
    /// Readiness belongs to the frame, not to the app: a frame that goes back to loading is not
    /// ready however ready its predecessor was, and this says nothing about the headless banner
    /// (which only proves the channel is open) or about an animation settling. While a frame is
    /// drawing — including in its deferred paint phase — this is still the previous completed
    /// frame's verdict; the frame being drawn publishes its own only once its paint is done.
    /// </remarks>
    public bool LastFrameReady => _frame.LastFrameReady;

    /// <summary>
    /// Exceptions a component threw while this surface drew. A component's error
    /// boundary keeps the frame going, which would otherwise make a broken page look
    /// like a passing render test; assert <c>Assert.Empty(surface.Faults)</c>.
    /// </summary>
    public IReadOnlyList<Exception> Faults => _faults;

    /// <summary>Records a failure for the assertions above. Called by the error boundary.</summary>
    internal static void ReportFault(Exception error) => _current?._faults.Add(error);

    /// <summary>Creates a windowless context backed by the software rasterizer.</summary>
    /// <remarks>
    /// <paramref name="theme"/> and <paramref name="accent"/> are the same two a window takes, so
    /// a frame drawn here looks like the frame the app draws in a window.
    /// </remarks>
    public static CuiSurface Create(
        int width,
        int height,
        float scale = 1f,
        int threads = 1,
        Theme theme = Theme.Light,
        Accent accent = Accent.Neutral)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        nint sw = Native.SwCreate();
        if (sw == 0) throw new InvalidOperationException("cui_sw_create failed.");

        Native.SwSetThreads(sw, threads);

        var desc = new NativeCtxDesc
        {
            Renderer = Native.SwIface(sw),
            Theme = theme,
            Accent = accent,
        };

        nint ctx = Native.Create(in desc);
        if (ctx == 0)
        {
            Native.SwDestroy(sw);
            throw new InvalidOperationException("cui_create failed.");
        }

        Native.SetViewport(ctx, width, height, scale);
        return new CuiSurface(sw, ctx, width, height, scale, RendererKind.Software);
    }

    /// <summary>Creates a windowless context for a GPU renderer (WebGL2 in the browser).</summary>
    /// <remarks>
    /// No rasterizer runs inside the process: the surface produces <c>cui_draw_data</c> and the
    /// host draws it — a JS WebGL2 renderer reads <see cref="GetDrawData"/> straight out of wasm
    /// memory. <paramref name="textures"/> must supply the texture callbacks (create, update,
    /// destroy), because CUI uploads its font atlas through them on the first text it draws.
    /// The desktop GL renderer is a different path: use a window, not this method.
    /// </remarks>
    public static CuiSurface CreateGpu(
        int width,
        int height,
        in NativeRendererIface textures,
        float scale = 1f,
        Theme theme = Theme.Light,
        Accent accent = Accent.Neutral)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (textures.TextureCreate == 0 || textures.TextureUpdate == 0 || textures.TextureDestroy == 0)
        {
            throw new ArgumentException(
                "a GPU surface needs texture create, update and destroy callbacks", nameof(textures));
        }

        var desc = new NativeCtxDesc
        {
            Renderer = textures,
            Theme = theme,
            Accent = accent,
        };

        nint ctx = Native.Create(in desc);
        if (ctx == 0) throw new InvalidOperationException("cui_create failed.");

        Native.SetViewport(ctx, width, height, scale);
        return new CuiSurface(0, ctx, width, height, scale, RendererKind.Gpu);
    }

    /// <summary>Starts a frame. Call <see cref="EndFrame"/> when the UI is built.</summary>
    /// <remarks>
    /// <see cref="EndFrame"/> runs layout and paint, so by the time the next
    /// frame starts the previous one's deferred canvas callbacks and arena
    /// strings are dead. Resetting both here is what keeps a headless loop from
    /// growing: without it the painter list and the arena would grow per frame.
    /// </remarks>
    public void BeginFrame(float deltaSeconds = 1f / 60f)
    {
        ObjectDisposedException.ThrowIf(_ctx == 0, this);

        // The frame boundary is the only place work from a pool thread may be
        // applied, so a frame never sees a half-applied result.
        Completions.Drain();

        // A component's error boundary reports into the surface that is drawing it,
        // so a test can assert that a page rendered without throwing.
        _previous = _current;
        _current = this;

        _frame.Reset();
        Utf8.BeginFrame();

        // A name table published or edited since the last frame is handed to CUI before this
        // frame draws, and blocks the frame just presented could still read are freed here.
        _frame.SyncLocalization(_ctx);

        _recorderBelow = WidgetRecorder.BeginFrame(_recorder);

        // A surface is how a test draws a frame, so it reports the same numbers a window does.
        _frameAllocated = FrameStats.Begin();

        Native.SetViewport(_ctx, _width, _height, _scale);
        Native.BeginFrame(_ctx, deltaSeconds);
    }

    /// <summary>Ends the frame, finalising layout and the draw list.</summary>
    public void EndFrame()
    {
        ObjectDisposedException.ThrowIf(_ctx == 0, this);
        try
        {
            // Closed before CUI lays out and paints, and before the recorder picks the frame's
            // text apart: these numbers are the app's own draw, which is what a budget is about.
            LastFrame = FrameStats.End(_frameAllocated);
            using (_frame.EnterPaint()) Native.EndFrame(_ctx);
        }
        finally
        {
            WidgetRecorder.EndFrame(_recorder, _recorderBelow, _ctx);
            _recorderBelow = null;
            _current = _previous;
            _previous = null;

            // Readiness is published here, last, rather than at MarkReady or before CUI paints:
            // the deferred canvas painters run inside EndFrame and may mark the frame they paint,
            // and until this line a reader still sees the previous completed frame's verdict.
            _frame.LastFrameReady = _frame.FrameReady;
        }
    }

    /// <summary>Resizes the logical viewport.</summary>
    public void Resize(int width, int height, float? scale = null)
    {
        _width = width;
        _height = height;
        if (scale.HasValue) _scale = scale.Value;
    }

    /// <summary>
    /// Rasterizes the last completed frame into <paramref name="pixels"/>.
    /// The buffer must hold at least <see cref="PixelWidth"/> * <see cref="PixelHeight"/> * 4 bytes.
    /// </summary>
    public void Render(byte[] pixels, PixelFormat format = PixelFormat.Bgra)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ObjectDisposedException.ThrowIf(_ctx == 0, this);
        if (_sw == 0) throw new InvalidOperationException("a GPU surface has no pixel buffer; draw the draw data instead.");

        int w = PixelWidth, h = PixelHeight;
        if (pixels.Length < w * h * 4)
        {
            throw new ArgumentException($"buffer too small: need {w * h * 4} bytes", nameof(pixels));
        }

        var drawData = Native.GetDrawData(_ctx);
        fixed (byte* p = pixels)
        {
            Native.SwRender(_sw, in *(NativeDrawData*)drawData, (nint)p, w, h, w * 4, (SwFormat)format);
        }
    }

    /// <summary>
    /// Renders only the part of the frame that changed since the last one.
    /// </summary>
    /// <remarks>
    /// Off by default: a test that compares whole frames wants all of them painted. When
    /// it is on, the software renderer keeps the previous buffer and repaints only the
    /// damaged rectangle, which is what a window does with a damaged region.
    /// </remarks>
    public bool Incremental
    {
        get => _incremental;
        set
        {
            if (_ctx == 0) return;
            if (_sw == 0) throw new InvalidOperationException("incremental rendering is a software-renderer feature.");
            if (_incremental == value) return;
            _incremental = value;
            Native.SwSetIncremental(_sw, (byte)(value ? 1 : 0));
        }
    }

    /// <summary>The rectangle the last render touched, or null when nothing was drawn.</summary>
    public RectF? LastDamage
    {
        get
        {
            if (_ctx == 0 || _sw == 0) return null;
            int x = 0, y = 0, w = 0, h = 0;
            return Native.SwLastDamage(_sw, ref x, ref y, ref w, ref h) == 0 ? null : new RectF(x, y, w, h);
        }
    }

    /// <summary>
    /// Records every widget this surface draws, so a test can find one by its label.
    /// </summary>
    /// <remarks>
    /// Off by default. Turning it on costs one branch per widget while it is off and one record
    /// per widget while it is on. Recording belongs to this surface: another one on the same
    /// thread keeps its own frame.
    /// </remarks>
    public bool RecordWidgets
    {
        get => _recordWidgets;
        set
        {
            _recordWidgets = value;
            WidgetRecorder.Attach(_recorder, value);
        }
    }

    /// <summary>The controls the last completed frame drew, in drawing order.</summary>
    /// <remarks>A container is not a control: it appears in <see cref="SemanticFrame"/> instead.</remarks>
    public IReadOnlyList<SemanticWidget> Widgets => _recorder.Controls;

    /// <summary>
    /// Everything the last frame recorded, containers included and in drawing order: each
    /// entry's <see cref="SemanticWidget.Depth"/> is the nesting a tree can be rebuilt from.
    /// </summary>
    public IReadOnlyList<SemanticWidget> SemanticFrame => _recorder.Entries;

    /// <summary>Convenience: build one frame with <paramref name="draw"/>, end it and rasterize.</summary>
    public void Draw(DrawFn draw, byte[] pixels, PixelFormat format = PixelFormat.Bgra)
    {
        ArgumentNullException.ThrowIfNull(draw);

        BeginFrame();
        draw(Ui);
        EndFrame();
        Render(pixels, format);
    }

    /// <summary>How many draw commands the last frame produced. Useful in assertions.</summary>
    public int CommandCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(_ctx == 0, this);
            var dd = (NativeDrawData*)Native.GetDrawData(_ctx);
            return dd == null ? 0 : dd->CommandCount;
        }
    }

    /// <summary>The draw data the last frame produced, for a GPU renderer.</summary>
    /// <remarks>
    /// The pointer and the memory behind it are valid until the next <see cref="BeginFrame"/>.
    /// A GPU renderer reads vertices, indices, commands and the font atlas id straight out of
    /// this structure — the same <c>cui_draw_data</c> the built-in renderers consume.
    /// </remarks>
    public nint GetDrawData()
    {
        // cui_get_draw_data returns &ui->draw_data: on a destroyed context that is not null,
        // it is the offset of the field inside a freed struct.
        ObjectDisposedException.ThrowIf(_ctx == 0, this);
        return Native.GetDrawData(_ctx);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Recording is per-thread state that outlives this surface, so a surface that goes
        // away must not leave a recorder on for whatever runs next on this thread.
        if (_recordWidgets)
        {
            _recordWidgets = false;
            WidgetRecorder.Attach(_recorder, false);
        }

        if (_ctx != 0)
        {
            Native.Destroy(_ctx);
            _ctx = 0;
            _frame.Reset();
            _frame.Release();
        }

        if (_sw != 0)
        {
            Native.SwDestroy(_sw);
            _sw = 0;
        }
    }
}
