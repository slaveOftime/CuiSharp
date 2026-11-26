using CuiSharp;

namespace CuiSharp.Tests;

/// <summary>
/// The typed image API: pixel upload, file decode, release. The checks are
/// observable — a red texture has to put red on the framebuffer, and a bad
/// span or a missing file has to fail before native sees it.
/// </summary>
public class ImageApiTests
{
    [Fact]
    public void ARgbTextureDrawsItsPixels()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(120, 120);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];

        var rgba = new byte[16 * 16 * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = 230; // red
            rgba[i + 3] = 255;
        }

        ulong tex = 0;
        surface.Draw(ui =>
        {
            tex = ui.CreateImage(16, 16, rgba);
            ui.Image(tex, new ImageOpts { Width = Size.Px(64), Height = Size.Px(64), Radius = 0 });
        }, pixels);
        Assert.Empty(surface.Faults);
        Assert.NotEqual(0ul, tex);

        // Some pixel must carry the texture's red: one strong channel, two weak ones.
        // Whether the framebuffer reads R first or B first is the backend's business.
        bool red = false;
        for (int i = 0; i + 2 < pixels.Length; i += 4)
        {
            int a = pixels[i], b = pixels[i + 1], c = pixels[i + 2];
            red |= (a > 180 && b < 90 && c < 90) || (c > 180 && a < 90 && b < 90);
        }
        Assert.True(red, "the uploaded texture never reached the framebuffer");

        surface.Draw(ui => ui.DestroyImage(tex), pixels);
        Assert.Empty(surface.Faults);
    }

    [Fact]
    public void ASpanShorterThanThePixelsIsRejectedBeforeTheCall()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(64, 64);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];

        // Native reads w*h*4 bytes; the wrapper refuses a short span rather than
        // letting it walk past the buffer.
        Assert.Throws<ArgumentException>(() => surface.Draw(
            ui => ui.CreateImage(16, 16, new byte[16]), pixels));
    }

    [Fact]
    public void AMissingFileReportsFailureInsteadOfATexture()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(64, 64);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];

        surface.Draw(ui =>
        {
            bool ok = ui.LoadImage("does-not-exist.png", out ulong tex, out int w, out int h);
            Assert.False(ok);
            Assert.Equal(0ul, tex);
            Assert.Equal(0, w);
            Assert.Equal(0, h);
            ui.DestroyImage(0); // releasing nothing must be safe
        }, pixels);
        Assert.Empty(surface.Faults);
    }

    [Fact]
    public void AnUpdatedRectangleReplacesThosePixelsOnTheSameTexture()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(120, 120);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];

        var rgba = new byte[16 * 16 * 4];
        for (int i = 0; i < rgba.Length; i += 4) { rgba[i] = 230; rgba[i + 3] = 255; } // red
        ulong tex = 0;
        surface.Draw(ui =>
        {
            tex = ui.CreateImage(16, 16, rgba);
            ui.Image(tex, new ImageOpts { Width = Size.Px(64), Height = Size.Px(64) });
        }, pixels);
        Assert.NotEqual(0ul, tex);

        // Turn the left half green in place, one strided row at a time, and draw again:
        // the same texture handle must now show the update without a new texture.
        for (int row = 0; row < 16; row++)
            for (int col = 0; col < 8; col++)
            {
                int at = row * 64 + col * 4;
                rgba[at] = 0; rgba[at + 1] = 220; rgba[at + 3] = 255;
            }
        surface.Draw(ui =>
        {
            ui.UpdateImage(tex, 0, 0, 8, 16, rgba, stride: 64);
            ui.Image(tex, new ImageOpts { Width = Size.Px(64), Height = Size.Px(64) });
        }, pixels);
        Assert.Empty(surface.Faults);

        bool green = false, red = false;
        for (int i = 0; i + 2 < pixels.Length; i += 4)
        {
            // The green channel is the second byte whether the buffer reads RGBA or BGRA; red
            // is the first in one and the third in the other.
            green |= pixels[i + 1] > 180 && pixels[i] < 90 && pixels[i + 2] < 90;
            red |= (pixels[i] > 180 && pixels[i + 1] < 90 && pixels[i + 2] < 90)
                || (pixels[i + 2] > 180 && pixels[i] < 90 && pixels[i + 1] < 90);
        }
        Assert.True(green, "the updated rectangle never reached the framebuffer");
        Assert.True(red, "the update replaced the whole texture instead of its rectangle");

        surface.Draw(ui => ui.DestroyImage(tex), pixels);
        Assert.Empty(surface.Faults);
    }

    [Fact]
    public void AnUpdateShorterThanItsRowsIsRejectedBeforeTheCall()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(64, 64);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];

        // Two rows of 8 pixels need 64 packed bytes. With padding, only the last row's
        // actual pixels must fit; a stride below width * 4 cannot address the rows.
        Assert.Throws<ArgumentException>(() => surface.Draw(
            ui => ui.UpdateImage(7, 0, 0, 8, 2, new byte[63]), pixels));
        Assert.Throws<ArgumentException>(() => surface.Draw(
            ui => ui.UpdateImage(7, 0, 0, 8, 2, new byte[96], stride: 31), pixels));

        Assert.Throws<ArgumentException>(() => surface.Draw(
            ui => ui.UpdateImage(7, 0, 0, 8, 2, new byte[95], stride: 64), pixels));
        Assert.Throws<ArgumentOutOfRangeException>(() => surface.Draw(
            ui => ui.UpdateImage(7, 0, 0, 1 << 30, 1, []), pixels));
        Assert.Throws<ArgumentOutOfRangeException>(() => surface.Draw(
            ui => ui.UpdateImage(7, int.MaxValue, 0, 1, 1, new byte[4]), pixels));
    }
}
