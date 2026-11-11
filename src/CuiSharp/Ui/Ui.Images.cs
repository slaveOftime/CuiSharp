// The typed image API: pixel upload, file decode and release. The mechanical
// mapping left these out because a texture handle has no managed owner; the
// widgets that take one (Image, ImageViewer, Cropper, ImageCompare,
// AvatarUpload) all read the same ulong.
using CuiSharp.Interop;

namespace CuiSharp;

public unsafe ref partial struct Ui
{
    /// <summary>
    /// Uploads RGBA8 pixels as a texture. The pixels are copied, so the span may live
    /// on the stack. Release the handle with <see cref="DestroyImage"/> when the page
    /// that shows it is done with it — <see cref="State{T}(UiText, Action)"/> with an
    /// <c>onFree</c> callback is the usual owner.
    /// </summary>
    public ulong CreateImage(int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (rgba.Length < (long)width * height * 4)
            throw new ArgumentException("rgba must hold width * height RGBA8 pixels.", nameof(rgba));

        fixed (byte* pixels = rgba)
            return Native.ImageCreate(Handle, width, height, (nint)pixels);
    }

    /// <summary>
    /// Decodes a PNG or JPEG from disk into a texture and reports its pixel size.
    /// Returns false when the file cannot be read or decoded; the texture is then 0.
    /// Release it with <see cref="DestroyImage"/>.
    /// </summary>
    public bool LoadImage(UiText path, out ulong texture, out int width, out int height)
    {
        int w = 0, h = 0;
        nuint tex = Native.ImageLoad(Handle, Utf8.Get(path), ref w, ref h);
        texture = tex;
        width = w;
        height = h;
        return tex != 0;
    }

    /// <summary>
    /// Frees a texture obtained from <see cref="CreateImage"/>, <see cref="LoadImage"/>
    /// or a widget that loaded one for you (the avatar upload). Passing 0 does
    /// nothing, so a field can be cleared and released in one step.
    /// </summary>
    public void DestroyImage(ulong texture)
    {
        if (texture != 0) Native.ImageDestroy(Handle, (nuint)texture);
    }
}
