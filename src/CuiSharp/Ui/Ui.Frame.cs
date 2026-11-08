using System.Runtime.InteropServices;
using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>
/// What a custom control needs from the frame and the input state: the clock, the
/// pointer, the keyboard, measurement, and the disabled gate. These are the C functions
/// a hand-written widget calls from inside <see cref="Ui"/>.
/// </summary>
public unsafe ref partial struct Ui
{
    // ---- the clock ---------------------------------------------------------

    /// <summary>Seconds since the app started. Monotonic; use it for animation phases.</summary>
    public double Time => Native.Time(Handle);

    /// <summary>Seconds since the previous frame, clamped by the host.</summary>
    public float DeltaTime => Native.DeltaTime(Handle);

    /// <summary>Frames presented so far, counted by CUI.</summary>
    public ulong FrameCount => Native.FrameCount(Handle);

    /// <summary>Device pixels per logical point for the window this frame draws into.</summary>
    public float DpiScale => Native.DpiScale(Handle);

    // ---- pointer and keyboard ---------------------------------------------

    /// <summary>Pointer position in points, top-left origin.</summary>
    public (float X, float Y) MousePosition
    {
        get
        {
            var p = Native.MousePos(Handle);
            return (p.X, p.Y);
        }
    }

    /// <summary>True while a mouse button is held.</summary>
    public bool MouseDown(MouseButton button) => Native.MouseDown(Handle, button) != 0;

    /// <summary>True on the frame a mouse button went down.</summary>
    public bool MouseClicked(MouseButton button) => Native.MouseClicked(Handle, button) != 0;

    /// <summary>True while the key is held (with auto-repeat).</summary>
    public bool KeyDown(Key key) => Native.KeyDown(Handle, key) != 0;

    /// <summary>True on the frame the key went down, including repeats.</summary>
    public bool KeyPressed(Key key) => Native.KeyPressed(Handle, key) != 0;

    /// <summary>The modifier keys held right now.</summary>
    public Mods Modifiers => (Mods)Native.KeyMods(Handle);

    /// <summary>True when the pointer is over this window and the UI wants it.</summary>
    public bool WantsMouse => Native.WantsMouse(Handle) != 0;

    /// <summary>The cursor the UI asked for last frame, for a backend that draws its own.</summary>
    public Cursor Cursor => Native.GetCursor(Handle);

    /// <summary>The name of a key as the UI shows it: <c>A</c>, <c>Space</c>, <c>F5</c>.</summary>
    public static string KeyName(Key key) => Marshal.PtrToStringUTF8(Native.KeyName(key)) ?? "";

    // ---- measurement -------------------------------------------------------

    /// <summary>Width and height of a single line of text, without wrapping.</summary>
    /// <remarks>
    /// Shaping, kerning and the font's own metrics are CUI's, so a hand-written control
    /// lays text out the same way the built-in components do. There is no allocation: the
    /// text is measured in the frame arena.
    /// </remarks>
    public TextMetrics MeasureText(UiText text, float size = 12f, Font font = Font.Body) =>
        TextMetrics.FromNative(Native.MeasureText(Handle, font, size, Utf8.Get(text), -1));

    // ---- disabled ----------------------------------------------------------

    /// <summary>True inside a <c>Disabled</c> scope (or one a parent opened).</summary>
    public bool IsDisabled => Native.IsDisabled(Handle) != 0;

    /// <summary>
    /// Draws <paramref name="body"/> with every control inside it disabled: it still
    /// lays out and paints, but clicks, keys and hover do nothing.
    /// </summary>
    /// <example><code>
    /// using (ui.Disabled(busy)) { ui.Button("Save"); }
    /// </code></example>
    public DisabledScope Disabled(bool disabled, DrawFn body)
    {
        Native.PushDisabled(Handle, (byte)(disabled ? 1 : 0));
        try
        {
            body(this);
        }
        finally
        {
            Native.PopDisabled(Handle);
        }

        return default;
    }

    /// <summary>Opens a disabled gate that <c>using</c> closes.</summary>
    public DisabledScope Disabled(bool disabled)
    {
        Native.PushDisabled(Handle, (byte)(disabled ? 1 : 0));
        return new DisabledScope(Handle);
    }

    /// <summary>Rectangle of any widget by id, for hit tests and overlays.</summary>
    public RectF RectOf(uint id)
    {
        var r = Native.GetRect(Handle, id);
        return new RectF(r.X, r.Y, r.W, r.H);
    }

    /// <summary>Rectangle of the widget with this string id.</summary>
    public RectF RectOf(UiText id) => RectOf(MakeId(id));

    // ---- theme -------------------------------------------------------------

    /// <summary>
    /// The theme this frame draws with. Assign to switch, from anywhere inside a frame.
    /// </summary>
    /// <remarks>
    /// The switch takes effect on the next frame, and CUI animates every colour token across the
    /// change, so there is no flag to keep and nothing to invalidate: assign it in the branch that
    /// decides it. Reading it costs one call, and is what a theme switch's selected item wants.
    /// </remarks>
    /// <example><code>
    /// if (ui.Shortcut(Mods.Primary, Key.J)) ui.ToggleTheme();
    ///
    /// int mode = ui.Theme == Theme.Dark ? 1 : 0;
    /// if (ui.Segmented("theme", Modes, ref mode)) ui.Theme = mode == 1 ? Theme.Dark : Theme.Light;
    /// </code></example>
    public Theme Theme
    {
        readonly get => Native.GetThemeMode(Handle);
        set => Native.SetTheme(Handle, value);
    }

    /// <summary>The accent in use, and the way to change it. See <see cref="Theme"/>. </summary>
    public Accent Accent
    {
        readonly get => Native.GetAccent(Handle);
        set => Native.SetAccent(Handle, value);
    }

    /// <summary>Switches to the other theme: what a "dark mode" command is.</summary>
    public readonly void ToggleTheme() => Native.SetTheme(Handle, Theme == Theme.Dark ? Theme.Light : Theme.Dark);

    /// <summary>Resolves a semantic token to its channels; other colors come back unchanged.</summary>
    public Color ColorOf(Color color)
    {
        if (!color.IsToken) return color;
        return UiMarshal.FromNative(Native.ColorOf(Handle, UiMarshal.ToNative(color)));
    }

    /// <summary>The channels a theme token resolves to this frame.</summary>
    public Color TokenColor(Token token) => UiMarshal.FromNative(Native.TokenColor(Handle, token));

    /// <summary>Turns off animation (transitions, springs and spinners) for accessibility.</summary>
    public void SetReducedMotion(bool reduced) => Native.SetReducedMotion(Handle, (byte)(reduced ? 1 : 0));

    /// <summary>The name of an accent as the UI shows it: "Violet".</summary>
    public static string AccentName(Accent accent) => Marshal.PtrToStringUTF8(Native.AccentName(accent)) ?? "";
}

