// Hand-written, not generated: the few OS calls CuiSharp needs on top of CUI's
// own surface, for the parts of the window that CUI deliberately leaves to the
// app (an app-drawn caption, see CuiAppOptions.Frameless).
using System.Runtime.InteropServices;

namespace CuiSharp.Interop;

internal static partial class Win32
{
    internal const int SwMaximize = 3;
    internal const int SwMinimize = 6;
    internal const int SwRestore = 9;

    private const uint WmClose = 0x0010;

    /// <summary>BOOL ShowWindow(HWND, int).</summary>
    [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
    internal static partial int ShowWindow(nint hwnd, int command);

    /// <summary>BOOL IsZoomed(HWND): non-zero when the window is maximized.</summary>
    [LibraryImport("user32.dll", EntryPoint = "IsZoomed")]
    internal static partial int IsZoomed(nint hwnd);

    /// <summary>BOOL PostMessageW(HWND, UINT, WPARAM, LPARAM).</summary>
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    internal static partial int PostMessage(nint hwnd, uint message, nint wparam, nint lparam);

    /// <summary>Posts a close request, so the frame loop ends the way the OS close button ends it.</summary>
    internal static int RequestClose(nint hwnd) => PostMessage(hwnd, WmClose, 0, 0);
}
