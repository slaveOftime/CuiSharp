using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CuiSharp.Hosting;
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

    /// <summary>
    /// What <see cref="CuiWindow.PickFile"/> uses. Null asks the OS through CUI; a test supplies one
    /// so a picker flow runs with no display.
    /// </summary>
    public IFileDialogProvider? FileDialog { get; set; }

    /// <summary>
    /// Called once the window exists and before the first frame is drawn — the first moment there
    /// is a handle to brand, log or query. <see cref="Hosting.Component.OnInitialized"/> is earlier
    /// than this on purpose: it runs before there is a window, so anything that needs one belongs
    /// here.
    /// </summary>
    public Action<CuiWindow>? WindowCreated { get; set; }

    /// <summary>
    /// Let a tool on the same machine drive this window while a human watches it
    /// (<see cref="Hosting.LiveSession"/>). Off by default and never automatic: an app is only
    /// driven when it says so here, or with <c>CUISHARP_LIVE=1</c> / <c>--live</c>. Windows-only
    /// for now; asking for it elsewhere fails rather than silently doing nothing.
    /// </summary>
    public bool LiveAttach { get; set; }
}

/// <summary>
/// A native window, its message loop and a renderer, owned by CUI.
/// <para>
/// <see cref="Run(CuiAppOptions, DrawFn)"/> does not run a loop of its own: it registers the draw
/// callback with CUI and lets the native loop call it. That is what keeps the frame alive while the
/// user drags a window edge — the OS runs a modal loop of its own there, so a managed
/// <c>while</c> loop would stop painting mid-drag and the layout would go stale. See
/// <c>docs/ARCHITECTURE.md</c>, "Frame model".
/// </para>
/// </summary>
public sealed unsafe class CuiApp : IDisposable
{
    private nint _handle;
    private readonly CuiWindow _window;
    private Theme _theme;
    private Accent _accent;
    private bool _themeDirty;
    private readonly nint _ctx;
    private readonly UiFrameState _frame = new();

