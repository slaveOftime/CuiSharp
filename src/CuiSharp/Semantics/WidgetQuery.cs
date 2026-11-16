namespace CuiSharp;

/// <summary>
/// What a search for a control can say: a label, a kind and a label (<c>Button "Save"</c>), a
/// role (<c>role:button</c>), a value (<c>value:Milk</c>), or a path through the tree
/// (<c>editor &gt; Button "Save"</c>: the containers are bare, and only the last step names a kind).
/// </summary>
/// <remarks>
/// <para>
/// An interface has more than one thing saying "Save" — a heading above the button, a menu entry
/// in another container — so a search that only compared substrings would click whichever was
/// drawn first. This type is the whole vocabulary a caller needs to be specific, and it ranks what
/// it does find: an exact match beats a prefix, a control beats a label.
/// </para>
/// <para>
/// Parsing is deliberately small: <c>Button "Save"</c>, <c>Save</c>, <c>role:button Save</c>,
/// <c>value:Milk</c>, and <c>editor &gt; Save</c> are the shapes worth knowing, and everything
/// else is a plain label. A query cannot fail to parse — a label may contain quotes and angles,
/// and the search should look for them — so the only guess in it is that a kind is one capitalized
/// word, which is what every wrapper name is.
/// </para>
/// </remarks>
public readonly struct WidgetQuery
{
    private readonly string? _kind;

    private WidgetQuery(string text, string? kind, string[] path, SemanticRole? role = null, string? value = null)
    {
        Text = text;
        _kind = kind;
        Path = path;
        Role = role;
        Value = value;
    }

    /// <summary>The text to look for: a label, an id, or the leaf of a path.</summary>
    public string Text { get; }

    /// <summary>The kind the caller named, or null.</summary>
    public string? Kind => _kind;

    /// <summary>The semantic role to require, or null.</summary>
    public SemanticRole? Role { get; }

    /// <summary>The value to require, or null: a text field's text, a slider's number, a toggle's state.</summary>
    public string? Value { get; }

    /// <summary>The containers to walk through, outermost first; empty for a plain search.</summary>
    public string[] Path { get; }

    /// <summary>A query built from parts, for a caller that already knows them.</summary>
    internal static WidgetQuery Of(
        string? kind, string text, string[]? path = null, SemanticRole? role = null, string? value = null) =>
        new(text, kind, path ?? [], role, value);

    /// <summary>
    /// Reads a query: <c>Save</c>, <c>Button "Save"</c>, <c>role:button Save</c>,
    /// <c>value:Milk</c>, <c>editor &gt; Save</c>, or <c>id:draft</c> for a widget by the id it was
    /// drawn with — the form that keeps working when the label is translated.
    /// </summary>
    public static WidgetQuery Parse(string query)
    {
        ArgumentException.ThrowIfNullOrEmpty(query);

        string remaining = query.Trim();
        SemanticRole? role = null;
        string? value = null;

        // `role:button` is a filter in front of the rest. A value filter consumes the rest:
        // values may contain spaces, and a query that names a value is already specific.
        if (remaining.StartsWith("role:", StringComparison.OrdinalIgnoreCase))
        {
            int space = remaining.IndexOf(' ', StringComparison.Ordinal);
            string token = space < 0 ? remaining[5..] : remaining[5..space];
            if (SemanticRoles.TryParse(Unquote(token), out SemanticRole parsedRole))
            {
                role = parsedRole;
                remaining = space < 0 ? "" : remaining[(space + 1)..].TrimStart();
            }
        }

        if (remaining.StartsWith("value:", StringComparison.OrdinalIgnoreCase))
        {
            value = Unquote(remaining[6..].Trim());
            remaining = "";
        }

        string[] steps = remaining.Split('>', StringSplitOptions.TrimEntries);
        if (steps.Length > 1)
        {
            // A path names containers and ends at the control; only the leaf can be typed.
            (string text, string? kind) = SplitKind(steps[^1]);
            var containers = new string[steps.Length - 1];
            for (int i = 0; i < containers.Length; i++) containers[i] = Unquote(steps[i]);
            return new WidgetQuery(text, kind, containers, role, value);
        }

        (string leaf, string? leafKind) = SplitKind(remaining);
        return new WidgetQuery(leaf, leafKind, [], role, value);
    }

    /// <summary>The query as a person would write it.</summary>
    public override string ToString()
    {
        var parts = new List<string>(3);
        if (Role is { } role) parts.Add($"role:{SemanticRoles.Name(role)}");
        if (Value is { } value) parts.Add($"value:{Quote(value)}");

        string leaf = Path.Length == 0
            ? Describe(_kind, Text)
            : string.Join(" > ", [.. Path, Describe(_kind, Text)]);
        if (leaf.Length > 0) parts.Add(leaf);
        return string.Join(' ', parts);
    }

    private static string Describe(string? kind, string text) =>
        kind is null ? text : $"{kind} \"{text}\"";

    private static string Quote(string text) =>
        text.Any(static ch => char.IsWhiteSpace(ch) || ch == '"') ? $"\"{text.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : text;

    /// <summary>Takes a leading kind off <c>Button "Save"</c>, leaving the label.</summary>
    private static (string Text, string? Kind) SplitKind(string step)
    {
        int quote = step.IndexOf('"', StringComparison.Ordinal);
        if (quote <= 0) return (Unquote(step), null);

        string kind = step[..quote].Trim();
        string rest = step[quote..].Trim();
        // A kind is one capitalized word, which is what every wrapper name is: `Save "now"` and
        // `save "hi"` are labels, `Button "Save"` names a kind.
        if (kind.Length == 0 || kind.Contains(' ', StringComparison.Ordinal) || !char.IsUpper(kind[0]))
            return (Unquote(step), null);
        return (Unquote(rest), kind);
    }

    private static string Unquote(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal)
            : trimmed;
    }
}
