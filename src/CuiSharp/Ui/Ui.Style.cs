using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>
/// One shadow layer of the theme: the box-shadow triple plus its alpha.
/// </summary>
/// <remarks>
/// An elevation is drawn with two of these, and only floating layers cast shadows at all — a card
/// rests on its border. The first layer is the tight contact shadow, the second the wider cast.
/// </remarks>
public readonly struct Shadow
{
    /// <summary>Vertical offset in points; positive casts downward.</summary>
    public readonly float Y;

    /// <summary>Blur radius in points.</summary>
    public readonly float Blur;

    /// <summary>Spread in points; negative pulls the shadow in at the sides.</summary>
    public readonly float Spread;

    /// <summary>Opacity, 0 to 1.</summary>
    public readonly float Alpha;

    public Shadow(float y, float blur, float spread = 0f, float alpha = 1f)
    {
        Y = y;
        Blur = blur;
        Spread = spread;
        Alpha = alpha;
    }

    public override string ToString() => $"y {Y:0.#} blur {Blur:0.#} spread {Spread:0.#} alpha {Alpha:0.##}";
}

/// <summary>
/// CUI's live design tokens, as <see cref="Ui.Style"/> hands them over: the radii and heights every
/// control and surface is drawn with, the text and heading scale, the motion multiplier and the
/// palette itself.
/// </summary>
/// <remarks>
/// <para>
/// This is a view over the theme CUI is drawing with, not a copy of it. Take it into a local — the
/// local is a pointer, so nothing is copied and nothing is allocated — and the assignments land in
/// CUI's own struct, which is why they apply from the next frame on and need no draw call after them.
/// </para>
/// <para>
/// A theme or accent switch (<see cref="Ui.Theme"/>, <see cref="Ui.Accent"/>,
/// <see cref="Ui.ToggleTheme"/>) rebuilds and animates the palette. The radii, the motion multiplier
/// and the font scale are explicitly kept across it; the control heights and the text and heading
/// sizes are kept as well, because CUI animates colour alone. CUI's own defaults are one
/// <c>ui.Theme</c> switch away only for the palette — an app that wants the stock radii and sizes back
/// assigns the values it captured at startup.
/// </para>
/// <para>
/// Spacing is deliberately absent: padding and gap belong to a container's <c>Box</c>, not to the
/// theme, so an app with one rhythm declares its own <c>static readonly Box</c> presets instead.
/// </para>
/// </remarks>
/// <example><code>
/// var style = ui.Style;
/// style.RadiusControl = 6;      // sharper buttons and inputs
/// style.ControlMd = 32;         // denser controls
/// style.TextBody = 14;
/// style.FontScale = 1.1f;       // every size in the app, at once
/// style[Token.Accent] = Color.Oklch(0.62f, 0.19f, 145);
/// style.SetHeading(1, 32);
/// </code></example>
public unsafe ref struct ThemeStyle
{
    private readonly NativeTheme* _theme;

    internal ThemeStyle(NativeTheme* theme) => _theme = theme;

    /// <summary>The corner radius of buttons, inputs and other controls, in points. Default 10.</summary>
    /// <remarks>
    /// CUI clamps it to half the control's height, so a value at or above that is a pill whatever the
    /// control's size; below it the corners stay concentric with a 14-point panel.
    /// </remarks>
    public float RadiusControl
    {
        readonly get => _theme->RadiusControl;
        set => _theme->RadiusControl = value;
    }

    /// <summary>The corner radius of menus, popovers and nested panels, in points. Default 14.</summary>
    /// <remarks>
    /// A row inside a panel is derived from this rather than set: an item sits six points inside its
    /// panel, so the two corners stay concentric at any radius. Raising it raises every menu item with it.
    /// </remarks>
    public float RadiusPanel
    {
        readonly get => _theme->RadiusPanel;
        set => _theme->RadiusPanel = value;
    }

    /// <summary>The corner radius of cards, dialogs and sheets, in points. Default 18.</summary>
    public float RadiusSurface
    {
        readonly get => _theme->RadiusSurface;
        set => _theme->RadiusSurface = value;
    }

    /// <summary>The height of a <see cref="ControlSize.Sm"/> control, in points. Default 30.</summary>
    public float ControlSm
    {
        readonly get => _theme->ControlSm;
        set => _theme->ControlSm = value;
    }

    /// <summary>The height of a default control, in points. Default 36.</summary>
    public float ControlMd
    {
        readonly get => _theme->ControlMd;
        set => _theme->ControlMd = value;
    }

    /// <summary>The height of a <see cref="ControlSize.Lg"/> control, in points. Default 44.</summary>
    public float ControlLg
    {
        readonly get => _theme->ControlLg;
        set => _theme->ControlLg = value;
    }

    /// <summary>The smallest text size, for captions and dense metadata, in points. Default 11.</summary>
    public float TextXs
    {
        readonly get => _theme->TextXs;
        set => _theme->TextXs = value;
    }

    /// <summary>The secondary text size, for subtext and labels, in points. Default 12.</summary>
    public float TextSm
    {
        readonly get => _theme->TextSm;
        set => _theme->TextSm = value;
    }

    /// <summary>The body text size, in points. Default 13.</summary>
    public float TextBody
    {
        readonly get => _theme->TextBody;
        set => _theme->TextBody = value;
    }

    /// <summary>The large text size, in points. Default 15.</summary>
    public float TextLg
    {
        readonly get => _theme->TextLg;
        set => _theme->TextLg = value;
    }

    /// <summary>
    /// Every text size multiplied at once — the dial behind a user's "text size" preference. Default 1.
    /// </summary>
    /// <remarks>
    /// CUI multiplies the size it draws with, so this reaches widget text and a custom canvas alike
    /// without a size changing anywhere in the app, and layout follows because a text node asks for
    /// its line height. Two things it does not reach: a rect the app computed from a hard-coded
    /// number, and <see cref="Ui.MeasureText(UiText, float, Font)"/>, which measures the size it is
    /// given; multiply the size by <see cref="FontScale"/> yourself when the two have to agree.
    /// </remarks>
    public float FontScale
    {
        readonly get => _theme->FontScale;
        set => _theme->FontScale = value;
    }

    /// <summary>
    /// How much the theme animates: 1 is normal, 0 is reduced motion, above 1 is slow motion.
    /// </summary>
    /// <remarks>
    /// For a reduced-motion preference use <see cref="Ui.SetReducedMotion(bool)"/>, which sets this
    /// same field and the flag CUI checks against it. This is the dial for watching a transition
    /// slowly while designing one.
    /// </remarks>
    public float Motion
    {
        readonly get => _theme->Motion;
        set => _theme->Motion = value;
    }

    /// <summary>
    /// One of the palette's semantic colours. Widgets draw from the same storage, so assigning a
    /// token repaints every use of it on the next frame.
    /// </summary>
    /// <remarks>
    /// Set a colour here rather than passing <see cref="Color.Rgb(uint)"/> into a widget: a token is
    /// what keeps light and dark working, and this is the way to make a token the brand's.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="token"/> is <see cref="Token.None"/> or <see cref="Token.Count"/>, which are
    /// not colours.
    /// </exception>
    public Color this[Token token]
    {
        readonly get
        {
            Check(token);
            return UiMarshal.FromNative(Colors[(int)token]);
        }
        set
        {
            Check(token);
            Colors[(int)token] = UiMarshal.ToNative(value);
        }
    }

    /// <summary>The size of a heading level, 1 to 4, in points. Defaults 36, 28, 22 and 17.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is not 1 to 4.</exception>
    public readonly float GetHeading(int level)
    {
        Level(level);
        return _theme->Heading[level - 1];
    }

    /// <summary>Sets one heading level's size in points. See <see cref="GetHeading"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is not 1 to 4.</exception>
    public void SetHeading(int level, float points)
    {
        Level(level);
        _theme->Heading[level - 1] = points;
    }

    /// <summary>
    /// The colour <see cref="Tone.Info"/> is drawn with — the theme's <c>tone_sky</c>. Default is the
    /// light/dark sky blue.
    /// </summary>
    /// <remarks>
    /// A tone that is not one of these four is a token, and the token is what a widget draws with:
    /// set <see cref="Tone.Success"/> through <c>this[Token.Success]</c>, <see cref="Tone.Warning"/>
    /// through <c>this[Token.Warning]</c>, <see cref="Tone.Danger"/> through <c>this[Token.Danger]</c>
    /// and <see cref="Tone.Accent"/> through <c>this[Token.Accent]</c>.
    /// </remarks>
    public Color ToneInfo
    {
        readonly get => UiMarshal.FromNative(_theme->ToneSky);
        set => _theme->ToneSky = UiMarshal.ToNative(value);
    }

    /// <summary>The colour <see cref="Tone.Teal"/> is drawn with — the theme's <c>tone_teal</c>.</summary>
    public Color ToneTeal
    {
        readonly get => UiMarshal.FromNative(_theme->ToneTeal);
        set => _theme->ToneTeal = UiMarshal.ToNative(value);
    }

    /// <summary>The colour <see cref="Tone.Violet"/> is drawn with — the theme's <c>tone_violet</c>.</summary>
    public Color ToneViolet
    {
        readonly get => UiMarshal.FromNative(_theme->ToneViolet);
        set => _theme->ToneViolet = UiMarshal.ToNative(value);
    }

    /// <summary>
    /// The colour <see cref="Tone.Rose"/> is drawn with — the theme's <c>tone_orchid</c>, which is the
    /// field that name resolves to upstream.
    /// </summary>
    public Color ToneRose
    {
        readonly get => UiMarshal.FromNative(_theme->ToneOrchid);
        set => _theme->ToneOrchid = UiMarshal.ToNative(value);
    }

    /// <summary>
    /// One of the two shadow layers an elevation is drawn with, where <paramref name="layer"/> is 0
    /// (the contact shadow) or 1 (the cast).
    /// </summary>
    /// <remarks>
    /// <see cref="Elevation.Resting"/> is a hairline a card sits on, <see cref="Elevation.Raised"/> a
    /// lifted panel and <see cref="Elevation.Floating"/> a menu, popover or dialog. Those are the only
    /// three that cast anything; <see cref="Elevation.None"/> has no shadow to read or write.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="elevation"/> is <see cref="Elevation.None"/>; the theme has no shadow for it.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="layer"/> is not 0 or 1.</exception>
    public Shadow GetShadow(Elevation elevation, int layer = 0)
    {
        NativeShadow* s = Layer(elevation, layer);
        return new Shadow(s->Y, s->Blur, s->Spread, s->Alpha);
    }

    /// <summary>Sets one of the two shadow layers an elevation is drawn with. See <see cref="GetShadow"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="elevation"/> is <see cref="Elevation.None"/>; the theme has no shadow for it.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="layer"/> is not 0 or 1.</exception>
    public void SetShadow(Elevation elevation, int layer, Shadow shadow)
    {
        NativeShadow* s = Layer(elevation, layer);
        s->Y = shadow.Y;
        s->Blur = shadow.Blur;
        s->Spread = shadow.Spread;
        s->Alpha = shadow.Alpha;
    }

    private readonly NativeColor* Colors => (NativeColor*)&_theme->Colors;

    private static void Level(int level)
    {
        if ((uint)(level - 1) >= 4)
            throw new ArgumentOutOfRangeException(nameof(level), level, "a heading level is 1 to 4");
    }

    private static void Check(Token token)
    {
        if (token == Token.None || (uint)token >= (uint)Token.Count)
            throw new ArgumentOutOfRangeException(nameof(token), token, "not a theme colour");
    }

    private NativeShadow* Layer(Elevation elevation, int layer)
    {
        if ((uint)layer >= 2)
            throw new ArgumentOutOfRangeException(nameof(layer), layer, "a theme elevation has two shadow layers");

        NativeShadow* first = elevation switch
        {
            Elevation.Resting => (NativeShadow*)&_theme->ShadowResting,
            Elevation.Raised => (NativeShadow*)&_theme->ShadowRaised,
            Elevation.Floating => (NativeShadow*)&_theme->ShadowFloating,
            _ => throw new ArgumentOutOfRangeException(
                nameof(elevation), elevation, "only Resting, Raised and Floating cast a shadow"),
        };

        return first + layer;
    }
}
