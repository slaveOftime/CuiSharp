using CuiSharp.Gallery;
using CuiSharp.Testing;

namespace CuiSharp.Tests;

public class GalleryPageTests
{
    [Theory]
    [InlineData(29, "Creative controls")]
    [InlineData(30, "Collaboration")]
    [InlineData(31, "Voice")]
    public void TheNewGalleryPagesDrawWithoutAllocating(int page, string heading)
    {
        using var gallery = new GalleryApp { Page = page };
        using var app = TestApp.Start(gallery.Frame, 1240, 820);
        Assert.True(app.Find("Heading", heading).Rect.W > 0);
        Assert.True(app.PaintedAnything());
        app.Frame(30);
        for (int i = 0; i < 10; i++)
        {
            app.Frame();
            Assert.Equal(0, app.Surface.LastFrame.AllocatedBytes);
        }
        Assert.Empty(app.Faults);
    }
}
public class PickersProbe
{
    [Fact]
    public void Probe()
    {
        using var gallery = new GalleryApp { Page = 15 };
        using var app = TestApp.Start(gallery.Frame, 1240, 820, theme: Theme.Dark);
        for (int i = 0; i < 5; i++) app.Frame();
        string early = string.Join(" | ", app.Faults.Take(5));
        for (int i = 0; i < 30; i++) app.Frame();
        var heading = app.Find("Heading", "Multi-select");
        Assert.True(app.Faults.Count == 0, $"early=[{early}] all=[{string.Join(" | ", app.Faults.Take(8))}]");
        Assert.True(heading.Rect.W > 0, "the pickers page vanished in the dark theme");
    }
}
