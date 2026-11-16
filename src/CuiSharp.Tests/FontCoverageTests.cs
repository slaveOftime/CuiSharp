using System.Runtime.InteropServices;
using System.Text;
using CuiSharp.Hosting;
using CuiSharp.Testing;

namespace CuiSharp.Tests;

/// <summary>
/// A script the bundled faces do not cover renders as nothing, with no box and no warning, so the
/// only way to find out is to ask. This pins the asking — and that the answer comes from pixels,
/// because a font's advance cannot tell a missing glyph from a present one.
/// </summary>
[Collection("FontProbeStatistics")]
public class FontCoverageTests
{
    [Fact]
    public void ALatinFacePaintsNoChineseAndACjkFaceDoes()
    {
        string inter = Path.Combine(RepositoryRoot, "native", "CUI", "assets", "fonts", "Inter-Regular.ttf");
        Assert.True(File.Exists(inter), "the repository's own Latin face");

        Assert.True(FontProbe.Paints(inter, "Hello"));
        Assert.False(FontProbe.Paints(inter, "中文"), "Inter has no CJK glyphs");

        // Partial coverage must not be accepted just because the Latin half paints.
        Assert.False(FontProbe.Paints(inter, "Hello中文"));
        Assert.False(FontProbe.PaintsThemeFace("Hello中文"));
    }

    [Fact]
    public void AFallbackIsOnlyInstalledWhenAFaceDrawsTheSample()
    {
        using var app = TestApp.Start(_ => { }, 400, 300);

        // Latin is what the bundled faces already draw, so nothing on the machine needs loading.
        Assert.Equal(0, (int)app.Surface.Ui.UseSystemFontFallback("Hello"));
    }

    [Fact]
    public void ASystemFallbackMakesAChineseLabelRender()
    {
        using var app = TestApp.Start(_ => { }, 400, 300);

        Font face = app.Surface.Ui.UseSystemFontFallback("中文");
        bool machineHasACjkFace = SystemFonts.Probes.Any(path => File.Exists(path) && FontProbe.Paints(path, "中文"));

        // What must hold either way: the answer is about pixels, so it agrees with the probe.
        if (machineHasACjkFace)
        {
            Assert.NotEqual(0, (int)face);
            byte[] blank = Render(app.Surface, " ");
            foreach (var rune in "中文".EnumerateRunes())
                Assert.True(HasInk(Render(app.Surface, rune.ToString()), blank));
        }
        else Assert.Equal(0, (int)face);
    }

