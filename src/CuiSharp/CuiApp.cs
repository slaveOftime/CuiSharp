using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>Everything <see cref="CuiApp"/> needs to create a window. All optional.</summary>
public sealed record CuiAppOptions
{
    /// <summary>Window title. Default <c>"CUI"</c>.</summary>
    public string Title { get; set; } = "CUI";

    /// <summary>Logical width in points. Default 1080.</summary>
    public int Width { get; set; } = 1080;

    /// <summary>Logical height in points. Default 720.</summary>
    public int Height { get; set; } = 720;

    /// <summary>Smallest the user can resize to. Default 360 x 280.</summary>
    public int MinWidth { get; set; } = 360;

    /// <summary>Smallest the user can resize to. Default 360 x 280.</summary>
    public int MinHeight { get; set; } = 280;

    /// <summary>Not resizable.</summary>
    public bool FixedSize { get; set; }

    /// <summary>Draw every frame even when idle (games, video). Off by default: an idle app uses no CPU.</summary>
    public bool Continuous { get; set; }

    /// <summary>Skip vsync.</summary>
    public bool NoVsync { get; set; }

    /// <summary>Create the window hidden, for tests and screenshot runs.</summary>
    public bool Hidden { get; set; }

    /// <summary>OpenGL with a software fallback, or force one. Default <see cref="Renderer.Auto"/>.</summary>
    public Renderer Renderer { get; set; }

    /// <summary>Initial theme.</summary>
    public Theme Theme { get; set; } = Theme.Light;

    /// <summary>Initial accent color.</summary>
    public Accent Accent { get; set; } = Accent.Neutral;

    /// <summary>Use the OS light/dark setting where available.</summary>
    public bool FollowSystemTheme { get; set; }
}

/// <summary>
/// A native window, its message loop and a renderer, owned by CUI.
/// <para>
/// <see cref="Run(CuiAppOptions, DrawFn)"/> drives CUI from a plain managed loop rather than from a
/// native callback. That costs one extra P/Invoke per frame and buys two
/// things: <c>dotnet watch</c> hot reload can patch the UI code between frames,
/// and there is no function pointer for the AOT compiler to pin.
/// </para>
/// </summary>
public sealed unsafe class CuiApp : IDisposable
{
    private nint _handle;
    private Theme _theme;
    private Accent _accent;
    private bool _themeDirty;
    private readonly nint _ctx;
    private readonly UiFrameState _frame = new();

    private CuiApp(nint handle, CuiAppOptions options)
    {
        _handle = handle;
        _ctx = Native.AppUi(handle);
        _theme = options.Theme;
        _accent = options.Accent;
        _themeDirty = false;
    }