/// <summary>Closes a disabled gate opened by <see cref="Ui.Disabled(bool)"/>.</summary>
public ref struct DisabledScope : IDisposable
{
    private nint _ui;

    internal DisabledScope(nint ui) { _ui = ui; }

    /// <summary>Pops the gate. Disposing twice does nothing.</summary>
    public void Dispose()
    {
        nint ui = _ui;
        if (ui == 0) return;
        _ui = 0;
        Native.PopDisabled(ui);
    }
}

/// <summary>The measurements CUI reports for one run of text.</summary>
public readonly struct TextMetrics
{
    public readonly float Width;
    public readonly float Height;
    public readonly float Ascent;
    public readonly float Descent;
    public readonly float LineHeight;

    internal TextMetrics(float width, float height, float ascent, float descent, float lineHeight)
    {
        Width = width;
        Height = height;
        Ascent = ascent;
        Descent = descent;
        LineHeight = lineHeight;
    }

    internal static TextMetrics FromNative(NativeTextMetrics m) =>
        new(m.Width, m.Height, m.Ascent, m.Descent, m.LineHeight);

    /// <summary>How many lines of this text fit in <paramref name="height"/>.</summary>
    public int LinesThatFit(float height) => LineHeight <= 0 ? 1 : Math.Max(1, (int)(height / LineHeight));
}
