using System.Runtime.InteropServices;

namespace CuiSharp.Tests;

/// <summary>
/// The window controls. CUI asks the window manager for each of them, so these run the same path a
/// Linux or macOS app takes, on the one platform CI has a window on.
/// </summary>
[Collection("ProcessWindowState")]
public partial class WindowControlTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [LibraryImport("user32.dll")]
    private static partial int GetWindowRect(nint hwnd, out Rect rect);

    [Fact]
    public void MaximizeAndRestoreChangeTheWindowSize()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = CuiApp.Create(new CuiAppOptions
        {
            Title = "WindowControl",
            Width = 400,
            Height = 300,
            Hidden = true,
            Renderer = Renderer.Software,
        });
        Assert.NotNull(app);

        GetWindowRect(app.NativeWindow, out var normal);
        Assert.False(app.IsMaximized);

        app.ToggleMaximize();
        Assert.True(app.IsMaximized);
        // CuiApp answers through the one window it owns, so the two cannot disagree.
        Assert.True(app.Window.IsMaximized);
        GetWindowRect(app.NativeWindow, out var maximized);
        Assert.True(maximized.Right - maximized.Left > normal.Right - normal.Left);

        app.ToggleMaximize();
        Assert.False(app.IsMaximized);
        Assert.False(app.Window.IsMaximized);
        GetWindowRect(app.NativeWindow, out var restored);
        Assert.Equal(normal.Right - normal.Left, restored.Right - restored.Left);
    }

    [Fact]
    public void AFixedSizeWindowNeverMaximizes()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = CuiApp.Create(new CuiAppOptions
        {
            Title = "WindowFixed",
            Width = 420,
            Height = 320,
            FixedSize = true,
            Hidden = true,
            Renderer = Renderer.Software,
        });
        Assert.NotNull(app);

        // CUI refuses to maximize a fixed-size window, because its borders are gone already.
        GetWindowRect(app.NativeWindow, out var before);
        Assert.True(before.Right - before.Left > 0);
        app.ToggleMaximize();
        Assert.False(app.IsMaximized);
        GetWindowRect(app.NativeWindow, out var after);
        Assert.Equal(before.Right - before.Left, after.Right - after.Left);
    }

    [Fact]
    public void MinimizingThenMaximizingEndsNormal()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = CuiApp.Create(new CuiAppOptions
        {
            Title = "WindowMinimize",
            Width = 400,
            Height = 300,
            Hidden = true,
            Renderer = Renderer.Software,
        });
        Assert.NotNull(app);

        // Maximize first so a Minimize that did nothing would leave IsMaximized true; then a
        // toggle from minimized maximizes again and a second toggle comes back normal, because the
        // toggle reads the state CUI reports rather than a flag of its own.
        app.ToggleMaximize();
        Assert.True(app.IsMaximized);
        app.Minimize();
        Assert.False(app.IsMaximized);
        app.ToggleMaximize();
        Assert.True(app.IsMaximized);
        app.ToggleMaximize();
        Assert.False(app.IsMaximized);
    }

    [Fact]
    public void DisposingTheAppReleasesItsWindow()
    {
        if (!OperatingSystem.IsWindows()) return;

        var app = CuiApp.Create(new CuiAppOptions
        {
            Title = "WindowDispose",
            Width = 320,
            Height = 240,
            Hidden = true,
            Renderer = Renderer.Software,
        });
        Assert.NotNull(app);

        var window = app.Window;
        Assert.NotEqual(0, window.NativeWindow);

        app.Dispose();

        // A caller that held onto CuiWindow (or calls through CuiApp after Dispose) must not reach
        // the freed cui_app* or fall through to CUI's "current app".
        Assert.Equal(0, window.NativeWindow);
        Assert.Equal((0, 0), window.PixelSize);
        Assert.False(window.IsMaximized);
        Assert.False(app.IsMaximized);
        window.Minimize();
        window.ToggleMaximize();
        window.RequestClose();
        window.SetTitle("Gone");
        app.Minimize();
        app.ToggleMaximize();
        app.RequestClose();
        Assert.False(app.Screenshot(new byte[320 * 240 * 4], 320, 240));
    }

    [Fact]
    public void RequestCloseEndsTheFrameLoop()
    {
        if (!OperatingSystem.IsWindows()) return;

        var app = CuiApp.Create(new CuiAppOptions
        {
            Title = "WindowClose",
            Width = 320,
            Height = 240,
            Hidden = true,
            Renderer = Renderer.Software,
        });
        Assert.NotNull(app);
        Assert.True(app.Frame());

        app.RequestClose();
        // WM_CLOSE is posted, so the loop notices on the next turn.
        Assert.False(app.Frame());
        app.Dispose();
    }
}
