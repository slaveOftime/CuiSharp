using System.Runtime.InteropServices;

namespace CuiSharp.Tests;

/// <summary>
/// The window controls CUI does not own: maximize, restore and close, driven through the
/// plain Win32 calls beside the ones CUI itself uses.
/// </summary>
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
        GetWindowRect(app.NativeWindow, out var maximized);
        Assert.True(maximized.Right - maximized.Left > normal.Right - normal.Left);

        app.ToggleMaximize();
        Assert.False(app.IsMaximized);
        GetWindowRect(app.NativeWindow, out var restored);
        Assert.Equal(normal.Right - normal.Left, restored.Right - restored.Left);
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
