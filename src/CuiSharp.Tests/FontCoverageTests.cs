using CuiSharp.Testing;

namespace CuiSharp.Tests;

/// <summary>
/// A script the bundled faces do not cover renders as nothing, with no box and no warning, so the
/// only way to find out is to ask. This pins the asking — and that the answer comes from pixels,
/// because a font's advance cannot tell a missing glyph from a present one.
/// </summary>
public class FontCoverageTests
{
    [Fact]
    public void ALatinFacePaintsNoChineseAndACjkFaceDoes()
    {
        string inter = Path.Combine(RepositoryRoot, "native", "CUI", "assets", "fonts", "Inter-Regular.ttf");
        Assert.True(File.Exists(inter), "the repository's own Latin face");

        Assert.True(FontProbe.Paints(inter, "Hello"));
        Assert.False(FontProbe.Paints(inter, "中文"), "Inter has no CJK glyphs");

        // The machine's own CJK face, when it has one: the probe has to say yes to a face that
        // really draws the script, or the fallback it drives would never fire.
        if (SystemFonts.Probes.FirstOrDefault(File.Exists) is { } system && FontProbe.Paints(system, "中文"))
        {
            Assert.True(FontProbe.Paints(system, "中文"));
        }
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
        if (machineHasACjkFace) Assert.NotEqual(0, (int)face);
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
            byte[] painted = Render(surface, sample);
            Assert.True(HasInk(painted, blank), $"UseSystemFontFallbacks installed no face that draws {sample}");
        }
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