    private CuiApp(nint handle, CuiAppOptions options)
    {
        _handle = handle;
        _window = new CuiWindow(handle);
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
    public nint NativeWindow => _window.NativeWindow;

    /// <summary>True when the OpenGL context could not be created and CUI fell back to its software rasterizer.</summary>
    public bool IsSoftwareRenderer => _window.IsSoftwareRenderer;

    /// <summary>The active backend: "win32", "cocoa", "x11", "sdl2" or "glfw".</summary>
    public string Backend => _window.Backend;

    /// <summary>The same window, for code that takes one: branding, logging, OS queries.</summary>
    public CuiWindow Window => _window;

    /// <summary>Applies a theme now, or on the next frame if the window is not up yet.</summary>
    public void SetTheme(Theme theme) { _theme = theme; _themeDirty = true; }

    /// <summary>Applies an accent now, or on the next frame if the window is not up yet.</summary>
    public void SetAccent(Accent accent) { _accent = accent; _themeDirty = true; }

    /// <summary>Creates the window. Returns null when the platform backend fails.</summary>
    public static CuiApp? Create(CuiAppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var desc = Describe(options);
        nint handle;
        try
        {
            handle = Native.AppCreate(in desc);
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            throw CuiSharpNativeException.From(error);
        }

        if (handle == 0) return null;

        var app = new CuiApp(handle, options);
        app.Window.FileDialog = options.FileDialog;
        LiveResize.Install(handle == 0 ? 0 : app.NativeWindow, options.NoVsync);
        WindowIcon.Install(app.NativeWindow);
        options.WindowCreated?.Invoke(app.Window);
        return app;
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

        // The frame that just presented is over: a changed name table reaches the next one, and a
        // block it may still have been reading is freed here.
        _frame.SyncLocalization(_ctx);

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
        var state = new RunState(options, draw);
        // Keep the opt-in branch inside the feature gate, rather than storing the gate in a
        // local: AOT's dependency scanner must discard the entire IPC construction path.
        if (Features.Live)
        {
            if (options.LiveAttach || Hosting.LiveSession.Requested(args))
            {
                if (!OperatingSystem.IsWindows())
                    throw new NotSupportedException("Live attach is Windows-only for now. Run without live attach on this platform.");
                // The message window belongs to the thread about to enter CUI's native loop.
                state.Live = new Hosting.LiveSession(options, state.Frame);
            }
        }

        GCHandle handle = GCHandle.Alloc(state);
        try
        {
            var desc = Describe(options);

            // Deferred canvas painters run inside CUI's paint phase, which happens
            // on this same thread but outside the callback, so the painting state
            // has to cover the whole loop.
            using (state.Frame.EnterPaint())
            {
                try
                {
                    int code = Native.AppRun(in desc, (nint)(delegate* unmanaged[Cdecl]<nint, void*, void>)&RunThunk, (nint)handle);
                    if (code != 0)
                    {
                        throw new InvalidOperationException(
                            "cui_app_create returned null: the platform window could not be created. " +
                            "On Windows this means cui.dll is missing next to the executable; on a " +
                            "Linux host it usually means there is no display (DISPLAY is unset).");
                    }

                    return code;
                }
                catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
                {
                    throw CuiSharpNativeException.From(error);
                }
            }
        }
        finally
        {
            if (Features.Live) state.Live?.Dispose();
            LiveResize.Uninstall(state.WindowHandle);
            state.Window?.Release();
            handle.Free();
        }
    }

    /// <summary>Managed state behind one <see cref="Run(CuiAppOptions, DrawFn)"/> call.</summary>
    private sealed class RunState(CuiAppOptions options, DrawFn draw)
    {
        internal readonly CuiAppOptions Options = options;
        internal readonly DrawFn Draw = draw;
        internal readonly UiFrameState Frame = new();
        internal nint WindowHandle;
        internal CuiWindow? Window;
        internal Hosting.LiveSession? Live;

        /// <summary>
        /// Publishes the window on the first frame. The app loop keeps its <c>cui_app*</c> private,
        /// so this is where it comes back: the window it made carries that pointer as its user data.
        /// </summary>
        internal void Hooking(nint ui)
        {
            if (Window is not null || !OperatingSystem.IsWindows()) return;
            nint app = Win32.AppOf(Win32.FindCuiWindow());
            if (app == 0)
            {
                if (Features.Live && Live is not null)
                    throw new InvalidOperationException("Live attach could not recover the native CUI window on the UI thread.");
                return;
            }

            WindowHandle = Native.AppNativeWindow(app);
            Window = new CuiWindow(app) { FileDialog = Options.FileDialog };
            LiveResize.Install(WindowHandle, Options.NoVsync);
            WindowIcon.Install(WindowHandle);
            Options.WindowCreated?.Invoke(Window);
            if (Features.Live) Live?.Bind(app, ui, WindowHandle);
        }
    }

    // The frame callback CUI calls for a normal frame and, during a window drag,
    // from the OS modal loop. It is the body of a frame, not the whole frame: CUI
    // began it already and will end and render it after we return.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RunThunk(nint ui, void* user)
    {
        var state = (RunState)GCHandle.FromIntPtr((nint)user).Target!;

        // The window exists before the first frame draws: hand it over, and make resizing fast.
        state.Hooking(ui);

        // The previous frame has been presented by now, so its painters and the
        // arena that holds this frame's argument strings can be reused.
        state.Frame.Reset();
        Utf8.BeginFrame();
        state.Frame.SyncLocalization(ui);
        if (Features.Live) state.Live?.EnterFrame();
        try
        {
            state.Draw(new Ui(ui, state.Frame));
        }
        finally
        {
            // A live capture is reading this frame's labels out of the arena the line above will
            // reset on the next callback, so its copies are taken now, before that happens.
            if (Features.Live) state.Live?.SealFrame();
        }
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
        _frame.Release();
    }
}
