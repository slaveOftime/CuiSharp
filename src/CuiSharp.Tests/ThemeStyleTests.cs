using CuiSharp.Hosting;
using CuiSharp.Testing;

namespace CuiSharp.Tests;

/// <summary>
/// <c>ui.Style</c>: CUI's design tokens, read and assigned. Every assertion is on something the frame
/// did — a control's measured size, the pixels a token painted — because a token that only round-trips
/// through its own getter proves nothing.
/// </summary>
public class ThemeStyleTests
{
    /// <summary>A plain copy of the tokens, so anything that cannot hold a <c>ref struct</c> can hold this.</summary>
    private readonly record struct Snapshot(
        float RadiusControl, float RadiusPanel, float RadiusSurface,
        float ControlSm, float ControlMd, float ControlLg,
        float TextXs, float TextSm, float TextBody, float TextLg,
        float Heading1, float Heading2, float Heading3, float Heading4,
        float FontScale, float Motion)
    {
        public static Snapshot Of(ThemeStyle style) => new(
            style.RadiusControl, style.RadiusPanel, style.RadiusSurface,
            style.ControlSm, style.ControlMd, style.ControlLg,
            style.TextXs, style.TextSm, style.TextBody, style.TextLg,
            style.GetHeading(1), style.GetHeading(2), style.GetHeading(3), style.GetHeading(4),
            style.FontScale, style.Motion);
    }

    /// <summary>Assigns whichever tokens the test named, then draws controls whose size follows them.</summary>
    private sealed class Tokens : Component
    {
        public float ControlMd;
        public float TextBody;
        public float FontScale;

        protected override void Draw(Ui ui)
        {
            ThemeStyle style = ui.Style;

            // Zero means "leave CUI's default", so a test names only the token it is about.
            if (ControlMd > 0) style.ControlMd = ControlMd;
            if (TextBody > 0) style.TextBody = TextBody;
            if (FontScale > 0) style.FontScale = FontScale;

            ui.Button("Save");
            ui.Button("Small", new ButtonOpts { Size = ControlSize.Sm });
        }
    }

    [Fact]
    public void TheDefaultsAreTheOnesCuiShips()
    {
        if (!OperatingSystem.IsWindows()) return;

        Snapshot tokens = default;
        using var surface = CuiSurface.Create(600, 400);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];

        surface.Draw(ui => tokens = Snapshot.Of(ui.Style), pixels);
        Assert.Empty(surface.Faults);

