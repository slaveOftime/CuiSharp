// Hand-written, not generated: the few OS calls CuiSharp needs on top of CUI's
// own surface. Everything here is Windows-only and every entry point is called
// only after an OperatingSystem.IsWindows() check.
using System.Runtime.InteropServices;

namespace CuiSharp.Interop;

internal static unsafe partial class Win32
{
    internal const int SwMaximize = 3;
    internal const int SwMinimize = 6;
    internal const int SwRestore = 9;

    /// <summary>Posts nothing and does nothing: the wake a waiting loop needs, with no effect of its own.</summary>
    internal const uint WmNull = 0x0000;

    internal const uint WmDestroy = 0x0002;
    internal const uint WmSize = 0x0005;
    internal const uint WmTimer = 0x0113;
    internal const uint WmPaint = 0x000F;
    internal const uint WmClose = 0x0010;
    internal const uint WmSetIcon = 0x0080;
    internal const uint WmNcDestroy = 0x0082;
    internal const uint WmApp = 0x8000;
    internal const uint WmEnterSizeMove = 0x0231;
    internal const uint WmExitSizeMove = 0x0232;

    /// <summary>The parent that makes a window message-only: it exists to receive messages, not to show.</summary>
    internal static readonly nint HWNDMessage = -3;

    private const int GwlWndProc = -4;
    private const int GwlUserData = -21;

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

