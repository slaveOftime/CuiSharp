namespace CuiSharp.Tests;

/// <summary>
/// The generated wrappers for record arrays and typed results have to survive a real
/// frame: a record is copied into the frame arena, and a returned result is converted
/// back. Both paths only fail at runtime, so both are exercised here.
/// </summary>
public class GeneratedRecordTests
{
    [Fact]
    public void ARecordArrayDrawsEveryItem()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(560, 320);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        VizItem[] items =
        [
            new("Search", 42, Color.Accent),
            new("Code", 22, Color.Success),
            new("Data", 18, Color.Warning),
        ];

        surface.Draw(ui =>
        {
            using (ui.Column(new() { Width = Size.Fill, Pad = 12 }))
            {
                ui.BarList("bars", items, new() { Width = Size.Fill });
                ui.SpeedDial("dial", [new SpeedAction("plus", "Add"), new SpeedAction("trash", "Delete")]);
                ui.Ticker("ticker", [new TickerItem("NVDA", "128.40", 1.2f, false)]);
            }
        }, pixels);

        Assert.Contains(pixels, b => b != 0);
        Assert.Empty(surface.Faults);
    }

    [Fact]
    public void ATypedResultIsConvertedNotReturnedAsNativeMemory()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(640, 320);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        PathBarResult result = default;

        surface.Draw(ui =>
        {
            using (ui.Column(new() { Width = Size.Fill, Pad = 12 }))
            {
                result = ui.PathBar("path", ["root", "src", "file.cs"]);
            }
        }, pixels);

        // Nothing was clicked in a frame drawn without input, and the struct came back
        // converted: -1 for "no segment", and no exception.
        Assert.Equal(-1, result.Clicked);
        Assert.False(result.Editing);
        Assert.Empty(surface.Faults);
    }
}