        // The numbers documented on each member, pinned so a submodule bump that retunes the look
        // fails here instead of silently restyling every app that did not ask for it.
        Assert.Equal(10f, tokens.RadiusControl);
        Assert.Equal(14f, tokens.RadiusPanel);
        Assert.Equal(18f, tokens.RadiusSurface);
        Assert.Equal(30f, tokens.ControlSm);
        Assert.Equal(36f, tokens.ControlMd);
        Assert.Equal(44f, tokens.ControlLg);
        Assert.Equal(11f, tokens.TextXs);
        Assert.Equal(12f, tokens.TextSm);
        Assert.Equal(13f, tokens.TextBody);
        Assert.Equal(15f, tokens.TextLg);
        Assert.Equal(36f, tokens.Heading1);
        Assert.Equal(28f, tokens.Heading2);
        Assert.Equal(22f, tokens.Heading3);
        Assert.Equal(17f, tokens.Heading4);
        Assert.Equal(1f, tokens.FontScale);
        Assert.Equal(1f, tokens.Motion);
    }

    [Fact]
    public void AControlHeightTokenSizesThatControlAndNoOther()
    {
        if (!OperatingSystem.IsWindows()) return;

        // The default first, so the change below is a change rather than a coincidence.
        using (var app = TestApp.Start(new Tokens(), 600, 400))
        {
            Assert.Equal(36f, app.FindButton("Save").Rect.H);
            Assert.Equal(30f, app.FindButton("Small").Rect.H);
        }

        using (var app = TestApp.Start(new Tokens { ControlMd = 60 }, 600, 400))
        {
            Assert.Equal(60f, app.FindButton("Save").Rect.H);
            Assert.Equal(30f, app.FindButton("Small").Rect.H);
        }
    }

    [Fact]
    public void ABodyTextTokenResizesTheTextAControlDraws()
    {
        if (!OperatingSystem.IsWindows()) return;

        // A button is its label plus its padding, so its width is the body text's width.
        float baseline;
        using (var app = TestApp.Start(new Tokens(), 600, 400))
        {
            baseline = app.FindButton("Save").Rect.W;
        }

        using (var app = TestApp.Start(new Tokens { TextBody = 26 }, 600, 400))
        {
            float wider = app.FindButton("Save").Rect.W;

            // A button is its label plus a fixed padding, so doubling the text widens the box by well
            // under double; it still has to widen by a lot, which it cannot do from anything but the token.
            Assert.True(wider > baseline * 1.4f, $"a 26-point body drew {wider} wide, the 13-point one {baseline}");
        }
    }

    [Fact]
    public void AFontScaleReachesTextWithoutASizeChangingInTheApp()
    {
        if (!OperatingSystem.IsWindows()) return;

        float baseline;
        using (var app = TestApp.Start(new Tokens(), 600, 400))
        {
            baseline = app.FindButton("Save").Rect.W;
        }

        using (var app = TestApp.Start(new Tokens { FontScale = 2f }, 600, 400))
        {
            float scaled = app.FindButton("Save").Rect.W;

            // Same fixed padding as above: the box grows by most of the text's growth, not by all of it.
            // The app assigned no size, so a wider box can only have come through the multiplier.
            Assert.True(scaled > baseline * 1.4f, $"2x scale drew {scaled} wide, 1x drew {baseline}");
        }
    }

    [Fact]
    public void AssigningATokenRepaintsEveryUseOfIt()
    {
        if (!OperatingSystem.IsWindows()) return;

        // A brand accent no CUI theme ships: if the frame paints it, the token wrote through.
        var brand = new Color(0x12, 0x34, 0x56);

        bool matched = true;
        using var surface = CuiSurface.Create(120, 60);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];

        for (int frame = 0; frame < 2; frame++)
        {
            surface.Draw(ui =>
            {
                ThemeStyle style = ui.Style;
                style[Token.Accent] = brand;

                // What the token reads back as, and what a widget resolving it gets, are one colour.
                matched = style[Token.Accent] == brand && ui.TokenColor(Token.Accent) == brand;

                // The whole surface in the accent, handed over as the token rather than as channels,
                // so the assignment has to reach the rasterizer's own token resolution too.
                using (ui.Box(new() { Width = Size.Fill, Height = Size.Fill, Bg = Color.Accent }))
                    ui.Text(" ");
            }, pixels, PixelFormat.Rgba);
        }

        Assert.Empty(surface.Faults);
        Assert.True(matched, "the token did not read back as the colour assigned to it");

        bool painted = false;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] == brand.R && pixels[i + 1] == brand.G && pixels[i + 2] == brand.B) painted = true;
        }

        Assert.True(painted, "no pixel carried the accent token the frame assigned");
    }

    [Fact]
    public void AToneColourReachesTheWidgetThatDrawsWithIt()
    {
        if (!OperatingSystem.IsWindows()) return;

        // A solid badge fills its background with the tone it was given, so the palette field is the
        // only thing between the assignment and the pixels.
        var brand = new Color(0x00, 0x8b, 0x8b);

        using var surface = CuiSurface.Create(200, 80);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];

        for (int frame = 0; frame < 2; frame++)
        {
            surface.Draw(ui =>
            {
                ThemeStyle style = ui.Style;
                style.ToneTeal = brand;
                ui.Badge("Teal", new BadgeOpts { Tone = Tone.Teal, Solid = true });
            }, pixels, PixelFormat.Rgba);
        }

        Assert.Empty(surface.Faults);

        bool painted = false;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] == brand.R && pixels[i + 1] == brand.G && pixels[i + 2] == brand.B) painted = true;
        }

        Assert.True(painted, "the badge did not fill with the teal the frame assigned");
    }

    [Fact]
    public void AnOutOfRangeTokenHeadingOrElevationIsRefused()
    {
        if (!OperatingSystem.IsWindows()) return;

        Exception? badToken = null, badLevel = null, badElevation = null, badLayer = null;
        using var surface = CuiSurface.Create(200, 100);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];

        surface.Draw(ui =>
        {
            ThemeStyle style = ui.Style;
            badToken = Refused(style, s => s[(Token)(-1)] = Color.Rgb(0xffffff));
            badLevel = Refused(style, s => s.SetHeading(5, 10));
            badElevation = Refused(style, s => s.SetShadow(Elevation.None, 1, new Shadow(0, 1)));
            badLayer = Refused(style, s => s.SetShadow(Elevation.Floating, 2, new Shadow(0, 1)));
        }, pixels);

        // Each refusal names the parameter it refused, so the message is the fix.
        Assert.Equal("token", Assert.IsType<ArgumentOutOfRangeException>(badToken).ParamName);
        Assert.Equal("level", Assert.IsType<ArgumentOutOfRangeException>(badLevel).ParamName);
        Assert.Equal("elevation", Assert.IsType<ArgumentOutOfRangeException>(badElevation).ParamName);
        Assert.Equal("layer", Assert.IsType<ArgumentOutOfRangeException>(badLayer).ParamName);
    }

    [Fact]
    public void AThemeSwitchKeepsWhatTheAppOwns()
    {
        if (!OperatingSystem.IsWindows()) return;

        float radius = 0, scale = 0, control = 0;
        Theme mode = Theme.Light;

        using var surface = CuiSurface.Create(400, 300);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];

        void Draw(Ui ui)
        {
            mode = ui.Theme;
            if (mode == Theme.Light)
            {
                ThemeStyle style = ui.Style;
                style.RadiusControl = 4;
                style.FontScale = 1.25f;
                style.ControlMd = 40;
                ui.ToggleTheme();
                return;
            }

            ui.Theme = mode;   // hold still: the switch is what this test is about
            ThemeStyle now = ui.Style;
            radius = now.RadiusControl;
            scale = now.FontScale;
            control = now.ControlMd;
        }

        surface.Draw(Draw, pixels);   // light: customise, then ask for dark
        surface.Draw(Draw, pixels);   // dark: the palette is rebuilt from CUI's own values
        Assert.Equal(Theme.Dark, mode);

        // Upstream carries these across a retarget on purpose (cui__theme_retarget).
        Assert.Equal(4f, radius);
        Assert.Equal(1.25f, scale);

        // A control height is not in that carried set, but a switch animates colour alone, so the
        // value the app assigned is still the one the frame draws with.
        Assert.Equal(40f, control);
    }

    [Fact]
    public void ReadingAndAssigningTheStyleAllocatesNothingPerFrame()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = TestApp.Start(new Tokens { ControlMd = 40, FontScale = 1.1f, TextBody = 14 }, 600, 400);
        app.Frame(4);   // let the layout and the first frame's pools settle

        // The component reads and writes every kind of member each frame; none of it may allocate.
        Assert.Equal(0, app.Surface.LastFrame.AllocatedBytes);
    }

    /// <summary>Runs one refused mutation, returning what it threw, or null when it did not.</summary>
    private static Exception? Refused(ThemeStyle style, Action<ThemeStyle> mutate)
    {
        try
        {
            mutate(style);
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }
}