    [Fact]
    public void AFaceThatDoesNotLoadPaintsNothingRatherThanTheThemeFace()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".ttf");

        // A failed load used to fall through to the bundled face, whose pixels made the probe
        // report that a file which is not there could draw Latin.
        Assert.False(FontProbe.Paints(missing, "Hello"));
    }

    [Fact]
    public void AnExistingFileThatIsNotAFontCannotBecomeAFallback()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".ttf");
        try
        {
            File.WriteAllBytes(path, [1, 2, 3]);
            using var surface = CuiSurface.Create(240, 60);
            Assert.Equal(-1, (int)surface.Ui.LoadFont(path));
            Assert.False(FontProbe.Paints(path, "Hello"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ASystemEmojiFaceWithOutlinesPaintsAnEmojiAsText()
    {
        // Windows' Segoe UI Emoji carries outlines behind its COLR layers, so an emoji inside a
        // file name can paint even though colour emoji go through ui.Emoji. A bitmap-only face
        // (Apple Color Emoji, Noto Color Emoji) cannot answer this, which is why the set does not
        // pretend it can.
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "seguiemj.ttf");
        if (!File.Exists(path)) return;

        Assert.True(FontProbe.Paints(path, "😀"));
    }

    [Fact]
    public void ASystemFallbackSetPaintsEveryScriptThisMachineCanDraw()
    {
        // Every sample the bundled faces lack but some face on this machine has: the set owes
        // each of them a face that paints. A machine with no such face has nothing to prove.
        var drawable = new List<string>();
        foreach (string candidate in SystemFonts.Samples)
        {
            if (FontProbe.PaintsThemeFace(candidate)) continue;
            if (!SystemFonts.Probes.Any(path => File.Exists(path) && FontProbe.Paints(path, candidate))) continue;

            drawable.Add(candidate);
        }

        if (drawable.Count == 0) return;

        using var surface = CuiSurface.Create(240, 60);
        Assert.True(surface.Ui.UseSystemFontFallbacks() > 0, "the set installed nothing on a machine with a face");

        byte[] blank = Render(surface, " ");

        // Ink, not a measurement: a missing glyph still reports a .notdef advance.
        foreach (string sample in drawable)
        {
            foreach (var rune in sample.EnumerateRunes())
                Assert.True(HasInk(Render(surface, rune.ToString()), blank),
                    $"UseSystemFontFallbacks installed no face that draws {rune} in {sample}");
        }
    }

    [Fact]
    public void RepeatedDrawCallsReuseTheSetWithoutLoadingMoreFacesOrAllocating()
    {
        using var surface = CuiSurface.Create(240, 60);
        int installed = -1;
        Font singular = 0;
        Font marker;
        int changed = 0;
        string inter = Path.Combine(RepositoryRoot, "native", "CUI", "assets", "fonts", "Inter-Regular.ttf");

        void Draw()
        {
            surface.BeginFrame();
            int count = surface.Ui.UseSystemFontFallbacks();
            Font face = surface.Ui.UseSystemFontFallback("中文");
            if (installed >= 0 && (installed != count || singular != face)) changed++;
            installed = count;
            singular = face;
            surface.Ui.Label("中文");
            surface.EndFrame();
        }

        Draw();
        marker = surface.Ui.LoadFont(inter);
        Assert.True((int)marker > (int)Font.Icons);
        for (int i = 0; i < 10; i++) Draw();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) Draw();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(0, changed);
        // Native handles are sequential: even a hidden reload changes the next handle.
        Assert.Equal((int)marker + 1, (int)surface.Ui.LoadFont(inter));
    }

    [Fact]
    public void ANewContextGetsItsOwnFallbacksEvenAfterAnotherWasDisposed()
    {
        int expected;
        using (var first = CuiSurface.Create(240, 60))
        {
            string inter = Path.Combine(RepositoryRoot, "native", "CUI", "assets", "fonts", "Inter-Regular.ttf");
            Font before = first.Ui.LoadFont(inter);
            Font chinese = first.Ui.UseSystemFontFallback("中文");
            expected = first.Ui.UseSystemFontFallbacks();
            Assert.Equal(expected, first.Ui.UseSystemFontFallbacks());
            if (chinese != 0)
            {
                Assert.Equal(chinese, first.Ui.UseSystemFontFallback("中"));
                Assert.Equal(chinese, first.Ui.UseSystemFontFallback("文"));
            }
            Assert.Equal((int)before + expected + 1, (int)first.Ui.LoadFont(inter));
        }

        using var second = CuiSurface.Create(240, 60);
        Assert.Equal(expected, second.Ui.UseSystemFontFallbacks());
        byte[] blank = Render(second, " ");
        if (expected > 0)
        {
            string sample = SystemFonts.Samples.First(s => !FontProbe.PaintsThemeFace(s)
                && SystemFonts.Probes.Any(p => FontProbe.Paints(p, s)));
            foreach (var rune in sample.EnumerateRunes())
                Assert.True(HasInk(Render(second, rune.ToString()), blank));
        }
    }

    [Fact]
    public void MissingAndInvisibleSamplesAreCachedWithoutAllocatingInDraw()
    {
        using var surface = CuiSurface.Create(240, 60);
        const string missing = "\U0010FFFF";
        Assert.Equal(0, (int)surface.Ui.UseSystemFontFallback(missing));
        Assert.Equal(0, (int)surface.Ui.UseSystemFontFallback(" \t\n\u200D"));
        Assert.Equal(0, (int)surface.Ui.UseSystemFontFallback(""));
        Assert.Throws<ArgumentNullException>(() => surface.Ui.UseSystemFontFallback(null!));

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            surface.Ui.UseSystemFontFallback(missing);
            surface.Ui.UseSystemFontFallback(" \t\n\u200D");
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void FirstUseInDrawPreservesEarlierTextAndDoesNotCountProbeFrames()
    {
        using var surface = CuiSurface.Create(240, 120);
        surface.RecordWidgets = true;
        surface.BeginFrame();
        UiText saved = "keep this text across the font probe";
        surface.Ui.Label(saved);
        long frames = FrameStats.Frames;
        int arenaBytes = Utf8.FrameBytes;
        surface.Ui.UseSystemFontFallbacks();
        Assert.Equal(frames, FrameStats.Frames);
        Assert.Equal(arenaBytes, Utf8.FrameBytes);
        Assert.Equal("keep this text across the font probe", Marshal.PtrToStringUTF8(Utf8.Get(saved)));
        surface.Ui.Label("after the probe");
        surface.EndFrame();
        Assert.Equal(["keep this text across the font probe", "after the probe"],
            surface.Widgets.Select(w => w.Text).ToArray());
    }

    [Fact]
    public void ProbingDoesNotRetireCallbacksRegisteredEarlierInTheFrame()
    {
        using var surface = CuiSurface.Create(240, 60);
        surface.BeginFrame();
        string key = Guid.NewGuid().ToString("N");
        Func<int, string?> label = static _ => "kept";
        nint handle = FrameCallbacks.Rent(key, label);
        object? holder = GCHandle.FromIntPtr(handle).Target;
        Assert.NotNull(holder);
        surface.Ui.UseSystemFontFallbacks();
        // Rent again before inspecting: if a buggy probe freed the old GCHandle, dereferencing it
        // directly could crash rather than produce a useful assertion failure.
        nint after = FrameCallbacks.Rent(key, label);
        Assert.Same(holder, GCHandle.FromIntPtr(after).Target);
        surface.EndFrame();
    }

    [Fact]
    public void AFileProbeCannotBorrowLatinGlyphsFromTheBundledFaces()
    {
        string icons = Path.Combine(RepositoryRoot, "native", "CUI", "assets", "fonts", "Lucide-CUI.ttf");
        Assert.True(File.Exists(icons));
        Assert.False(FontProbe.Paints(icons, "Hello"));
    }

    private static byte[] Render(CuiSurface surface, string text)
    {
        byte[] pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];

        surface.BeginFrame();
        surface.Ui.Label(text);
        surface.EndFrame();
        surface.Render(pixels);

        return pixels;
    }

    private static bool HasInk(byte[] painted, byte[] blank)
    {
        if (painted.Length != blank.Length) return true;

        for (int i = 0; i < painted.Length; i++)
        {
            if (painted[i] != blank[i]) return true;
        }

        return false;
    }

    private static string RepositoryRoot
    {
        get
        {
            string? directory = AppContext.BaseDirectory;
            while (directory is not null && !File.Exists(Path.Combine(directory, "CuiSharp.slnx")))
                directory = Path.GetDirectoryName(directory);
            return directory ?? throw new InvalidOperationException("the repository root was not found");
        }
    }
}

// FrameStats is process-wide. No other test may draw frames while its preservation is asserted.
[CollectionDefinition("FontProbeStatistics", DisableParallelization = true)]
public sealed class FontProbeStatisticsCollection { }
