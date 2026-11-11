using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CuiSharp.Interop;

namespace CuiSharp.Tests;

/// <summary>
/// A GPU surface produces draw data and owns no pixels: the host's renderer consumes the
/// frame, and CUI reaches the host through the texture callbacks. These pin what the CPU can
/// observe about that contract, so a desktop CI leg catches a break before a browser does.
/// </summary>
public sealed unsafe class GpuSurfaceTests
{
    // The frame tests need the native library, which a checkout may not have built yet;
    // same probe LocalizationTests uses.
    private static readonly bool NativeReady = CanLoadNative();

    private static bool CanLoadNative()
    {
        try
        {
            _ = Native.KeyName(Key.A);
            return true;
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            return false;
        }
    }
    [Fact]
    public void AFrameCarriesDrawDataAndUploadsTheFontAtlas()
    {
        if (!NativeReady) return;

        var calls = (TextureCalls*)NativeMemory.AllocZeroed((nuint)sizeof(TextureCalls));
        try
        {
            using var surface = CuiSurface.CreateGpu(320, 200, Iface(calls));

            surface.BeginFrame();
            surface.Ui.Label("hello");
            surface.EndFrame();

            // The frame is built for a renderer to draw; the renderer is what the counts feed.
            Assert.True(surface.CommandCount > 0);
            Assert.NotEqual(0, surface.GetDrawData());

            // CUI uploads its glyph atlas through the interface on the first text it draws,
            // which is the reason a GPU surface cannot take a null callback.
            Assert.True(calls->Created > 0, "the font atlas should have been created");
            Assert.Equal((int)TextureFormat.R8, calls->LastCreateFormat);
            Assert.True(calls->LastCreatePixels != 0, "the atlas arrives with its pixels");
            Assert.True((long)calls->LastCreateWidth * calls->LastCreateHeight > 0);
        }
        finally
        {
            NativeMemory.Free(calls);
        }
    }

    [Fact]
    public void RedrawingTextUpdatesTheExistingAtlasInsteadOfReplacingIt()
    {
        if (!NativeReady) return;

        var calls = (TextureCalls*)NativeMemory.AllocZeroed((nuint)sizeof(TextureCalls));
        try
        {
            using var surface = CuiSurface.CreateGpu(320, 200, Iface(calls));

            surface.BeginFrame();
            surface.Ui.Label("aaa");
            surface.EndFrame();
            int afterFirstFrame = calls->Created;

            // New glyphs extend the atlas: they arrive as a partial update of the texture
            // already created, which is what the renderer's update callback has to survive.
            surface.BeginFrame();
            surface.Ui.Label("bbb");
            surface.EndFrame();

            Assert.Equal(afterFirstFrame, calls->Created);
            Assert.True(calls->Updated > 0, "new glyphs should update the atlas in place");
            Assert.True(calls->LastUpdatePixels != 0);
            Assert.True(calls->LastUpdateWidth > 0 && calls->LastUpdateHeight > 0);
            Assert.True(calls->LastUpdateStride >= calls->LastUpdateWidth);
        }
        finally
        {
            NativeMemory.Free(calls);
        }
    }

    [Fact]
    public void SoftwareOnlyMembersRefuseAGpuSurface()
    {
        if (!NativeReady) return;

        using var surface = CuiSurface.CreateGpu(64, 64, Iface(null));

        Assert.Equal(RendererKind.Gpu, surface.Kind);
        Assert.Null(surface.LastDamage);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        Assert.Throws<InvalidOperationException>(() => surface.Render(pixels));
        Assert.Throws<InvalidOperationException>(() => surface.Incremental = true);
    }

    [Fact]
    public void ASoftwareSurfaceStillReportsItsOwnKind()
    {
        if (!NativeReady) return;

        using var surface = CuiSurface.Create(64, 64);

        Assert.Equal(RendererKind.Software, surface.Kind);
        Assert.NotEqual(0, surface.GetDrawData());
    }

    [Fact]
    public void MissingTextureCallbacksAreRejectedRatherThanLeftToCrashLater()
    {
        // CUI calls texture_create unconditionally for the font atlas, so a surface without
        // the callbacks would fail on the first glyph instead of here.
        var partial = new NativeRendererIface
        {
            TextureCreate = (nint)(delegate* unmanaged[Cdecl]<void*, int, int, int, void*, nuint>)&CreateTexture,
            TextureUpdate = (nint)(delegate* unmanaged[Cdecl]<void*, nuint, int, int, int, int, void*, int, void>)&UpdateTexture,
        };

        Assert.Throws<ArgumentException>(() => CuiSurface.CreateGpu(64, 64, partial));
    }

    [Fact]
    public void DrawDataIsNotReadableAfterDispose()
    {
        if (!NativeReady) return;

        var surface = CuiSurface.CreateGpu(64, 64, Iface(null));
        surface.Dispose();

        // cui_get_draw_data returns &ui->draw_data, so a destroyed context yields the field's
        // offset inside a freed struct: a plausible pointer to nothing.
        Assert.Throws<ObjectDisposedException>(() => surface.GetDrawData());
    }

    [Fact]
    public void PixelSizeRoundsTheWayCuiRoundsTheFrame()
    {
        if (!NativeReady) return;

        // cui_draw_data.width_px is vw * scale + 0.5f; a surface that truncated would disagree
        // by a pixel at 1.5 and a renderer comparing the two would refuse every frame.
        using var surface = CuiSurface.Create(1001, 601, scale: 1.5f);

        Assert.Equal(1502, surface.PixelWidth);
        Assert.Equal(902, surface.PixelHeight);
    }

    // ---- the host side of the contract -------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct TextureCalls
    {
        public int Created;
        public int Updated;
        public int Destroyed;
        public int LastCreateFormat;
        public int LastCreateWidth;
        public int LastCreateHeight;
        public nint LastCreatePixels;
        public int LastUpdateWidth;
        public int LastUpdateHeight;
        public int LastUpdateStride;
        public nint LastUpdatePixels;
    }

    private static NativeRendererIface Iface(TextureCalls* calls) => new()
    {
        User = (nint)calls,
        TextureCreate = (nint)(delegate* unmanaged[Cdecl]<void*, int, int, int, void*, nuint>)&CreateTexture,
        TextureUpdate = (nint)(delegate* unmanaged[Cdecl]<void*, nuint, int, int, int, int, void*, int, void>)&UpdateTexture,
        TextureDestroy = (nint)(delegate* unmanaged[Cdecl]<void*, nuint, void>)&DestroyTexture,
    };

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nuint CreateTexture(void* user, int w, int h, int format, void* pixels)
    {
        if (user != null)
        {
            var calls = (TextureCalls*)user;
            calls->Created++;
            calls->LastCreateFormat = format;
            calls->LastCreateWidth = w;
            calls->LastCreateHeight = h;
            calls->LastCreatePixels = (nint)pixels;
            return (nuint)calls->Created;
        }

        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void UpdateTexture(
        void* user, nuint texture, int x, int y, int w, int h, void* pixels, int stride)
    {
        if (user == null) return;
        var calls = (TextureCalls*)user;
        calls->Updated++;
        calls->LastUpdateWidth = w;
        calls->LastUpdateHeight = h;
        calls->LastUpdateStride = stride;
        calls->LastUpdatePixels = (nint)pixels;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DestroyTexture(void* user, nuint texture)
    {
        if (user == null) return;
        ((TextureCalls*)user)->Destroyed++;
    }
}
