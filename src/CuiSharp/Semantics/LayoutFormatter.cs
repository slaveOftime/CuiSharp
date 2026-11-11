using System.Text;

namespace CuiSharp;

/// <summary>
/// A layout view of a recorded frame: each named node with the rectangle CUI laid it out under
/// its own id where one is known, the observed bounds of what a container holds where any
/// descendant has a verified rect, and the unknowns named as unknowns.
/// </summary>
/// <remarks>
/// <para>
/// What CUI does not expose, this does not invent. A true content extent lives in CUI's private
/// per-id scroll state and a wheel-scrolled widget reports an unbounded sentinel there, so a
/// scrolled or virtualized node prints <c>extent:UNKNOWN</c> instead of a child union pretending
/// to be an extent. A widget recorded by text alone (a heading, a label) is laid out under no id
/// of its own, so its rectangle is not verified and its bounds are excluded from every union.
/// An unnamed container has no native id at all and cannot appear.
/// </para>
/// <para>
/// Observed bounds are written <c>union≥</c>: they are the union of the verified recorded rects
/// inside a node, a lower bound on what the node actually holds, not the layout's content extent.
/// </para>
/// </remarks>
public static class LayoutFormatter
{
    /// <summary>The warning line every layout view carries, so no line below it reads as more than it is.</summary>
    public const string Header =
        "layout: rects exist only for ids CUI lays out; unnamed/id-0 containers and text-only "
        + "widgets have no verified rect; union≥ is a lower bound of recorded rects, not a "
        + "content extent; scrolled/virtualized extents are UNKNOWN (CUI keeps them private).";

    /// <summary>
    /// The wrappers CUI keeps its content scrolled behind: the rect is the viewport, the real
    /// extent is private state, and for wheel-scrolled widgets CUI itself models it as unbounded.
    /// </summary>
    /// <remarks>
    /// Each entry was read out of the pinned native source: scroll areas and the virtual list
    /// derive per-id scroll state (cui_widgets.c, cui_data.c), the table/tree/grid and code views
    /// scroll their own window (cui_data.c, cui_code.c), and the terminal, flame and graph views
    /// report unbounded wheel content (cui_terminal.c, cui_flame.c, cui_graph.c). The header
    /// disclaimer covers any scrolled widget this list has not met yet: a missing entry means a
    /// line loses its UNKNOWN annotation, never a false extent.
    /// </remarks>
    private static readonly HashSet<string> ScrolledContentKinds =
    [
        "Scroll", "VirtualList", "Table", "Tree", "TreeTable", "DataGrid",
        "CodeEditor", "HexView", "DiffView", "LogView", "Terminal",
        "FlameGraph", "GraphView",
    ];

    /// <summary>Renders the layout view of one recorded frame tree, header line first.</summary>
    public static string Print(SemanticNode frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var sb = new StringBuilder();
        sb.Append(Header).Append('\n');
        Walk(frame, 0);
        return sb.ToString().TrimEnd('\n');

        void Walk(SemanticNode node, int indent)
        {
            sb.Append(' ', indent * 2).Append(Label(node));

            // The frame root is not a widget: it never asked for a rect.
            if (node.Parent is not null)
                sb.Append(' ').Append(HasVerifiedRect(node) ? $"[{node.Rect}]" : RectDoubt(node));

            if (ScrolledContentKinds.Contains(node.Kind))
                sb.Append(" extent:UNKNOWN(scrolled or virtualized content is not exposed)");

            if (node.Children.Count > 0 && ObservedBounds(node) is { } bounds)
            {
                sb.Append(" union≥[").Append(bounds).Append("] (lower-bound, recorded rects)");
                if (HasVerifiedRect(node) && Exceeds(bounds, node.Rect))
                    sb.Append(" OVERFLOW(observed bounds)");
            }

            sb.Append('\n');
            foreach (SemanticNode child in node.Children) Walk(child, indent + 1);
        }
    }

    /// <summary>
    /// True when the id this node was recorded under is the widget's own: CUI laid it out under
    /// that id and only that widget's rect can be there. A text-only widget records under
    /// whatever id the last action set, which makes its rect someone else's.
    /// </summary>
    private static bool HasVerifiedRect(SemanticNode node) =>
        node.Id != 0 && !SemanticNode.IsStatic(node.Kind);

    private static string RectDoubt(SemanticNode node) =>
        node.Id == 0 ? "[rect:none]" : "[rect:unverified]";

    /// <summary>The union of the verified rects of everything below this node, or null if none has one.</summary>
    private static RectF? ObservedBounds(SemanticNode node)
    {
        RectF? acc = null;
        foreach (SemanticNode descendant in node.Descendants())
        {
            if (!HasVerifiedRect(descendant)) continue;
            acc = acc is { } current ? Expand(current, descendant.Rect) : descendant.Rect;
        }

        return acc;
    }

    private static bool Exceeds(RectF content, RectF box) =>
        content.X < box.X - 0.5f || content.Y < box.Y - 0.5f
        || content.X + content.W > box.X + box.W + 0.5f
        || content.Y + content.H > box.Y + box.H + 0.5f;

    private static RectF Expand(RectF a, RectF b)
    {
        float x = MathF.Min(a.X, b.X);
        float y = MathF.Min(a.Y, b.Y);
        float right = MathF.Max(a.X + a.W, b.X + b.W);
        float bottom = MathF.Max(a.Y + a.H, b.Y + b.H);
        return new RectF(x, y, right - x, bottom - y);
    }

    private static string Label(SemanticNode node) =>
        node.Text.Length == 0 ? node.Kind : $"{node.Kind} \"{node.Text}\"";
}
