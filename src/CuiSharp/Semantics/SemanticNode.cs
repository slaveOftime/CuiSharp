using System.Text;

namespace CuiSharp;

/// <summary>
/// One named thing in a frame — a control, or a container the app gave an id — and everything
/// drawn inside it.
/// </summary>
/// <remarks>
/// <para>
/// The tree is the nesting the app wrote: a button drawn inside <c>ui.Card(new() { Id = "editor" })</c>
/// is a child of <c>Card "editor"</c>, and a button inside an unnamed column is still a child of
/// whatever named container encloses that column. Only things with a name appear, so the tree
/// says nothing it does not know.
/// </para>
/// <para>
/// A node is a snapshot of the frame it came from: find the control again after the interface
/// changes rather than holding a node.
/// </para>
/// </remarks>
public sealed class SemanticNode
{
    private readonly List<SemanticNode> _children = [];

    private SemanticNode(string kind, string text, RectF rect, int depth, SemanticNode? parent, uint id = 0)
    {
        Kind = kind;
        Text = text;
        Rect = rect;
        Depth = depth;
        Parent = parent;
        Id = id;
    }

    /// <summary>The wrapper that drew it: <c>Button</c>, <c>Input</c>, <c>Card</c>.</summary>
    public string Kind { get; }

    /// <summary>The label, or the id when the widget or container has one.</summary>
    public string Text { get; }

    /// <summary>Where it was laid out, in points.</summary>
    public RectF Rect { get; }

    /// <summary>How many containers were open around it.</summary>
    public int Depth { get; }

    /// <summary>The container it sits in, or null for a node at the top of the frame.</summary>
    public SemanticNode? Parent { get; }

    /// <summary>Everything drawn inside it, in drawing order.</summary>
    public IReadOnlyList<SemanticNode> Children => _children;

    /// <summary>
    /// The id CUI laid it out under — for an item of a tab strip or a segmented control, the id
    /// the widget derived for that item. Unrelated to the label, so it survives translation.
    /// </summary>
    public uint Id { get; }

    /// <summary>True when the app drew something inside it, so it can be a parent.</summary>
    public bool IsContainer { get; private set; }

    /// <summary>
    /// The nesting that leads here, e.g. <c>Card "editor" &gt; Button "Save"</c>: the shortest
    /// description of where a control is.
    /// </summary>
    public string Path =>
        Parent is null ? Kind : Parent.Kind == "Frame" ? Label : Parent.Path + " > " + Label;

    private string Label => Text.Length == 0 ? Kind : $"{Kind} \"{Text}\"";

    /// <summary>Every node below this one, depth first.</summary>
    public IEnumerable<SemanticNode> Descendants()
    {
        foreach (SemanticNode child in _children)
        {
            yield return child;
            foreach (SemanticNode deeper in child.Descendants()) yield return deeper;
        }
    }

    /// <summary>
    /// Finds this node or anything below it by a query: a label (<c>Save</c>), a kind and a label
    /// (<c>Button "Save"</c>), or a path (<c>Card "editor" &gt; Button "Save"</c>).
    /// </summary>
    /// <remarks>
    /// An exact label beats a partial one and a control that can be acted on beats static text,
    /// so a heading that happens to say "Save" never hides the button underneath it.
    /// </remarks>
    public bool TryFind(string query, out SemanticNode node) => TryFind(WidgetQuery.Parse(query), out node);

