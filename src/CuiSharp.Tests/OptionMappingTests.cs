using System.Text;
using CuiSharp.Interop;

namespace CuiSharp.Tests;

/// <summary>
/// The managed option structs convert into the native ones. These tests check
/// the conversion, not the layout (that is GeneratedAbiTests).
/// </summary>
public class OptionMappingTests
{
    [Fact]
    public void ColorTokensUseCuisMarkerEncoding()
    {
        var accent = Color.Accent;

        Assert.True(accent.IsToken);
        Assert.Equal((byte)Token.Accent, accent.R);
        Assert.Equal(0xC5, accent.G);
        Assert.Equal(0x1D, accent.B);
        Assert.Equal(0, accent.A);
    }

    [Fact]
    public void LiteralColorsAreNotMistakenForTokens()
    {
        var violet = Color.Rgb(0x7747ff);

        Assert.False(violet.IsToken);
        Assert.Equal((byte)0x77, violet.R);
        Assert.Equal((byte)0x47, violet.G);
        Assert.Equal((byte)0xFF, violet.B);
        Assert.Equal(255, violet.A);
    }

    [Fact]
    public void SeriesColorsCycleThroughSix()
    {
        Assert.Equal((byte)Token.Series1, Color.Series(0).R);
        Assert.Equal((byte)Token.Series3, Color.Series(2).R);
        // Six colors, so index 6 wraps back to the first.
        Assert.Equal((byte)Token.Series1, Color.Series(6).R);
        Assert.Equal((byte)Token.Series2, Color.Series(7).R);
    }

    [Fact]
    public void BoolFieldsConvertToOneByteFlags()
    {
        var native = new Box
        {
            Wrap = true,
            ScrollX = true,
            ScrollY = false,
            Clip = true,
        }.ToNative();

        Assert.Equal(1, native.Wrap);
        Assert.Equal(1, native.ScrollX);
        Assert.Equal(0, native.ScrollY);
        Assert.Equal(1, native.Clip);
    }

    [Fact]
    public void DefaultOptionsCarryNoFlags()
    {
        var native = new ButtonOpts().ToNative();

        Assert.Equal(0, native.Disabled);
        Assert.Equal(0, native.Loading);
        Assert.Equal(0, native.Selected);
        Assert.Equal(ButtonVariant.Default, native.Variant);
        Assert.Equal(0, native.Icon);
    }

    [Fact]
    public void SizeFactoriesMapToKinds()
    {
        Assert.Equal(SizeKind.Grow, new Box { Width = Size.Fill }.ToNative().Width.Kind);
        var px = new Box { Width = Size.Px(240) }.ToNative();
        Assert.Equal(SizeKind.Fixed, px.Width.Kind);
        Assert.Equal(240f, px.Width.Value);
        Assert.Equal(SizeKind.Percent, new Box { Width = Size.Percent(70) }.ToNative().Width.Kind);

        // The struct default is FIT, which is what a zeroed C struct means.
        var empty = new Box().ToNative();
        Assert.Equal(SizeKind.Fit, empty.Width.Kind);
        Assert.Equal(0f, empty.Width.Value);
    }

    [Fact]
    public void ChartOptionsKeepLabelCountInSync()
    {
        string[] labels = ["Jan", "Feb", "Mar"];
        var native = new ChartOpts { Labels = labels, Height = 200 }.ToNative();

        Assert.Equal(3, native.LabelCount);
        Assert.Equal(200f, native.Height);
    }
}