    /// <summary>A fresh stack-only drawing context for this window's current frame.</summary>
    public Ui Ui
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            return new Ui(_ctx, _frame);
        }
    }

    /// <summary>The OS window: HWND on Windows, NSWindow* on macOS, X11 Window on Linux.</summary>
    public nint NativeWindow => Native.AppNativeWindow(_handle);

    /// <summary>True when the OpenGL context could not be created and CUI fell back to its software rasterizer.</summary>
    public bool IsSoftwareRenderer => Native.AppIsSoftware(_handle) != 0;

    /// <summary>The active backend: "win32", "cocoa", "x11", "sdl2" or "glfw".</summary>
    public string Backend
    {
        get
        {
            var p = Native.AppBackend(_handle);
            return System.Runtime.InteropServices.Marshal.PtrToStringUTF8(p) ?? "unknown";
        }
    }

    /// <summary>Applies a theme now, or on the next frame if the window is not up yet.</summary>
    public void SetTheme(Theme theme) { _theme = theme; _themeDirty = true; }

    /// <summary>Applies an accent now, or on the next frame if the window is not up yet.</summary>
    public void SetAccent(Accent accent) { _accent = accent; _themeDirty = true; }

    /// <summary>Creates the window. Returns null when the platform backend fails.</summary>
    public static CuiApp? Create(CuiAppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var desc = Describe(options);
        var handle = Native.AppCreate(in desc);
        return handle == 0 ? null : new CuiApp(handle, options);
    }

    /// <summary>
    /// Presents the previous frame, waits for input or an animation, then
    /// begins a new one. Returns false once the window is closed.
    /// </summary>
    public bool Frame()
    {
        if (_handle == 0) return false;

        if (_themeDirty)
        {
            Native.SetTheme(_ctx, _theme);
            Native.SetAccent(_ctx, _accent);
            _themeDirty = false;
        }

        // cui_app_frame presents the frame drawn last time, which runs the
        // deferred canvas paint callbacks. Only then is it safe to drop them.
        bool alive;
        using (_frame.EnterPaint()) alive = Native.AppFrame(_handle) != 0;
        _frame.Reset();

        // Everything the last frame copied for argument strings is dead now.
        Utf8.BeginFrame();

        return alive;
    }

    /// <summary>Asks the loop to stop after the current frame.</summary>
    public void Quit() { if (_handle != 0) Native.AppQuit(_handle); }

    /// <summary>True when this window is maximized.</summary>
    public bool IsMaximized => _handle != 0 && OperatingSystem.IsWindows() && Win32.IsZoomed(NativeWindow) != 0;

    /// <summary>Minimizes this window.</summary>
    public void Minimize()
    {
        if (_handle != 0 && OperatingSystem.IsWindows()) Win32.ShowWindow(NativeWindow, Win32.SwMinimize);
    }

    /// <summary>Maximizes this window, or restores it when it is already maximized.</summary>
    public void ToggleMaximize()
    {
        if (_handle == 0 || !OperatingSystem.IsWindows()) return;
        Win32.ShowWindow(NativeWindow, Win32.IsZoomed(NativeWindow) != 0 ? Win32.SwRestore : Win32.SwMaximize);
    }

    /// <summary>Closes this window, exactly as its own close button would.</summary>
    public void RequestClose()
    {
        if (_handle == 0) return;
        if (OperatingSystem.IsWindows()) Win32.RequestClose(NativeWindow);
        else Native.AppQuit(_handle);
    }

    /// <summary>Renders the current UI into an RGBA buffer. Handy for tests and screenshots.</summary>
    public bool Screenshot(byte[] rgba, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        fixed (byte* p = rgba) return Native.AppScreenshot(_handle, (nint)p, width, height) != 0;
    }

    /// <summary>
    /// Creates a window and calls <paramref name="draw"/> once per frame until
    /// the window closes. This is the whole host: no XAML, no dispatcher.
    /// </summary>
    /// <example>
    /// <code>
    /// CuiApp.Run(new CuiAppOptions { Title = "Hello", Width = 420, Height = 300 }, ui =&gt;
    /// {
    ///     using (ui.Column(new Box { Pad = 32, Gap = 16 }))
    ///     {
    ///         ui.Heading("Hello, CUI", 2);
    ///         if (ui.Button("Click me", new ButtonOpts { Variant = ButtonVariant.Primary })) clicks++;
    ///     }
    /// });
    /// </code>
    /// </example>
    public static int Run(CuiAppOptions options, DrawFn draw) => Run(options, draw, args: null);

    /// <summary>
    /// <see cref="Run(CuiAppOptions, DrawFn)"/>, told the arguments the app itself was started
    /// with — a host that parsed its own command line does not have to rely on the process's.
    /// </summary>
    internal static int Run(CuiAppOptions options, DrawFn draw, string[]? args)
    {
        ArgumentNullException.ThrowIfNull(draw);

        // Headless: `CUISHARP_HEADLESS=1` or `--headless` answers commands on the standard
        // streams instead of opening a window, so `dotnet cui` drives any app unmodified. This
        // is the only place that decides it, and the draw callback is the whole app — for a
        // hosted one it is the host's frame, so a tool sees the same lifecycle a window does.
        if (Features.Headless && Hosting.HeadlessLoop.Requested(args))
            return Hosting.HeadlessLoop.Run(draw, options);

        // CUI owns the loop here on purpose. `cui_app_run` registers a frame
        // callback, and that is the callback the OS-driven resize loop invokes:
        // while the user drags a window border, Windows runs a modal message loop
        // and our managed loop never gets a turn. Without a frame callback CUI can
        // only re-present the previous frame, so the layout stays stale until the
        // drag ends. The loop-style `Create`/`Frame` pair cannot do this: it is
        // the caller's loop that is blocked, so the plan's rule is that a real
        // window runs through Run.
        var state = new RunState(draw);
        GCHandle handle = GCHandle.Alloc(state);
        try
        {
            var desc = Describe(options);

            // Deferred canvas painters run inside CUI's paint phase, which happens
            // on this same thread but outside the callback, so the painting state
            // has to cover the whole loop.
            using (state.Frame.EnterPaint())
            {
                int code = Native.AppRun(in desc, (nint)(delegate* unmanaged[Cdecl]<nint, void*, void>)&RunThunk, (nint)handle);
                if (code != 0) throw new InvalidOperationException(
                    "cui_app_create returned null. On Windows this means cui.dll is missing next to the executable.");
                return code;
            }
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Managed state behind one <see cref="Run(CuiAppOptions, DrawFn)"/> call.</summary>
    private sealed class RunState(DrawFn draw)
    {
        internal readonly DrawFn Draw = draw;
        internal readonly UiFrameState Frame = new();
    }

    // The frame callback CUI calls for a normal frame and, during a window drag,
    // from the OS modal loop. It is the body of a frame, not the whole frame: CUI
    // began it already and will end and render it after we return.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RunThunk(nint ui, void* user)
    {
        var state = (RunState)GCHandle.FromIntPtr((nint)user).Target!;

        // The previous frame has been presented by now, so its painters and the
        // arena that holds this frame's argument strings can be reused.
        state.Frame.Reset();
        Utf8.BeginFrame();
        state.Draw(new Ui(ui, state.Frame));
    }

    private static NativeAppDesc Describe(CuiAppOptions options) => new()
    {
        Title = Utf8.Get(options.Title),
        Width = options.Width,
        Height = options.Height,
        MinWidth = options.MinWidth,
        MinHeight = options.MinHeight,
        FixedSize = (byte)(options.FixedSize ? 1 : 0),
        Continuous = (byte)(options.Continuous ? 1 : 0),
        NoVsync = (byte)(options.NoVsync ? 1 : 0),
        Hidden = (byte)(options.Hidden ? 1 : 0),
        Renderer = options.Renderer,
        Theme = options.Theme,
        Accent = options.Accent,
        FollowSystemTheme = (byte)(options.FollowSystemTheme ? 1 : 0),
    };

    /// <inheritdoc />
    public void Dispose()
    {
        if (_handle == 0) return;
        Native.AppDestroy(_handle);
        _handle = 0;
        _frame.Reset();
    }
}
