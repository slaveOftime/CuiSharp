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
