using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>
/// The OS window behind a running app: the handle to brand, log and query, without a P/Invoke of
/// your own.
/// <para>
/// <see cref="CuiAppOptions.WindowCreated"/> hands one of these over after CUI has created the
/// window and before the first frame draws, which is the first moment a window has a handle at
/// all — far too late for <see cref="Hosting.Component.OnInitialized"/>, which runs before the
/// window exists.
/// </para>
/// <para>
/// Windows-only members report <c>0</c>/<c>false</c> elsewhere: the app loop keeps its
/// <c>cui_app*</c> to itself, and Windows is the one platform where the window can be asked for
/// it back (its user data is that pointer).
/// </para>
/// </summary>
public sealed class CuiWindow
{
    private nint _app;

    internal CuiWindow(nint app) => _app = app;

    /// <summary>
    /// The HWND on Windows, the X11 window id on Linux, and <c>NSWindow *</c> on macOS — whatever
    /// this platform's own APIs expect. 0 until the window exists.
    /// </summary>
    public nint NativeWindow => _app == 0 ? 0 : Native.AppNativeWindow(_app);

    /// <summary>The backend that created the window: <c>win32</c>, <c>x11</c>, <c>cocoa</c>, …</summary>
    public string Backend
    {
        get
        {
            var name = Native.AppBackend(_app);
            return System.Runtime.InteropServices.Marshal.PtrToStringUTF8(name) ?? "unknown";
        }
    }

    /// <summary>
    /// True when OpenGL was unavailable and CUI draws with its software rasterizer. Worth logging
    /// on a station or a remote session: it explains a frame rate nobody can reproduce.
    /// </summary>
    public bool IsSoftwareRenderer => _app != 0 && Native.AppIsSoftware(_app) != 0;

    /// <summary>The client area in physical pixels.</summary>
    public (int Width, int Height) PixelSize
    {
        get
        {
            if (_app == 0) return (0, 0);
            var size = Native.AppSize(_app);
            return ((int)size.X, (int)size.Y);
        }
    }

    /// <summary>True when the window is maximized.</summary>
    public bool IsMaximized => _app != 0 && OperatingSystem.IsWindows() && Win32.IsZoomed(NativeWindow) != 0;

    /// <summary>Retitles the window. Safe to call from a frame.</summary>
    public void SetTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        if (_app != 0) Native.AppSetTitle(_app, Utf8.Get(title));
    }

    /// <summary>Minimizes the window.</summary>
    public void Minimize()
    {
        if (_app != 0 && OperatingSystem.IsWindows()) Win32.ShowWindow(NativeWindow, Win32.SwMinimize);
    }

    /// <summary>Maximizes the window, or restores it when it is already maximized.</summary>
    public void ToggleMaximize()
    {
        if (_app == 0 || !OperatingSystem.IsWindows()) return;
        Win32.ShowWindow(NativeWindow, Win32.IsZoomed(NativeWindow) != 0 ? Win32.SwRestore : Win32.SwMaximize);
    }

    /// <summary>Closes the window, exactly as its own close button would.</summary>
    public void RequestClose()
    {
        if (_app == 0) return;
        if (OperatingSystem.IsWindows()) Win32.RequestClose(NativeWindow);
        else Native.AppQuit(_app);
    }

    /// <summary>Stops handing the window out; the loop that owns it is ending.</summary>
    internal void Release() => _app = 0;
}
