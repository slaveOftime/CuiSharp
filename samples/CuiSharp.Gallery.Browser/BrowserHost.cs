using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using CuiSharp;

[assembly: SupportedOSPlatform("browser")]

namespace CuiSharp.Gallery.Browser;

/// <summary>Runs the real gallery on a windowless CUI surface and presents its pixels to the browser.</summary>
public static partial class BrowserHost
{
    private static CuiSurface? _surface;
    private static GalleryApp? _gallery;
    private static byte[] _pixels = [];
    private static GCHandle _pixelPin;
    private static int _pixelAddress;
    private static double _lastTimestamp;
    private static bool _forcePresent = true;
    private static bool _stopped;
    private static string? _failure;

    internal static void Initialize()
    {
        _surface = CuiSurface.Create(1, 1, theme: Theme.Light, accent: Accent.Violet);
        _surface.Incremental = true;
        _gallery = new GalleryApp();
    }

    /// <summary>Builds, rasterizes, and presents one frame; called by the browser's animation loop.</summary>
    [JSExport]
    public static void Frame(double timestampMilliseconds)
    {
        var surface = _surface;
        var gallery = _gallery;
        if (_stopped || _failure is not null || surface is null || gallery is null) return;

        try
        {
            float delta = _lastTimestamp == 0
                ? 1f / 60f
                : (float)Math.Clamp((timestampMilliseconds - _lastTimestamp) / 1000.0, 1.0 / 240.0, 0.1);
            _lastTimestamp = timestampMilliseconds;

            surface.BeginFrame(delta);
            gallery.Frame(surface.Ui);
            surface.EndFrame();
            surface.Render(_pixels, PixelFormat.Rgba);

            RectF? damage = surface.LastDamage;
            int x = 0, y = 0, dirtyWidth = 0, dirtyHeight = 0;
            if (_forcePresent)
            {
                dirtyWidth = surface.PixelWidth;
                dirtyHeight = surface.PixelHeight;
            }
            else if (damage is { } rect && rect.W > 0 && rect.H > 0)
            {
                x = (int)rect.X;
                y = (int)rect.Y;
                dirtyWidth = (int)rect.W;
                dirtyHeight = (int)rect.H;
            }

            if (dirtyWidth > 0 && dirtyHeight > 0)
            {
                Present(_pixelAddress, surface.PixelWidth, surface.PixelHeight, x, y, dirtyWidth, dirtyHeight);
                _forcePresent = false;
            }

            if (surface.Faults.Count != 0)
                throw new InvalidOperationException(surface.Faults[0].ToString());
        }
        catch (Exception error)
        {
            _failure = error.ToString();
            Console.Error.WriteLine(_failure);
        }
    }

    /// <summary>Updates the logical viewport and device-pixel scale when the canvas changes size.</summary>
    [JSExport]
    public static void Resize(int width, int height, float scale)
    {
        var surface = _surface;
        if (_stopped || surface is null) return;

        width = Math.Max(1, width);
        height = Math.Max(1, height);
        scale = float.IsFinite(scale) && scale > 0 ? scale : 1;
        surface.Resize(width, height, scale);

        int length = checked(surface.PixelWidth * surface.PixelHeight * 4);
        if (_pixels.Length != length)
        {
            if (_pixelPin.IsAllocated) _pixelPin.Free();
            _pixels = new byte[length];
            _pixelPin = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
            _pixelAddress = checked((int)_pixelPin.AddrOfPinnedObject());
        }

        _forcePresent = true;
    }

    [JSExport]
    public static void SetPage(int page)
    {
        if (_gallery is not null && page is >= 0 and < GalleryApp.PageCount)
            _gallery.Page = page;
    }

    [JSExport]
    public static void PointerMove(float x, float y) => _surface?.MouseMove(x, y);

    [JSExport]
    public static void PointerDown(int button) =>
        _surface?.MouseDown(button switch { 1 => MouseButton.Right, 2 => MouseButton.Middle, _ => MouseButton.Left });

    [JSExport]
    public static void PointerUp(int button) =>
        _surface?.MouseUp(button switch { 1 => MouseButton.Right, 2 => MouseButton.Middle, _ => MouseButton.Left });

    [JSExport]
    public static void PointerLeave() => _surface?.MouseLeave();

    [JSExport]
    public static void Scroll(float dy, float dx) => _surface?.Scroll(dy, dx);

    [JSExport]
    public static void Focus(bool focused) => _surface?.Focus(focused);

    [JSExport]
    public static void KeyDown(string code, int modifiers) =>
        _surface?.KeyDown(MapKey(code), (Mods)modifiers);

    [JSExport]
    public static void KeyUp(string code, int modifiers) =>
        _surface?.KeyUp(MapKey(code), (Mods)modifiers);

    [JSExport]
    public static void TypeText(string text)
    {
        if (text.Length != 0) _surface?.Type(text);
    }

    /// <summary>Releases the page texture owners and the long-lived pinned framebuffer on navigation away.</summary>
    [JSExport]
    public static void Shutdown()
    {
        if (_stopped) return;
        _stopped = true;
        _gallery?.Dispose();
        _gallery = null;
        _surface?.Dispose();
        _surface = null;
        if (_pixelPin.IsAllocated) _pixelPin.Free();
        _pixels = [];
        _pixelAddress = 0;
    }

    [JSImport("canvas.present", "main.js")]
    private static partial void Present(int pixelAddress, int width, int height, int x, int y, int dirtyWidth, int dirtyHeight);

    private static Key MapKey(string code)
    {
        if (code.Length == 4 && code.StartsWith("Key", StringComparison.Ordinal)
            && code[3] is >= 'A' and <= 'Z')
        {
            return (Key)((int)Key.A + code[3] - 'A');
        }

        if (code.Length == 6 && code.StartsWith("Digit", StringComparison.Ordinal)
            && code[5] is >= '0' and <= '9')
        {
            return (Key)((int)Key.D0 + code[5] - '0');
        }

        if (code.Length >= 2 && code[0] == 'F' && int.TryParse(code.AsSpan(1), out int functionKey)
            && functionKey is >= 1 and <= 12)
        {
            return (Key)((int)Key.F1 + functionKey - 1);
        }

        return code switch
        {
            "Tab" => Key.Tab,
            "Enter" => Key.Enter,
            "Escape" => Key.Escape,
            "Backspace" => Key.Backspace,
            "Delete" => Key.Delete,
            "Space" => Key.Space,
            "ArrowLeft" => Key.Left,
            "ArrowRight" => Key.Right,
            "ArrowUp" => Key.Up,
            "ArrowDown" => Key.Down,
            "Home" => Key.Home,
            "End" => Key.End,
            "PageUp" => Key.PageUp,
            "PageDown" => Key.PageDown,
            "Minus" => Key.Minus,
            "Equal" => Key.Equal,
            "Comma" => Key.Comma,
            "Period" => Key.Period,
            "Slash" => Key.Slash,
            _ => Key.None,
        };
    }
}
