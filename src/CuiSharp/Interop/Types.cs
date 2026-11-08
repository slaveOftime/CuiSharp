using CuiSharp.Interop;

// Hand-written value types. Enums, structs, functions and icons are generated:
// see bindings/api.json and tools/bindgen.cs.
namespace CuiSharp;

/// <summary>cui_color. Either four channels or a semantic token that follows the theme.</summary>
public readonly struct Color
{
    public readonly byte R, G, B, A;

    public Color(byte r, byte g, byte b, byte a = 255) { R = r; G = g; B = b; A = a; }

    /// <summary>CUI_RGB(0xRRGGBB).</summary>
    public static Color Rgb(uint hex) =>
        new((byte)((hex >> 16) & 0xff), (byte)((hex >> 8) & 0xff), (byte)(hex & 0xff));

    /// <summary>CUI_RGBA(0xRRGGBBAA).</summary>
    public static Color Rgba(uint hex) =>
        new((byte)((hex >> 24) & 0xff), (byte)((hex >> 16) & 0xff), (byte)((hex >> 8) & 0xff), (byte)(hex & 0xff));

    public static readonly Color Transparent = new(0, 0, 0, 0);

    public static Color FromToken(Token t) => new((byte)t, 0xC5, 0x1D, 0);

    public static Color Background => FromToken(Token.Background);
    public static Color Surface => FromToken(Token.Surface);
    public static Color SurfaceRaised => FromToken(Token.SurfaceRaised);
    public static Color SurfaceMuted => FromToken(Token.SurfaceMuted);
    public static Color Foreground => FromToken(Token.Foreground);
    public static Color TextSecondary => FromToken(Token.TextSecondary);
    public static Color TextMuted => FromToken(Token.TextMuted);
    public static Color Border => FromToken(Token.Border);
    public static Color BorderStrong => FromToken(Token.BorderStrong);
    public static Color Accent => FromToken(Token.Accent);
    public static Color AccentForeground => FromToken(Token.AccentForeground);
    public static Color Success => FromToken(Token.Success);
    public static Color Warning => FromToken(Token.Warning);
    public static Color Danger => FromToken(Token.Danger);
    public static Color Info => FromToken(Token.Info);

    /// <summary>
    /// The theme color a semantic tone stands for, for the primitives that take a
    /// <see cref="Color"/> rather than a <see cref="Tone"/> (charts, sparklines,
    /// custom canvas drawing). <see cref="Tone.Accent"/> and the extra palette tones
    /// all resolve to the accent color.
    /// </summary>
    public static Color For(Tone tone) => tone switch
    {
        Tone.Success => Success,
        Tone.Warning => Warning,
        Tone.Danger => Danger,
        Tone.Info => Info,
        Tone.Neutral => TextSecondary,
        _ => Accent,
    };

    /// <summary>CUI_COLOR_SERIES(n): cycles through the six chart colors.</summary>
    public static Color Series(int n) => FromToken(Token.Series1 + n % 6);

    /// <summary>
    /// A color from an OKLCH triple: perceptual lightness 0..1, chroma, hue in degrees.
    /// </summary>
    /// <remarks>
    /// The space the built-in themes are built in, so a custom color of the same lightness
    /// as the theme reads as a member of it instead of a bolt-on.
    /// </remarks>
    public static Color Oklch(float lightness, float chroma, float hueDegrees, float alpha = 1f) =>
        UiMarshal.FromNative(Native.Oklch(lightness, chroma, hueDegrees, alpha));

    /// <summary>Blends two colors: <paramref name="t"/> 0 is <paramref name="a"/>, 1 is <paramref name="b"/>.</summary>
    public static Color Mix(Color a, Color b, float t) =>
        UiMarshal.FromNative(Native.Mix(UiMarshal.ToNative(a), UiMarshal.ToNative(b), t));

    /// <summary>Multiplies the color's alpha, for a disabled or washed-out variant.</summary>
    public Color WithAlpha(float multiplier) =>
        UiMarshal.FromNative(Native.Alpha(UiMarshal.ToNative(this), multiplier));

    public bool IsToken => G == 0xC5 && B == 0x1D;
}

/// <summary>cui_size: FIT, FILL, a fixed size in points, or a percentage of the parent.</summary>
public readonly struct Size
{
    internal readonly SizeKind Kind;
    internal readonly float Value;

    private Size(SizeKind kind, float value) { Kind = kind; Value = value; }

    public static readonly Size Fit = new(SizeKind.Fit, 0);
    public static readonly Size Fill = new(SizeKind.Grow, 1);
    public static Size Grow(float w) => new(SizeKind.Grow, w);
    public static Size Px(float v) => new(SizeKind.Fixed, v);
    public static Size Percent(float p) => new(SizeKind.Percent, p);

    /// <summary>
    /// A bare number is a fixed size in points, so <c>Width = 240</c> reads like
    /// every other layout value. Use <see cref="Fill"/>, <see cref="Fit"/> or
    /// <see cref="Percent"/> when the size is relative.
    /// </summary>
    public static implicit operator Size(float points) => Px(points);

    internal static Size FromParts(SizeKind kind, float value) => new(kind, value);
}



/// <summary>cui_renderer_kind: OpenGL with a software fallback, or forced.</summary>

/// <summary>cui_date.</summary>
public struct Date
{
    public int Year, Month, Day;
    public Date(int y, int m, int d) { Year = y; Month = m; Day = d; }
    public readonly override string ToString() => $"{Year:D4}-{Month:D2}-{Day:D2}";

    /// <summary>Days since 1970-01-01, which is what date arithmetic in CUI uses.</summary>
    public readonly int DayNumber => Native.DateToDays(UiMarshal.ToNative(this));

    /// <summary>The date that many days after 1970-01-01.</summary>
    public static Date FromDayNumber(int days) =>
        UiMarshal.FromNative(Native.DateFromDays(days));

    /// <summary>A copy moved by <paramref name="days"/>, positive or negative.</summary>
    public readonly Date AddDays(int days) => FromDayNumber(DayNumber + days);

    /// <summary>Days from this date to <paramref name="other"/>; negative when it is earlier.</summary>
    public readonly int DaysUntil(Date other) => other.DayNumber - DayNumber;
}

/// <summary>A rectangle in points, top-left origin.</summary>
public readonly struct RectF
{
    public readonly float X, Y, W, H;
    public RectF(float x, float y, float w, float h) { X = x; Y = y; W = w; H = h; }
    public override string ToString() => $"({X}, {Y}) {W}x{H}";
}
