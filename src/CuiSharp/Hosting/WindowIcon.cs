// Hand-written. Puts the icon the .NET apphost embeds — the one ApplicationIcon points at — into
// the title bar and the taskbar button.
using CuiSharp.Interop;

namespace CuiSharp.Hosting;

internal static class WindowIcon
{
    // The .NET apphost embeds the ApplicationIcon as RT_GROUP_ICON 32512, which is IDI_APPLICATION.
    // The CUI window class asks for group 1 and, not finding it, falls back to the icon a NULL
    // instance loads: the system's generic application logo. The embedded icon then shows on the
    // exe file in Explorer but not on the window — Explorer reads the resource, the shell's window
    // decorations ask the window. WM_SETICON is the call only managed code that knows the id can make.
    private const int IdiApplication = 32512;
    private const int ImageIcon = 1;
    private const int SmCxIcon = 11;
    private const int SmCyIcon = 12;
    private const int SmCxSmIcon = 49;
    private const int SmCySmIcon = 50;
    private const int IconSmall = 0;
    private const int IconBig = 1;

    // Process-lifetime copies, loaded once and reused by every window: WM_SETICON does not take
    // ownership of an icon, so destroying one on window close would strand the other windows, and
    // two handles that live until the process exits are the honest alternative.
    private static nint _large;
    private static nint _small;
    private static bool _tried;

    /// <summary>Applies the host executable's icon to a top-level window. Does nothing when the
    /// host embeds none, leaving the class icon — the system default — in place.</summary>
    internal static void Install(nint hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0)
        {
            return;
        }

        if (!_tried)
        {
            _tried = true;
            nint module = Win32.GetModuleHandle(null);
            _large = Win32.LoadIcon(module, IdiApplication, ImageIcon,
                Win32.GetSystemMetrics(SmCxIcon), Win32.GetSystemMetrics(SmCyIcon), 0);
            _small = Win32.LoadIcon(module, IdiApplication, ImageIcon,
                Win32.GetSystemMetrics(SmCxSmIcon), Win32.GetSystemMetrics(SmCySmIcon), 0);
        }

        if (_large == 0 && _small == 0)
        {
            return;
        }

        _ = Win32.SendMessage(hwnd, Win32.WmSetIcon, IconSmall, _small);
        _ = Win32.SendMessage(hwnd, Win32.WmSetIcon, IconBig, _large);
    }
}
