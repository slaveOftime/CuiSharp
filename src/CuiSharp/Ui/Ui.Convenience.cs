// Concise authoring for the hot fields of the layout and text option structs (issue #9).
// These are constructors, not a second set of method overloads: no new candidate competes with
// the generated wrappers, so Row(), Row(default), Card(new()) and Text("x", default) keep their
// exact current binding. ConvenienceCompilerTests pins that compatibility and the concise shapes.

namespace CuiSharp;

public partial struct Box
{
    /// <summary>
    /// A box with its hot layout fields set: <c>ui.Row(new(gap: 12, align: Align.Center))</c>,
    /// <c>ui.Column(new(pad: 18, gap: 12, height: Size.Fill))</c>. Every other field keeps its
    /// native default, exactly as the equivalent object initializer.
    /// </summary>
    public Box(float gap = 0, float pad = 0, Align align = default, Size width = default, Size height = default)
        : this()
    {
        Gap = gap;
        Pad = pad;
        Align = align;
        Width = width;
        Height = height;
    }
}

public partial struct Card
{
    /// <summary>
    /// A card with its hot fields set: <c>ui.Card(new(pad: 16, gap: 12, dir: Dir.Column))</c>.
    /// Every other field keeps its native default, exactly as the equivalent object initializer.
    /// </summary>
    public Card(float pad = 0, float gap = 0, Dir dir = default, Size width = default, Size height = default)
        : this()
    {
        Pad = pad;
        Gap = gap;
        Dir = dir;
        Width = width;
        Height = height;
    }
}

public partial struct TextOpts
{
    /// <summary>
    /// Text options with the two fields a call site reaches for most:
    /// <c>ui.Text("Live", new(size: 12, color: Color.TextSecondary))</c>. Every other field keeps
    /// its native default, exactly as the equivalent object initializer.
    /// </summary>
    public TextOpts(float size = 0, Color color = default)
        : this()
    {
        Size = size;
        Color = color;
    }
}

public partial struct BadgeOpts
{
    /// <summary>
    /// A badge in one word: <c>ui.Badge("Updating", new(Tone.Danger))</c>. Every other field keeps
    /// its native default, exactly as the equivalent object initializer.
    /// </summary>
    public BadgeOpts(Tone tone = default)
        : this()
    {
        Tone = tone;
    }
}