    /// <summary>LRESULT CallWindowProcW(WNDPROC, HWND, UINT, WPARAM, LPARAM).</summary>
    [LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")]
    internal static partial nint CallWindowProc(nint previous, nint hwnd, uint message, nint wparam, nint lparam);

    /// <summary>LONG_PTR SetWindowLongPtrW(HWND, int, LONG_PTR).</summary>
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    /// <summary>LONG_PTR GetWindowLongPtrW(HWND, int).</summary>
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static partial nint GetWindowLongPtr(nint hwnd, int index);

    /// <summary>HWND GetActiveWindow(void).</summary>
    [LibraryImport("user32.dll", EntryPoint = "GetActiveWindow")]
    internal static partial nint GetActiveWindow();

    /// <summary>int GetClassNameW(HWND, LPWSTR, int).</summary>
    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    internal static partial int GetClassName(nint hwnd, char* name, int count);

    /// <summary>BOOL EnumThreadWindows(DWORD, WNDENUMPROC, LPARAM).</summary>
    [LibraryImport("user32.dll", EntryPoint = "EnumThreadWindows")]
    internal static partial int EnumThreadWindows(uint thread, nint callback, nint lparam);

    /// <summary>BOOL SetWindowTextW(HWND, LPCWSTR).</summary>
    [LibraryImport("user32.dll", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SetWindowText(nint hwnd, string text);

    /// <summary>BOOL IsWindow(HWND).</summary>
    [LibraryImport("user32.dll", EntryPoint = "IsWindow")]
    internal static partial int IsWindow(nint hwnd);

    /// <summary>BOOL GetClientRect(HWND, LPRECT).</summary>
    [LibraryImport("user32.dll", EntryPoint = "GetClientRect")]
    internal static partial int GetClientRect(nint hwnd, out Rect rect);

    /// <summary>HMODULE GetModuleHandleW(LPCWSTR).</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandle(string? name);

    /// <summary>HICON LoadImageW(HINSTANCE, LPCWSTR, IMAGE_ICON, int, int, UINT).</summary>
    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", SetLastError = true)]
    internal static partial nint LoadIcon(nint module, nint name, int imageType, int width, int height, uint flags);

    /// <summary>int GetSystemMetrics(int).</summary>
    [LibraryImport("user32.dll", EntryPoint = "GetSystemMetrics")]
    internal static partial int GetSystemMetrics(int index);

    /// <summary>LRESULT SendMessageW(HWND, UINT, WPARAM, LPARAM).</summary>
    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    internal static partial nint SendMessage(nint hwnd, uint message, nint wparam, nint lparam);

    /// <summary>FARPROC GetProcAddress(HMODULE, LPCSTR).</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetProcAddress")]
    internal static partial nint GetProcAddress(nint module, [MarshalAs(UnmanagedType.LPStr)] string name);

    /// <summary>DWORD GetCurrentThreadId(void).</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    internal static partial uint GetCurrentThreadId();

    /// <summary>A window rectangle, in the layout RECT uses.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;

        internal readonly int Width => Right - Left;
        internal readonly int Height => Bottom - Top;
    }

    // Set by the enumeration callback below; only ever read and written on the
    // thread that owns the window, while that enumeration is running.
    private static nint _found;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static int OnThreadWindow(nint hwnd, nint lparam)
    {
        if (!IsCuiWindow(hwnd)) return 1;
        _found = hwnd;
        return 0;
    }

    /// <summary>
    /// The window CUI created on this thread: the one whose user data is a <c>cui_app*</c>.
    /// The CUI app loop owns the window and never hands the pointer back, so the loop's own
    /// window is where the app is found again — see <see cref="Hosting.LiveResize"/>.
    /// </summary>
    internal static nint FindCuiWindow()
    {
        nint active = GetActiveWindow();
        if (IsCuiWindow(active)) return active;

        nint previous = _found;
        _found = 0;
        try
        {
            EnumThreadWindows(GetCurrentThreadId(),
                (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&OnThreadWindow, 0);
            return _found;
        }
        finally
        {
            _found = previous;
        }
    }

    /// <summary>The <c>cui_app*</c> a window belongs to, or 0 when it is not a CUI window.</summary>
    internal static nint AppOf(nint hwnd) => hwnd == 0 ? 0 : GetWindowLongPtr(hwnd, GwlUserData);

    /// <summary>Whether this is one of the windows CUI registers ("CUIWindow" with an app behind it).</summary>
    internal static bool IsCuiWindow(nint hwnd)
    {
        if (AppOf(hwnd) == 0) return false;
        char* name = stackalloc char[64];
        int length = GetClassName(hwnd, name, 64);
        return length == 9 && new ReadOnlySpan<char>(name, 9).SequenceEqual("CUIWindow");
    }

    /// <summary>WNDCLASSW, with the two string fields as raw pointers the caller keeps alive across the call.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WndClass
    {
        internal uint Style;
        internal nint WndProc;
        internal int CbClsExtra;
        internal int CbWndExtra;
        internal nint Instance;
        internal nint Icon;
        internal nint Cursor;
        internal nint Background;
        internal nint MenuName;
        internal nint ClassName;
    }

    /// <summary>ATOM RegisterClassW(const WNDCLASSW*).</summary>
    [LibraryImport("user32.dll", EntryPoint = "RegisterClassW", SetLastError = true)]
    internal static partial ushort RegisterClass(in WndClass wc);

    /// <summary>HWND CreateWindowExW(...).</summary>
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateWindowEx(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    /// <summary>UINT_PTR SetTimer(HWND, UINT_PTR, UINT, TIMERPROC): NULL proc posts WM_TIMER to the window procedure.</summary>
    [LibraryImport("user32.dll", EntryPoint = "SetTimer")]
    internal static partial nint SetTimer(nint hwnd, nint eventId, uint elapsedMs, nint proc);

    /// <summary>BOOL KillTimer(HWND, UINT_PTR).</summary>
    [LibraryImport("user32.dll", EntryPoint = "KillTimer")]
    internal static partial int KillTimer(nint hwnd, nint eventId);

    /// <summary>BOOL DestroyWindow(HWND).</summary>
    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow")]
    internal static partial int DestroyWindow(nint hwnd);

    /// <summary>MSG, the subset a pump loop reads.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Msg
    {
        internal nint Hwnd;
        internal uint Message;
        internal nint WParam;
        internal nint LParam;
        internal uint Time;
        internal int PtX;
        internal int PtY;
    }

    internal const uint PmRemove = 1;

    /// <summary>BOOL PeekMessageW(MSG*, HWND, UINT, UINT, UINT).</summary>
    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    internal static partial int PeekMessage(out Msg msg, nint hwnd, uint min, uint max, uint remove);

    /// <summary>LRESULT DispatchMessageW(const MSG*).</summary>
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    internal static partial nint DispatchMessage(in Msg msg);

    /// <summary>LRESULT DefWindowProcW(HWND, UINT, WPARAM, LPARAM).</summary>
    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    internal static partial nint DefWindowProc(nint hwnd, uint message, nint wparam, nint lparam);

    /// <summary>BOOL UnregisterClassW(LPCWSTR, HINSTANCE).</summary>
    [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int UnregisterClass(string className, nint instance);

    /// <summary>Replaces the window procedure, returning the one CUI installed.</summary>
    internal static nint HookWindowProc(nint hwnd, nint replacement) =>
        SetWindowLongPtr(hwnd, GwlWndProc, replacement);

    /// <summary>Puts a window procedure back.</summary>
    internal static void UnhookWindowProc(nint hwnd, nint original) =>
        SetWindowLongPtr(hwnd, GwlWndProc, original);
}
