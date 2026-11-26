using CuiSharp.Gallery;
using CuiSharp.Testing;

namespace CuiSharp.Tests;

public class GalleryPageTests
{
    [Theory]
    [InlineData(420)]
    [InlineData(1240)]
    public void TheTitleBarOpensTheCommandPaletteAboveThePage(int width)
    {
        using var gallery = new GalleryApp { Page = 1 };
        using var app = TestApp.Start(gallery.Frame, width, 820);
        var palette = app.Find("IconButton", "Command palette");
        Assert.False(app.TryFind("IconButton \"Close window\"", out _));
        Assert.True(palette.Rect.Y + palette.Rect.H <= app.Find("Scroll", "main").Rect.Y);

        palette.Click();
        app.Key(Key.Enter);

        Assert.Equal(0, gallery.Page); // The palette's first command is Go to Overview.
        Assert.Empty(app.Faults);
    }

    [Fact]
    public void TheNarrowTitleBarOpensTheComponentDrawer()
    {
        using var gallery = new GalleryApp();
        using var app = TestApp.Start(gallery.Frame, 420, 820);
        Assert.False(app.TryFind("NavItem \"Inputs\"", out _));

        var menu = app.Find("IconButton", "Components");
        Assert.True(menu.Rect.Y + menu.Rect.H <= app.Find("Scroll", "main").Rect.Y);
        menu.Click();
        app.Settle();
        app.Find("NavItem", "Inputs").Click();

        Assert.Equal(2, gallery.Page);
        Assert.Empty(app.Faults);
    }

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
        app.AssertSteadyStateAllocatesNothing();
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
