using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CuiSharp.Interop;

namespace CuiSharp.Hosting;

/// <summary>What the resize hook does with one window message.</summary>
internal enum ResizeStep
{
    /// <summary>Hand it to the window procedure CUI installed, unchanged.</summary>
    Forward,

    /// <summary>
    /// Forward a paint with the app told a drag is in progress, so it draws the new size exactly
    /// once: CUI's paint handler runs a frame only while it believes a drag is on.
    /// </summary>
    RenderOnce,

    /// <summary>Swallow it — the handler behind it draws a frame this message already covers.</summary>
    Suppress,
}

/// <summary>
/// Makes a window resize at the speed the mouse moves. This is a Windows-only hook over CUI's own
/// window procedure, because the cost is in how Win32 drives a drag:
/// <list type="number">
/// <item>
/// The window class has <c>CS_HREDRAW | CS_VREDRAW</c>, so a size change invalidates the whole
/// client area. CUI's <c>WM_SIZE</c> handler draws a live frame <em>before</em> the paint that
/// follows it, so one mouse step costs two full frames and two presents.
/// </item>
/// <item>
/// <c>WM_SIZE</c> arrives synchronously from inside the modal resize loop for every mouse sample —
/// a 500 Hz mouse asks for 500 frames a second — while <c>WM_PAINT</c> is coalesced by Windows and
/// only arrives once the input queue drains. Drawing in <c>WM_SIZE</c> is therefore both duplicated
/// and uncoalesced.
/// </item>
/// <item>
/// With vsync on, each of those presents blocks inside the modal loop for a display refresh, so the
/// window falls further behind the cursor with every step.
/// </item>
/// </list>
/// The hook stops the app drawing in <c>WM_SIZE</c> (by not letting CUI see the drag begin), lets
/// the coalesced paint draw the newest size once, and stops waiting for the display for the length
/// of the drag. Nothing about the app or the native library changes: it drives the live-frame path
/// CUI already has for exactly this case.
/// </summary>
internal static unsafe class LiveResize
{
    private static readonly Dictionary<nint, Hook> Installed = [];
    private static nint _proc;
    private static nint _swapInterval;
    private static int _interval = -1;

    /// <summary>Decides what one message needs. Pure, so the rule can be tested without a window.</summary>
    internal static ResizeStep Decide(uint message, bool inSizeMove, bool sizeChanged) => message switch
    {
        // Swallowed so CUI never learns a drag started: its own WM_SIZE handler would draw, and
        // that duplicate is the thing this hook exists to remove. A paint below replays the start.
        Win32.WmEnterSizeMove or Win32.WmExitSizeMove => ResizeStep.Suppress,

        // The coalesced one: a drag is on and the size moved since the last paint, so draw it now.
        Win32.WmPaint when inSizeMove && sizeChanged => ResizeStep.RenderOnce,

        // Anything else, including a paint during a drag that changed no size (a move): as it was.
        _ => ResizeStep.Forward,
    };

    /// <summary>Hooks the window once. Safe to call from the frame loop: the second call does nothing.</summary>
    internal static void Install(nint hwnd, bool noVsync)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0 || Installed.ContainsKey(hwnd)) return;

        _proc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&Proc;
        nint previous = Win32.HookWindowProc(hwnd, _proc);
        if (previous == 0) return;

        Installed[hwnd] = new Hook(hwnd, previous, wantsVsync: !noVsync);
    }

    /// <summary>Puts the window procedure back, and the display interval with it.</summary>
    internal static void Uninstall(nint hwnd)
    {
        if (hwnd == 0 || !Installed.Remove(hwnd, out Hook? hook)) return;

        Vsync(wants: hook.WantsVsync);
        if (Win32.IsWindow(hwnd) != 0) Win32.UnhookWindowProc(hwnd, hook.Previous);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint Proc(nint hwnd, uint message, nint wparam, nint lparam)
    {
        if (!Installed.TryGetValue(hwnd, out Hook? hook)) return 0;

        switch (message)
        {
            case Win32.WmEnterSizeMove:
                hook.InSizeMove = true;
                hook.SizeChanged = false;
                // A present that waits for the display, inside a loop the OS drives, is the stall.
                Vsync(wants: false);
                return 0;

            case Win32.WmExitSizeMove:
                hook.InSizeMove = false;
                Vsync(wants: hook.WantsVsync);
                if (hook.SizeChanged) DrawOnce(hook);
                return 0;

            case Win32.WmSize:
                hook.SizeChanged = true;
                break;

            case Win32.WmNcDestroy:
                // The window is going away: stop holding its procedure, then let CUI clean up.
                Installed.Remove(hwnd);
                break;
        }

        if (Decide(message, hook.InSizeMove, hook.SizeChanged) == ResizeStep.RenderOnce)
        {
            DrawOnce(hook);
            return 0;
        }

        return Win32.CallWindowProc(hook.Previous, hwnd, message, wparam, lparam);
    }

    /// <summary>
    /// One frame at the current size: CUI's paint handler draws when it believes a drag is on, so
    /// the pair of messages is what tells it, and it is undone before returning.
    /// </summary>
    private static void DrawOnce(Hook hook)
    {
        hook.SizeChanged = false;
        Win32.CallWindowProc(hook.Previous, hook.Handle, Win32.WmEnterSizeMove, 0, 0);
        Win32.CallWindowProc(hook.Previous, hook.Handle, Win32.WmPaint, 0, 0);
        Win32.CallWindowProc(hook.Previous, hook.Handle, Win32.WmExitSizeMove, 0, 0);
    }

    /// <summary>Sets the swap interval, if this process has a context to set it on.</summary>
    private static void Vsync(bool wants)
    {
        int wanted = wants ? 1 : 0;
        if (_interval == wanted) return;
        if (SwapInterval(wanted)) _interval = wanted;
    }

    private static bool SwapInterval(int interval)
    {
        if (_swapInterval == 0)
        {
            nint opengl = Win32.GetModuleHandle("opengl32.dll");
            if (opengl == 0) return false;
            _swapInterval = Win32.GetProcAddress(opengl, "wglSwapIntervalEXT");
            if (_swapInterval == 0) return false;
        }

        try
        {
            ((delegate* unmanaged[Stdcall]<int, int>)_swapInterval)(interval);
            return true;
        }
        catch (Exception)
        {
            // An old driver, or no context current: vsync simply stays as the app asked for it.
            _swapInterval = 0;
            return false;
        }
    }

    /// <summary>The state of one hooked window.</summary>
    private sealed class Hook(nint handle, nint previous, bool wantsVsync)
    {
        internal nint Handle { get; } = handle;

        internal nint Previous { get; } = previous;

        internal bool WantsVsync { get; } = wantsVsync;

        internal bool InSizeMove { get; set; }

        internal bool SizeChanged { get; set; }
    }
}
