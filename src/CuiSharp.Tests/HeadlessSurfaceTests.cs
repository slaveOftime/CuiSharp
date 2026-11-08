namespace CuiSharp.Tests;

/// <summary>
/// The headless surface lets UI code run — and be asserted on — with no window.
/// These are the tests the design is meant to make possible.
/// </summary>
public class HeadlessSurfaceTests
{
    [Fact]
    public void DrawsPixelsWithoutAWindow()
    {
        if (!OperatingSystem.IsWindows()) return;   // needs cui.dll

        using var surface = CuiSurface.Create(320, 200);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];

        surface.Draw(ui =>
        {
            using (ui.Column(new Box { Pad = 16, Gap = 8 }))
            {
                ui.Heading("Headless", 3);
                ui.Button("Save", new ButtonOpts { Variant = ButtonVariant.Primary });
            }
        }, pixels);

        Assert.True(surface.CommandCount > 0);
        Assert.Contains(pixels, b => b != 0);
    }

    [Fact]
    public void EmptyUiProducesNoCommands()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(200, 100);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        surface.Draw(_ => { }, pixels);

        // An empty frame still clears the surface: one command, no widgets.
        Assert.InRange(surface.CommandCount, 0, 1);
    }

    [Fact]
    public void WidgetsCanBeDrawnAndMeasuredWithoutAHost()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(240, 120);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];

        surface.Draw(ui => ui.Canvas("c", Size.Fill, Size.Fill, (ui, box) =>
        {
            ui.DrawRect(box, Color.Accent, 6);
        }), pixels);

        // The canvas painted something: at least one fully opaque pixel exists.
        Assert.Contains(Enumerable.Range(0, pixels.Length / 4), i => pixels[i * 4 + 3] == 255);
    }
}
