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
