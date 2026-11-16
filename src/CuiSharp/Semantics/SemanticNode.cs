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

    private SemanticNode(
        string kind, string text, RectF rect, int depth, SemanticNode? parent, uint id = 0,
        bool hasOwnRect = false, bool detached = false, int layer = 0,
        string? automationId = null, string? value = null, SemanticState state = SemanticState.None)
    {
        Kind = kind;
        Text = text;
        Rect = rect;
        Depth = depth;
        Parent = parent;
        Id = id;
        HasOwnRect = hasOwnRect;
        Detached = detached;
        Layer = layer;
        AutomationId = automationId;
        Value = value;
        State = state;
    }

    /// <summary>The wrapper that drew it: <c>Button</c>, <c>Input</c>, <c>Card</c>.</summary>
    public string Kind { get; }

    /// <summary>The label, or the id when the widget or container has one.</summary>
    public string Text { get; }

    /// <summary>The same thing as <see cref="Text"/>, named the way an accessibility adapter reads it.</summary>
    public string Name => Text;

    /// <summary>
    /// The role this wrapper plays, e.g. <see cref="SemanticRole.Button"/>. The exact wrapper is
    /// still <see cref="Kind"/>; the role is what an accessibility tree, an agent or a query groups by.
    /// </summary>
    public SemanticRole Role => SemanticRoles.Of(Kind);

    /// <summary>
    /// A stable id the app (or a <c>ui.Id</c> / <c>ui.Automation</c> scope) gave this widget,
    /// independent of its label. Null when the app did not name it; <c>id:</c> queries also keep
    /// matching the recorded text for widgets whose text is already their id.
    /// </summary>
    public string? AutomationId { get; }

    /// <summary>The value a control holds this frame: a text field's text, a slider's number, a toggle's on/off.</summary>
    public string? Value { get; }

    /// <summary>What was observable about the control in this frame.</summary>
    public SemanticState State { get; }

    /// <summary>True when a <c>ui.Disabled</c> scope did not cover the control.</summary>
    public bool IsEnabled => (State & SemanticState.Enabled) != 0;

    /// <summary>True when this frame reported keyboard focus for the control.</summary>
    public bool IsFocused => (State & SemanticState.Focused) != 0;

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

    /// <summary>
    /// True when the recorded id is one CUI laid this very widget out under, so its rect is its
    /// own. False means the id may belong to whatever was drawn before it: the rect is a hint,
    /// not a fact. This is geometry provenance, unrelated to how queries rank the node.
    /// </summary>
    public bool HasOwnRect { get; }

    /// <summary>
    /// True when CUI draws this node on a floating layer of its own — a dialog, popover, menu,
    /// tooltip, toast or tour. The tree keeps it where the app drew it, but its geometry belongs
    /// to its own layer and says nothing about the bounds of its nesting ancestors.
    /// </summary>
    public bool Detached { get; }

    /// <summary>
    /// The native layout layer this node was drawn on: 0 for the frame's own flow, one higher
    /// for every floating scope enclosing it. An unnamed floating scope records no node, so
    /// this is how its contents stay attributed to their own layer even while the tree keeps
    /// them where the app drew them.
    /// </summary>
    internal int Layer { get; }

    /// <summary>True when the app drew something inside it, so it can be a parent.</summary>
    public bool IsContainer { get; private set; }

    /// <summary>
    /// The nesting that leads here, e.g. <c>Card "editor" &gt; Button "Save"</c>: the shortest
    /// description of where a control is.
    /// </summary>
    /// <remarks>
    /// Written to be read, so a container carries its kind. A query drops the kinds from the
    /// containers and keeps them only on the last step: <c>editor &gt; Button "Save"</c>.
    /// </remarks>
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
    /// (<c>Button "Save"</c>), or a path (<c>editor &gt; Button "Save"</c>: a container is named by
    /// its id or label, and only the last step may name a kind).
    /// </summary>
    /// <remarks>
    /// An exact label beats a partial one and a control that can be acted on beats static text,
    /// so a heading that happens to say "Save" never hides the button underneath it.
    /// </remarks>
    public bool TryFind(string query, out SemanticNode node) => TryFind(WidgetQuery.Parse(query), out node);

    internal bool TryFind(WidgetQuery query, out SemanticNode node)
    {
        if (query.Path.Length > 0)
        {
            // Walk the named containers, outermost first, then search what the last one holds.
            SemanticNode scope = this;
            foreach (string step in query.Path)
            {
                if (!scope.TryFindStep(step, out SemanticNode container)) goto NotFound;
                scope = container;
            }

            if (scope.TryFind(WidgetQuery.Of(query.Kind, query.Text, role: query.Role, value: query.Value), out node)) return true;
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

    private bool TryFindStep(string step, out SemanticNode container)
    {
        WidgetQuery query = WidgetQuery.Of(kind: null, step);
        WidgetQuery byId = step.StartsWith("id:", StringComparison.Ordinal)
            ? query
            : WidgetQuery.Of(kind: null, "id:" + step);

        SemanticNode? best = null;
        int bestScore = int.MaxValue;
        foreach (SemanticNode candidate in SelfAndDescendants())
        {
            if (!candidate.IsContainer) continue;
            int score = Math.Min(candidate.Score(query), candidate.Score(byId));
            if (score < bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        container = best!;
        return best is not null;
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
    /// Finds one node, refusing to choose when several controls share the best rank. The ordinary
    /// <see cref="TryFind(string, out SemanticNode)"/> keeps its ranking for programmatic callers;
    /// the headless <c>find</c> and <c>click</c> use this so a test can never silently hit the
    /// wrong row.
    /// </summary>
    internal bool TryFindUnique(WidgetQuery query, out SemanticNode node)
    {
        // A path narrows the scope, then the leaf uses the same strict search as a plain query.
        if (query.Path.Length > 0)
        {
            SemanticNode scope = this;
            foreach (string step in query.Path)
            {
                if (!scope.TryFindStep(step, out SemanticNode container))
                {
                    node = null!;
                    return false;
                }

                scope = container;
            }

            return scope.TryFindUnique(WidgetQuery.Of(query.Kind, query.Text, role: query.Role, value: query.Value), out node);
        }

        SemanticNode? best = null;
        int bestScore = int.MaxValue;
        var matches = new List<SemanticNode>();

        foreach (SemanticNode candidate in SelfAndDescendants())
        {
            int score = candidate.Score(query);
            if (score == int.MaxValue) continue;

            if (score < bestScore)
            {
                bestScore = score;
                best = candidate;
                matches.Clear();
                matches.Add(candidate);
            }
            else if (score == bestScore)
            {
                matches.Add(candidate);
            }
        }

        if (best is null)
        {
            node = null!;
            return false;
        }

        if (matches.Count > 1)
            throw new InvalidOperationException(Ambiguous(query, matches));

        node = best;
        return true;
    }

    private static string Ambiguous(WidgetQuery query, List<SemanticNode> matches)
    {
        const int shown = 8;
        string paths = string.Join(
            '\n', matches.Take(shown).Select(static match => "  " + match.Path));
        string more = matches.Count > shown ? $"\n  …and {matches.Count - shown} more" : "";

        return $"\"{query}\" matches {matches.Count} controls; refusing to guess.{Environment.NewLine}" +
            paths + more + Environment.NewLine +
            "Use a kind (`Button \"…\"`), a role (`role:button`), a value (`value:…`), a path (`card > Button \"…\"`) or `id:…`.";
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
        if (query.Kind is { } kind && !Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)) return int.MaxValue;
        if (query.Role is { } role && Role != role) return int.MaxValue;

        // `id:draft` is the exact-id form: no partial match, no language. An explicit automation
        // id beats a fallback text match, and an actionable control beats its container or a
        // static label inside the same id scope.
        if (query.Path.Length == 0
            && query.Role is null
            && query.Value is null
            && query.Text.StartsWith("id:", StringComparison.Ordinal))
        {
            string id = query.Text[3..];
            if (id.Length == 0) return int.MaxValue;
            int rank = IsStatic(Kind) ? 2 : IsContainer ? 1 : 0;
            if (string.Equals(AutomationId, id, StringComparison.Ordinal)) return rank;
            if (string.Equals(Text, id, StringComparison.Ordinal)) return 3 + rank;
            return int.MaxValue;
        }

        int valueTier = 0;
        if (query.Value is { } value)
        {
            if (Value is null) return int.MaxValue;
            valueTier = Value.Equals(value, StringComparison.OrdinalIgnoreCase) ? 0
                : Value.StartsWith(value, StringComparison.OrdinalIgnoreCase) ? 1
                : Value.Contains(value, StringComparison.OrdinalIgnoreCase) ? 2
                : int.MaxValue;
            if (valueTier == int.MaxValue) return valueTier;
        }

        if (query.Text.Length == 0)
        {
            if (query.Role is null && query.Value is null) return int.MaxValue;
            return (IsStatic(Kind) ? 3 : 0) + valueTier;
        }

        int tier = Text.Equals(query.Text, StringComparison.OrdinalIgnoreCase) ? 0
            : Text.StartsWith(query.Text, StringComparison.OrdinalIgnoreCase) ? 1
            : Text.Contains(query.Text, StringComparison.OrdinalIgnoreCase) ? 2
            : int.MaxValue;
        if (tier == int.MaxValue) return tier;

        // Text a person reads but cannot act on: a click on it lands on whatever is behind it.
        return (IsStatic(Kind) ? 3 : 0) + tier + valueTier;
    }

    /// <summary>
    /// The wrappers that draw text and nothing to act on. A search prefers anything else when
    /// both answer the query, which is what keeps <c>click Save</c> on the button.
    /// </summary>
    internal static bool IsStatic(string kind) => kind is
        "Heading" or "Label" or "Secondary" or "Text" or "Muted" or "Badge" or "Caption" or "Code" or
        "Kbd" or "Divider" or "Separator" or "Spacer" or "Gap" or "Icon" or "Progress" or "ProgressBar" or
        "ProgressRing" or "Meter" or "Gauge" or "RadialGauge" or "Skeleton" or "Spinner" or "EmptyState" or
        "Metric" or "Readout" or "DotDisplay" or "StatusBarText";

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
    /// <param name="details">
    /// Include the semantic role and any automation id, value or state the frame recorded. The
    /// default output stays the compact kind-and-label tree existing tests and readers know;
    /// <c>dotnet cui tree --details</c> asks for this form.
    /// </param>
    public string Print(bool rects = false, bool details = false)
    {
        var sb = new StringBuilder();
        Walk(this, 0);
        return sb.ToString().TrimEnd('\n');

        void Walk(SemanticNode node, int indent)
        {
            sb.Append(' ', indent * 2).Append(details ? node.Describe() : node.Label);
            if (rects) sb.Append(' ').Append(node.Rect);
            sb.Append('\n');
            foreach (SemanticNode child in node._children) Walk(child, indent + 1);
        }
    }

    /// <summary>
    /// The compact label plus the role, automation id, value and state of this one node.
    /// </summary>
    public string Describe()
    {
        var text = new StringBuilder(Label);
        text.Append(" role=").Append(SemanticRoles.Name(Role));
        if (AutomationId is { Length: > 0 } id) text.Append(" id=").Append(id);
        if (Value is not null) text.Append(" value=\"").Append(Escape(Value)).Append('"');
        if (State != SemanticState.None) text.Append(" state=").Append(SemanticStates.Format(State));
        return text.ToString();
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    /// <summary>
    /// Projects this frame tree onto the shape a platform accessibility layer consumes. The
    /// platform adapter (UIA, AT-SPI, NSAccessibility) is still ahead; this object is the stable
    /// data it will read, so the app does not have to expose a second tree.
    /// </summary>
    public SemanticAccessibilityNode ToAccessibility() =>
        new(Role, Name, Value, State, AutomationId, Rect, [.. _children.Select(child => child.ToAccessibility())]);

    /// <inheritdoc />
    public override string ToString() => Label;

    /// <summary>
    /// Finds a node by the explicit id the app gave it. The text of a widget that takes an id is
    /// still the fallback, so existing <c>FindById("draft")</c> calls keep working.
    /// </summary>
    internal SemanticNode? FindById(string id) =>
        TryFind(WidgetQuery.Of(kind: null, "id:" + id), out SemanticNode node) ? node : null;

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
                open[^1]._children.Add(new SemanticNode(
                    entry.Kind, entry.Text, entry.Rect, entry.Depth, open[^1], entry.Id,
                    entry.HasOwnRect, entry.Detached, entry.Layer,
                    entry.AutomationId, entry.Value, entry.State));
                continue;
            }

            var container = new SemanticNode(
                entry.Kind, entry.Text, entry.Rect, entry.Depth, open[^1], entry.Id,
                entry.HasOwnRect, entry.Detached, entry.Layer,
                entry.AutomationId, entry.Value, entry.State)
            {
                IsContainer = true,
            };
            open[^1]._children.Add(container);
            open.Add(container);
        }

        return root;
    }
}
