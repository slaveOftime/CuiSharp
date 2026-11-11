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
/// to be an extent. A rect is printed as fact only where the recorder proved the id is the
/// widget's own (<see cref="SemanticNode.HasOwnRect"/>): a widget recorded by text alone, or by
/// whatever id the previous call happened to leave behind, prints <c>[rect:unverified]</c> and
/// counts for nothing. An unnamed container has no native id at all and cannot appear.
/// </para>
/// <para>
/// Observed bounds are written <c>union≥</c>: they are the union of the verified recorded rects
/// a node's own native layout layer holds, a lower bound on what the node actually holds, not
/// the layout's content extent. A floating widget — a dialog, popover, menu, tooltip, toast or
/// tour — is drawn on a layer of its own, marked <c>(floating)</c>, kept in the tree exactly
/// where the app drew it, and never counted into the bounds of the containers it is nested
/// under; an unnamed floating scope leaves no node of its own, but its contents still carry
/// the layer they were drawn on.
/// </para>
/// </remarks>
public static class LayoutFormatter
{
    /// <summary>The warning line every layout view carries, so no line below it reads as more than it is.</summary>
    public const string Header =
        "layout: rects exist only for ids CUI lays out; unnamed/id-0 containers and widgets "
        + "without a proven own id print [rect:unverified]; union≥ is a lower bound of recorded "
        + "rects, not a content extent; scrolled/virtualized extents are UNKNOWN (CUI keeps them "
        + "private); floating layers (dialogs, popovers, toasts, ...) sit outside their container.";

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

            // A floating widget is drawn on a layer of its own: say so where it prints, so its
            // rect and its absence from the ancestors' bounds both read as deliberate.
            if (node.Detached) sb.Append(" (floating)");

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
    /// True when the recorder proved the id this node was recorded under is the widget's own —
    /// the id CUI laid it out under — and CUI actually gave that id a rectangle this frame.
    /// Nothing else counts: not the kind's rank in a query, not a label that looks trustworthy.
    /// </summary>
    private static bool HasVerifiedRect(SemanticNode node) =>
        node.Id != 0 && node.HasOwnRect && node.Rect.W > 0 && node.Rect.H > 0;

    private static string RectDoubt(SemanticNode node) =>
        node.Id == 0 ? "[rect:none]" : "[rect:unverified]";

    /// <summary>
    /// The union of the verified rects of everything this node's own layout layer holds, or null
    /// if none has one. A floating descendant — marked or not — is not its content: CUI lays
    /// that subtree out on a layer of its own, anchored wherever it belongs, and an unnamed
    /// floating scope leaves no node to mark, so the layer the recorder stamped on each entry
    /// is what keeps those rects out of these bounds and out of any OVERFLOW.
    /// </summary>
    private static RectF? ObservedBounds(SemanticNode node)
    {
        RectF? acc = null;
        foreach (SemanticNode child in node.Children)
        {
            if (child.Detached || child.Layer != node.Layer) continue;
            if (HasVerifiedRect(child))
                acc = acc is { } current ? Expand(current, child.Rect) : child.Rect;
            if (ObservedBounds(child) is { } inner)
                acc = acc is { } wider ? Expand(wider, inner) : inner;
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
