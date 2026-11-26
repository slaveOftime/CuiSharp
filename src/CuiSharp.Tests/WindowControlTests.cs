using System.Runtime.InteropServices;
using CuiSharp.Interop;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [LibraryImport("user32.dll")]
    private static partial int ClientToScreen(nint hwnd, ref Point point);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ATitleBarReplacesTheSystemCaptionOnlyWhenOptedIn(bool custom, bool fixedSize)
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = CuiApp.Create(new CuiAppOptions
        {
            Width = 420,
            Height = 320,
            Hidden = true,
            Continuous = true,
            Renderer = Renderer.Software,
            CustomTitleBar = custom,
            FixedSize = fixedSize,
        });
        Assert.NotNull(app);
        nint hwnd = app.NativeWindow;
        var origin = new Point();
        Assert.NotEqual(0, ClientToScreen(hwnd, ref origin));
        Assert.NotEqual(0, GetWindowRect(hwnd, out var outer));
        if (!custom)
        {
            Assert.True(origin.Y > outer.Top);
            return;
        }

        Assert.Equal(outer.Top, origin.Y);
        float scale = Win32.GetDpiForWindow(hwnd) / 96f;
        nint Hit(int x, int y)
        {
            int screenX = origin.X + (int)(x * scale), screenY = origin.Y + (int)(y * scale);
            nint coords = (ushort)screenX | ((nint)(ushort)screenY << 16);
            return Win32.SendMessage(hwnd, Win32.WmNcHitTest, 0, coords);
        }

        Assert.True(app.Frame());
        app.Ui.TitleBar("Window under test");
        Assert.True(app.Frame()); // CUI has now laid out the preceding frame.
        app.Ui.TitleBar("Window under test");

        Assert.Equal(Win32.HtCaption, Hit(80, 24));
        Assert.Equal(Win32.HtClient, Hit(393, 24)); // Close is an ordinary CUI control.
        Assert.Equal(fixedSize ? Win32.HtClient : Win32.HtMaxButton, Hit(353, 24));
        if (!fixedSize) Assert.Equal(12, Hit(80, 1)); // HTTOP: the resize border still wins.

        if (!fixedSize)
        {
            // Compare the same points on the same frame: replacing the Windows hook with
            // the raw query would lose both hits; managed widgets do not register native chrome.
            Assert.Equal(TitleBarHit.None, Native.TitleBarHitTest(app.Ui.Handle, 80, 24));
            Assert.Equal(TitleBarHit.None, Native.TitleBarHitTest(app.Ui.Handle, 353, 24));
        }

        Assert.True(app.Frame());
        var ui = app.Ui;
        using (ui.Disabled(true)) ui.TitleBar("Window under test");
        Assert.Equal(Win32.HtClient, Hit(80, 24));
        Assert.Equal(Win32.HtClient, Hit(353, 24));
    }

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
        Assert.False(app.TryGetPlacement(out _));
        Assert.False(window.TryGetPlacement(out _));
    }

    [Fact]
    public void PlacementRoundTripsNormalBoundsAndMaximizedState()
    {
        if (!OperatingSystem.IsWindows()) return;

        WindowPlacement saved;
        using (var first = CuiApp.Create(new CuiAppOptions
        {
            Title = "PlacementFirst",
            Width = 440,
            Height = 320,
            Hidden = true,
            Renderer = Renderer.Software,
        }))
        {
            Assert.NotNull(first);
            first.SetPosition(140, 120);
            first.SetSize(520, 380);
            first.ToggleMaximize();

            Assert.True(first.TryGetPlacement(out saved));
            Assert.Equal(520, saved.Width);
            Assert.Equal(380, saved.Height);
            Assert.Equal(WindowState.Maximized, saved.State);
            Assert.True(saved.HasPosition);
            Assert.Equal(140, saved.X);
            Assert.Equal(120, saved.Y);
        }

        using var reopened = CuiApp.Create(new CuiAppOptions
        {
            Title = "PlacementReopened",
            Width = 360,
            Height = 280,
            // CUI applies the startup state when it shows the window.
            Placement = saved,
            Renderer = Renderer.Software,
        });
        Assert.NotNull(reopened);
        Assert.True(reopened.TryGetPlacement(out var second));
        Assert.Equal(520, second.Width);
        Assert.Equal(380, second.Height);
        Assert.Equal(140, second.X);
        Assert.Equal(120, second.Y);
        Assert.True(second.HasPosition);
        Assert.Equal(WindowState.Maximized, second.State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FollowingTheSystemKeepsAccentChangesWithoutReapplyingTheManualTheme(bool accentFirst)
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = CuiApp.Create(new CuiAppOptions { Hidden = true, Renderer = Renderer.Software });
        Assert.NotNull(app);
        app.SetFollowSystemTheme(true);
        Theme systemTheme = app.Ui.Theme;
        app.SetFollowSystemTheme(false);

        // Queue a different theme so an accidental reapply is visible on either OS setting.
        app.SetTheme(systemTheme == Theme.Dark ? Theme.Light : Theme.Dark);
        Accent accent = app.Ui.Accent == Accent.Blue ? Accent.Green : Accent.Blue;
        if (accentFirst) app.SetAccent(accent);
        app.SetFollowSystemTheme(true);
        if (!accentFirst) app.SetAccent(accent);

        app.RequestClose();
        Assert.False(app.Frame());
        Assert.Equal(systemTheme, app.Ui.Theme);
        Assert.Equal(accent, app.Ui.Accent);
    }

    [Fact]
    public void StoppingFollowSystemThemeLetsAnExplicitThemeStick()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = CuiApp.Create(new CuiAppOptions
        {
            Title = "WindowSystemTheme",
            Width = 360,
            Height = 280,
            Hidden = true,
            Renderer = Renderer.Software,
        });
        Assert.NotNull(app);

        app.SetFollowSystemTheme(true);
        app.SetFollowSystemTheme(false);
        app.SetTheme(Theme.Dark);
        app.RequestClose();
        Assert.False(app.Frame());
        Assert.Equal(Theme.Dark, app.Ui.Theme);
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
