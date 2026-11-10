using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>
/// Input for a windowless surface: the same events the platform backend feeds a real
/// window, so a test or a tool can click, type and scroll without a display.
/// </summary>
/// <remarks>
/// CUI reads input state during the frame, so deliver events and then draw the frame that
/// should react to them: <c>surface.MouseMove(x, y)</c>, <c>surface.MouseDown()</c>,
/// <c>surface.Draw(...)</c>. A click is a press and a release on two frames, exactly like
/// a real pointer.
/// </remarks>
/// <example><code>
/// using var surface = CuiSurface.Create(800, 600);
/// surface.MouseMove(120, 48);
/// surface.MouseDown();
/// surface.Draw(page.Draw, pixels);
/// surface.MouseUp();
/// surface.Draw(page.Draw, pixels);   // the button reports its click here
/// </code></example>
public sealed partial class CuiSurface
{
    /// <summary>Moves the pointer, in points.</summary>
    public void MouseMove(float x, float y) => Native.InputMouseMove(Handle, x, y);

    /// <summary>Presses a mouse button.</summary>
    public void MouseDown(MouseButton button = MouseButton.Left) => Native.InputMouseButton(Handle, button, 1);

    /// <summary>Releases a mouse button.</summary>
    public void MouseUp(MouseButton button = MouseButton.Left) => Native.InputMouseButton(Handle, button, 0);

    /// <summary>Presses and releases a button in one call, as a click through the frames that follow.</summary>
    public void Click(MouseButton button = MouseButton.Left)
    {
        MouseDown(button);
        MouseUp(button);
    }

    /// <summary>
    /// The pointer left the window: hover state clears and no tooltip stays behind. This is what a
    /// screenshot wants, so the picture is the UI and not the mouse.
    /// </summary>
    public void MouseLeave() => Native.InputMouseLeave(Handle);

    /// <summary>Scrolls; positive <paramref name="dy"/> scrolls down.</summary>
    public void Scroll(float dy, float dx = 0) => Native.InputScroll(Handle, dx, dy);

    /// <summary>Presses a key, with the modifiers held at the same time.</summary>
    public void KeyDown(Key key, Mods mods = 0) => Native.InputKey(Handle, key, 1, (int)mods);

    /// <summary>Releases a key.</summary>
    public void KeyUp(Key key, Mods mods = 0) => Native.InputKey(Handle, key, 0, (int)mods);

    /// <summary>Types UTF-8 text, as the keyboard layout would deliver it.</summary>
    public void Type(UiText text) => Native.InputText(Handle, Utf8.Get(text));

    /// <summary>Reports the window gaining or losing focus.</summary>
    public void Focus(bool focused) => Native.InputFocus(Handle, (byte)(focused ? 1 : 0));

    /// <summary>Reports files dragged over the surface at a position, in points.</summary>
    public void FileHover(float x, float y) => Native.InputFileHover(Handle, 1, x, y);

    /// <summary>Reports that the drag left the surface.</summary>
    public void FileHoverEnd() => Native.InputFileHover(Handle, 0, 0, 0);

    /// <summary>Reports files dropped onto the surface at a position, in points.</summary>
    public void FileDrop(ReadOnlySpan<string> paths, float x, float y) =>
        Native.InputFileDrop(Handle, UiMarshal.Strings(paths), paths.Length, x, y);

    /// <summary>Number of files the surface is tracking as dropped.</summary>
    public int DroppedFileCount => Native.DroppedFileCount(Handle);

    /// <summary>The path of a dropped file, or null when the index is out of range.</summary>
    public string? DroppedFile(int index)
    {
        var path = Native.DroppedFile(Handle, index);
        return path == 0 ? null : System.Runtime.InteropServices.Marshal.PtrToStringUTF8(path);
    }

    /// <summary>True while files are being dragged over the surface.</summary>
    public bool FileDragActive => Native.FileDragActive(Handle) != 0;
}