    internal bool TryFind(WidgetQuery query, out SemanticNode node)
    {
        // `id:draft` is the exact-id form: no ranking, no partial match, no language.
        if (query.Path.Length == 0 && query.Kind is null && query.Text.StartsWith("id:", StringComparison.Ordinal))
        {
            string id = query.Text[3..];
            foreach (SemanticNode candidate in SelfAndDescendants())
            {
                if (!string.Equals(candidate.Text, id, StringComparison.Ordinal)) continue;

                node = candidate;
                return true;
            }

            node = null!;
            return false;
        }

        if (query.Path.Length > 0)
        {
            // Walk the named containers, outermost first, then search what the last one holds.
            SemanticNode scope = this;
            foreach (string step in query.Path)
            {
                if (!scope.TryFind(WidgetQuery.Of(kind: null, step), out SemanticNode container)) goto NotFound;
                scope = container;
            }

            if (scope.TryFind(WidgetQuery.Of(query.Kind, query.Text), out node)) return true;
            goto NotFound;
        }

        SemanticNode? best = null;
        int bestScore = int.MaxValue;
        foreach (SemanticNode candidate in SelfAndDescendants())
        {
            int score = candidate.Score(query);
            if (score < bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        if (best is not null)
        {
            node = best;
            return true;
        }

    NotFound:
        node = null!;
        return false;
    }

    /// <summary>Finds a node below this one, or explains what is there instead.</summary>
    public SemanticNode Find(string query)
    {
        ArgumentException.ThrowIfNullOrEmpty(query);
        return TryFind(query, out SemanticNode node)
            ? node
            : throw new InvalidOperationException($"Nothing matching \"{query}\" under {Path}. It holds:\n{Print()}");
    }

    /// <summary>
    /// How well a node answers a query: lower is better, <see cref="int.MaxValue"/> is not at all.
    /// </summary>
    /// <remarks>
    /// Three tiers — the label is the query, the label starts with it, the label contains it —
    /// and a control that can be acted on outranks static text at the same tier. A kind in the
    /// query is a filter, not a preference: name one and only that kind answers.
    /// </remarks>
    private int Score(WidgetQuery query)
    {
        if (query.Text.Length == 0) return int.MaxValue;
        if (query.Kind is { } kind && !Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)) return int.MaxValue;

        int tier = Text.Equals(query.Text, StringComparison.OrdinalIgnoreCase) ? 0
            : Text.StartsWith(query.Text, StringComparison.OrdinalIgnoreCase) ? 1
            : Text.Contains(query.Text, StringComparison.OrdinalIgnoreCase) ? 2
            : int.MaxValue;
        if (tier == int.MaxValue) return tier;

        // Text a person reads but cannot act on: a click on it lands on whatever is behind it.
        return IsStatic(Kind) ? tier + 3 : tier;
    }

    /// <summary>
    /// The wrappers that draw text and nothing to act on. A search prefers anything else when
    /// both answer the query, which is what keeps <c>click Save</c> on the button.
    /// </summary>
    internal static bool IsStatic(string kind) => kind is
        "Heading" or "Label" or "Text" or "Muted" or "Badge" or "Caption" or "Code" or "Kbd" or
        "Divider" or "Spacer" or "Icon" or "ProgressBar" or "Spinner";

    internal IEnumerable<SemanticNode> SelfAndDescendants()
    {
        yield return this;
        foreach (SemanticNode descendant in Descendants()) yield return descendant;
    }

    /// <summary>
    /// The tree as text, one line per node: what a test prints when it fails, and what an agent
    /// reads to find out which controls a frame drew.
    /// </summary>
    /// <param name="rects">Include each node's laid-out rectangle.</param>
    public string Print(bool rects = false)
    {
        var sb = new StringBuilder();
        Walk(this, 0);
        return sb.ToString().TrimEnd('\n');

        void Walk(SemanticNode node, int indent)
        {
            sb.Append(' ', indent * 2).Append(node.Label);
            if (rects) sb.Append(' ').Append(node.Rect);
            sb.Append('\n');
            foreach (SemanticNode child in node._children) Walk(child, indent + 1);
        }
    }

    /// <inheritdoc />
    public override string ToString() => Label;

    /// <summary>Rebuilds the nesting from what a frame recorded.</summary>
    /// <summary>Finds a node by the id CUI gave it, below this one.</summary>
    internal SemanticNode? FindById(uint id)
    {
        foreach (SemanticNode candidate in SelfAndDescendants())
        {
            if (candidate.Id == id) return candidate;
        }

        return null;
    }

    internal static SemanticNode Build(IReadOnlyList<SemanticWidget> entries)
    {
        var root = new SemanticNode("Frame", "", default, 0, null) { IsContainer = true };
        // A named container is a node; everything else attaches to the nearest named container
        // that encloses it. Unnamed containers were never recorded, so their children hang off
        // the nearest named one and the depth still says how deep they really are.
        var open = new List<SemanticNode> { root };
        foreach (SemanticWidget entry in entries)
        {
            // Containers close themselves: depth never decreases inside one, so a container at
            // this depth or shallower has already ended. It has to happen for every entry, not
            // only for the next container: a control drawn after a table has closed belongs to
            // whatever encloses the table, not to the table.
            while (open.Count > 1 && open[^1].Depth >= entry.Depth) open.RemoveAt(open.Count - 1);

            if (!entry.IsContainer)
            {
                open[^1]._children.Add(new SemanticNode(entry.Kind, entry.Text, entry.Rect, entry.Depth, open[^1], entry.Id));
                continue;
            }

            var container = new SemanticNode(entry.Kind, entry.Text, entry.Rect, entry.Depth, open[^1], entry.Id)
            {
                IsContainer = true,
            };
            open[^1]._children.Add(container);
            open.Add(container);
        }

        return root;
    }
}
