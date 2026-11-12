using System.Buffers;
using System.Text;
using System.Text.Json;

namespace CuiSharp.Tools;

/// <summary>
/// The offline API catalog: the JSON the exporter wrote at build time, read as data. No
/// reflection, no project, no app, no native library — `dotnet cui api` answers from these
/// bytes alone. The exporter owns the truth; this class only finds entries in it and renders
/// them for a person or, with <see cref="CatalogResult.RenderJson"/>, for a machine.
/// </summary>
internal sealed class ApiCatalog
{
    private readonly List<CatalogType> types;

    private ApiCatalog(int schema, string version, List<CatalogType> types)
    {
        Schema = schema;
        Version = version;
        this.types = types;
    }

    internal int Schema { get; }

    /// <summary>The catalog schema this reader understands; anything else is refused by name.</summary>
    internal const int SupportedSchema = 1;

    /// <summary>The CuiSharp version the catalog was exported from.</summary>
    internal string Version { get; }

    internal IReadOnlyList<CatalogType> Types => types;

    /// <summary>
    /// Reads the shipped catalog, refusing anything this build cannot answer from: a wrong
    /// schema and a malformed shape both name their path, because the alternative is an
    /// <c>api</c> answer that quietly reads as empty.
    /// </summary>
    internal static ApiCatalog Load(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("the API catalog is not valid JSON: " + error.Message, error);
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Bad("$", "an object");

            int schema = RequireInt(root, "schema", "$");
            if (schema != SupportedSchema)
                throw new InvalidDataException(
                    $"the API catalog uses schema {schema}; this build of cui reads schema {SupportedSchema}. " +
                    "Update CuiSharp.Tools, or regenerate the catalog with tools/export-api.cs.");

            string version = RequireString(root, "version", "$");
            var types = new List<CatalogType>();
            int atType = 0;
            foreach (JsonElement t in RequireArray(root, "types", "$"))
            {
                string typePath = "$.types[" + atType++ + "]";
                if (t.ValueKind != JsonValueKind.Object) throw Bad(typePath, "an object");

                var members = new List<CatalogMember>();
                int atMember = 0;
                foreach (JsonElement m in RequireArray(t, "members", typePath))
                {
                    string memberPath = typePath + ".members[" + atMember++ + "]";
                    if (m.ValueKind != JsonValueKind.Object) throw Bad(memberPath, "an object");

                    var parameters = new List<CatalogParameter>();
                    if (m.TryGetProperty("parameters", out JsonElement ps))
                    {
                        int atParameter = 0;
                        foreach (JsonElement p in RequireValue(ps, memberPath + ".parameters", JsonValueKind.Array).EnumerateArray())
                        {
                            string parameterPath = memberPath + ".parameters[" + atParameter++ + "]";
                            if (p.ValueKind != JsonValueKind.Object) throw Bad(parameterPath, "an object");
                            parameters.Add(new CatalogParameter(
                                RequireString(p, "name", parameterPath),
                                RequireString(p, "type", parameterPath),
                                OptionalString(p, "default", parameterPath)));
                        }
                    }

                    members.Add(new CatalogMember(
                        RequireString(m, "name", memberPath),
                        RequireString(m, "kind", memberPath),
                        RequireString(m, "signature", memberPath),
                        OptionalString(m, "extensionOn", memberPath),
                        OptionalString(m, "member", memberPath),
                        OptionalString(m, "summary", memberPath) ?? "",
                        OptionalString(m, "value", memberPath),
                        parameters,
                        OptionalString(m, "id", memberPath)));
                }

                types.Add(new CatalogType(
                    RequireString(t, "name", typePath),
                    RequireString(t, "displayName", typePath),
                    RequireString(t, "kind", typePath),
                    OptionalString(t, "summary", typePath) ?? "",
                    members));
            }

            return new ApiCatalog(schema, version, types);
        }
    }

    /// <summary>The logical name the tools assembly embeds the checked-in catalog under.</summary>
    internal const string EmbeddedResourceName = "CuiSharp.Tools.api-catalog.json";

    /// <summary>
    /// Reads the catalog this assembly embedded at build time. This is the CLI's only catalog
    /// source: no file lookup, no project discovery, no reflection over CuiSharp, no native
    /// library — the bytes travelled inside the tool package, so `dotnet cui api` answers in a
    /// directory that holds no app at all.
    /// </summary>
    internal static ApiCatalog LoadEmbedded()
    {
        using Stream? data = typeof(ApiCatalog).Assembly.GetManifestResourceStream(EmbeddedResourceName);
        if (data is null)
            throw new ToolException(
                $"this build of cui carries no embedded API catalog (resource \"{EmbeddedResourceName}\"); " +
                "the CuiSharp.Tools package is broken — reinstall it.");

        using var reader = new StreamReader(data);
        return Load(reader.ReadToEnd());
    }

    private static InvalidDataException Bad(string path, string what) =>
        new("the API catalog is malformed: " + path + " should be " + what);

    private static JsonElement RequireValue(JsonElement value, string path, JsonValueKind kind) =>
        value.ValueKind == kind ? value : throw Bad(path, kind.ToString().ToLowerInvariant());

    private static JsonElement Require(JsonElement parent, string name, string path)
    {
        if (!parent.TryGetProperty(name, out JsonElement value)) throw Bad(path + "." + name, "present");
        return value;
    }

    private static int RequireInt(JsonElement parent, string name, string path)
    {
        JsonElement value = RequireValue(Require(parent, name, path), path + "." + name, JsonValueKind.Number);
        if (!value.TryGetInt32(out int number)) throw Bad(path + "." + name, "a 32-bit number");
        return number;
    }

    private static string RequireString(JsonElement parent, string name, string path) =>
        RequireValue(Require(parent, name, path), path + "." + name, JsonValueKind.String).GetString()!;

    private static string? OptionalString(JsonElement parent, string name, string path) =>
        parent.TryGetProperty(name, out JsonElement value)
            ? RequireValue(value, path + "." + name, JsonValueKind.String).GetString()
            : null;

    private static JsonElement[] RequireArray(JsonElement parent, string name, string path) =>
        [.. RequireValue(Require(parent, name, path), path + "." + name, JsonValueKind.Array).EnumerateArray()];

    /// <summary>
    /// Finds <c>Ui.LineChart</c>, <c>CuiSharp.ChartOpts</c>, or a bare member name. An unknown
    /// term is not dead air: it comes back with the nearest names, so the next call is one edit
    /// away, and an ambiguous one names its owners instead of pretending to answer.
    /// </summary>
    internal CatalogResult Query(string term)
    {
        // `Ui.LineChart(UiText, ChartOpts)` asks about the same member as `Ui.LineChart`.
        string clean = term.Trim();
        int paren = clean.IndexOf('(');
        if (paren > 0) clean = clean[..paren].TrimEnd();

        if (clean.Length == 0) return CatalogResult.Unknown(term, []);

        // `CuiSharp.ChartOpts` is a type name, not an owner and a member: the whole term
        // resolves as a type before anything is split, or the qualified name reads as a failed
        // owner lookup and answers with suggestions instead of the type the caller asked for.
        CatalogType? whole = FindType(clean);
        if (whole is not null) return CatalogResult.Exact(term, TypeLines(whole));

        string[] parts = clean.Split('.');
        if (parts.Length > 1)
        {
            // An owner was named: either that owner has the member or the term is unknown.
            // Falling back to every other type's member of that name would be a confident
            // wrong answer — `ChartOpts.Run` must not come back with `CuiApp.Run`.
            string ownerName = string.Join('.', parts[..^1]);
            string last = parts[^1];
            CatalogType? owner = FindType(ownerName);
            if (owner is null)
            {
                int dot = ownerName.LastIndexOf('.');
                string ownerLeaf = dot < 0 ? ownerName : ownerName[(dot + 1)..];
                return CatalogResult.Unknown(term, [.. Suggest(last).Concat(Suggest(ownerLeaf)).Distinct().Take(3)]);
            }

            if (HasMember(owner, last)) return CatalogResult.Exact(term, MemberLines(owner, last));
            return CatalogResult.Unknown(term, [.. SuggestWithin(last, owner.Members.Select(m => m.Name))]);
        }

        // A bare member name belongs to whoever declares it. One owner answers the question;
        // several owners means the term is ambiguous, and the qualified candidates are it.
        List<(CatalogType Owner, CatalogMember Member)> hits =
            [.. types.SelectMany(t => t.Members
                .Where(m => Equal(m.Name, clean) || Equal(m.SourceName ?? "", clean))
                .Select(m => (Owner: t, Member: m)))];
        if (hits.Count > 0)
        {
            List<CatalogType> owners = [.. hits.Select(h => h.Owner).Distinct()];
            if (owners.Count == 1) return CatalogResult.Exact(term, MemberLines(owners[0], clean));

            var lines = new List<string>();
            foreach (CatalogType owner in owners)
            {
                foreach (CatalogMember member in owner.Members.Where(m => Equal(m.Name, clean) || Equal(m.SourceName ?? "", clean)))
                    lines.Add($"{owner.DisplayName}.{member.Name}  {member.Signature}");
                if (lines.Count >= MaxCandidates) break;
            }

            if (owners.Count > MaxCandidates)
                lines.Add($"… {owners.Count} owners in all; name the owner to narrow it (`{owners[0].DisplayName}.{clean}`).");
            return CatalogResult.Ambiguous(term, [.. lines.Distinct().Take(MaxCandidates + 1)]);
        }

        List<string> candidates = Candidates(clean);
        if (candidates.Count > 0) return CatalogResult.Ambiguous(term, candidates);
        return CatalogResult.Unknown(term, [.. Suggest(clean)]);
    }

    /// <summary>How many qualified candidates an ambiguous answer lists before pointing at the owners.</summary>
    private const int MaxCandidates = 15;

    private CatalogType? FindType(string name) =>
        types.FirstOrDefault(t => Equal(t.Name, name) || Equal(Arityless(t.Name), name) || Equal(t.DisplayName, name));

    /// <summary>`CuiSharp.KeyFn`1` is reachable as `CuiSharp.KeyFn` too.</summary>
    private static string Arityless(string name)
    {
        int tick = name.IndexOf('`');
        return tick < 0 ? name : name[..tick];
    }

    private static bool HasMember(CatalogType owner, string name) =>
        owner.Members.Any(m => Equal(m.Name, name) || Equal(m.SourceName ?? "", name));

    private static bool Equal(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private List<string> TypeLines(CatalogType type)
    {
        var lines = new List<string> { $"{type.DisplayName}  [{type.Kind}]" };
        if (type.Summary.Length > 0) lines.Add($"  {type.Summary}");
        foreach (CatalogMember member in type.Members)
        {
            // The declaration is the answer: return type, parameter types, defaults, values.
            // A hoisted extension gets its call form as an example beneath it, never in place
            // of the signature — a field rendered as `ui.Width` is neither declaration nor call.
            lines.Add($"  {member.Signature}");
            if (member.ExtensionOn is not null) lines.Add($"      call: {Call(member)}");
            if (member.Summary.Length > 0) lines.Add($"      {member.Summary}");
        }

        return lines;
    }

    private List<string> MemberLines(CatalogType owner, string name)
    {
        var lines = new List<string>();
        foreach (CatalogMember member in owner.Members.Where(m => Equal(m.Name, name) || Equal(m.SourceName ?? "", name)))
        {
            string line = member.ExtensionOn is not null
                ? $"{member.Signature}   call: {Call(member)}"
                : member.Name == ".ctor" || member.Kind is "operator" or "conversion"
                    ? $"{owner.DisplayName}.{member.Name}  {member.Signature}"
                    : member.Signature;
            lines.Add(line);
            if (member.Summary.Length > 0) lines.Add($"    {member.Summary}");
        }

        return lines;
    }

    /// <summary>How the call is typed: `ui.Button(UiText label, ButtonOpts o = default)`.</summary>
    private static string Call(CatalogMember member)
    {
        int arguments = member.Signature.IndexOf('(');
        return arguments < 0 ? $"ui.{member.Name}" : $"ui.{member.Name}{member.Signature[arguments..]}";
    }

    /// <summary>
    /// The icon index straight off the constant fields: name, private-use code point, and the
    /// literal to paste. The filter matches anywhere in the name, empty shows all.
    /// </summary>
    internal IReadOnlyList<string> Icons(string? filter) => IconsResult(filter).Lines;

    /// <summary>
    /// The icon index as a result with a match state, so the CLI can answer a filter that hit
    /// nothing with a nonzero exit: "no icon matches" is a mistake worth naming, not a page of
    /// near-miss glyphs ending in exit 0 that a script reads as success.
    /// </summary>
    internal CatalogResult IconsResult(string? filter)
    {
        CatalogType? icons = types.FirstOrDefault(t => Equal(t.Name, "CuiSharp.Icons"));
        if (icons is null)
            return CatalogResult.Answer(CatalogMatch.Unknown, "icons", ["the catalog has no icon constants."]);

        var lines = new List<string>();
        foreach (CatalogMember member in icons.Members.Where(m => m.Kind == "constant"))
        {
            if (filter is { Length: > 0 } && !member.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            string codepoint = member.Value is { Length: > 0 } glyph
                ? $"U+{(int)glyph[0]:X4}"
                : "?";
            string literal = member.Value is { Length: > 0 } g && g.Length == 1
                ? $"\"\\u{(int)g[0]:X4}\""
                : JsonString(member.Value ?? "");
            lines.Add($"{member.Name,-28} {codepoint}  {literal}");
        }

        return lines.Count > 0
            ? CatalogResult.Answer(CatalogMatch.Exact, filter ?? "icons", lines)
            : CatalogResult.Answer(CatalogMatch.Unknown, filter ?? "", [$"no icon matches \"{filter}\". `cui icons` lists them all."]);
    }

    /// <summary>
    /// One JSON string literal, written the same reflection-free way as every other byte of
    /// JSON this class emits — the icon fallback is the only string it ever quotes.
    /// </summary>
    private static string JsonString(string value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) writer.WriteStringValue(value);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private List<string> Candidates(string clean)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogType type in types)
        {
            if (type.DisplayName.Contains(clean, StringComparison.OrdinalIgnoreCase)) names.Add(type.DisplayName);
            foreach (CatalogMember member in type.Members)
                if (member.Name.Contains(clean, StringComparison.OrdinalIgnoreCase)) names.Add(member.Name);
        }

        return [.. names.Take(20)];
    }

    private IEnumerable<string> Suggest(string term) => SuggestWithin(term, AllNames());

    /// <summary>
    /// Nearest names within a given set. Distance is case-insensitive because the lookup is:
    /// a reader who mistyped `linchrt` must still be offered `LineChart`.
    /// </summary>
    private static IEnumerable<string> SuggestWithin(string term, IEnumerable<string> names)
    {
        int budget = Math.Max(2, term.Length / 2);
        string needle = term.ToLowerInvariant();
        var scored = new List<(int Distance, string Name)>();
        foreach (string name in names)
        {
            if (Math.Abs(name.Length - term.Length) > budget) continue;
            int distance = EditDistance(needle, name.ToLowerInvariant());
            if (distance <= budget) scored.Add((distance, name));
        }

        return scored
            .OrderBy(s => s.Distance).ThenBy(s => s.Name, StringComparer.Ordinal)
            .Select(s => s.Name)
            .Distinct()
            .Take(3);
    }

    private IEnumerable<string> AllNames()
    {
        foreach (CatalogType type in types)
        {
            yield return type.DisplayName;
            foreach (CatalogMember member in type.Members) yield return member.Name;
        }
    }

    private static int EditDistance(string a, string b)
    {
        int[] row = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) row[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            int previous = row[0];
            row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int swap = row[j];
                row[j] = Math.Min(row[j] + 1, Math.Min(row[j - 1] + 1, previous + (a[i - 1] == b[j - 1] ? 0 : 1)));
                previous = swap;
            }
        }

        return row[b.Length];
    }
}

internal sealed record CatalogType(string Name, string DisplayName, string Kind, string Summary, IReadOnlyList<CatalogMember> Members);

internal sealed record CatalogMember(
    string Name, string Kind, string Signature, string? ExtensionOn, string? SourceName, string Summary, string? Value,
    IReadOnlyList<CatalogParameter> Parameters, string? Id = null);

internal sealed record CatalogParameter(string Name, string Type, string? Default);

internal enum CatalogMatch
{
    /// <summary>The term named exactly one type or one set of member overloads.</summary>
    Exact,

    /// <summary>Nothing matched exactly, but these names do.</summary>
    Ambiguous,

    /// <summary>Nothing matched; the suggestions are the point.</summary>
    Unknown,
}

internal sealed class CatalogResult
{
    private CatalogResult(CatalogMatch match, string term, IReadOnlyList<string> lines, IReadOnlyList<string> suggestions)
    {
        Match = match;
        Term = term;
        Lines = lines;
        Suggestions = suggestions;
    }

    internal CatalogMatch Match { get; }

    /// <summary>Exit-code ready: exact answers succeed, everything else is a mistake worth naming.</summary>
    internal bool IsExact => Match == CatalogMatch.Exact;

    internal string Term { get; }

    internal IReadOnlyList<string> Lines { get; }

    internal IReadOnlyList<string> Suggestions { get; }

    internal string Render() => string.Join('\n', Lines);

    /// <summary>
    /// The machine-readable answer, written with <see cref="Utf8JsonWriter"/> rather than
    /// <c>JsonSerializer</c>: the writer needs no contract resolver, so <c>--json</c> keeps
    /// answering in a runtime that has disabled reflection-based serialization, which is the
    /// configuration a statically linked, AOT-published host runs under.
    /// </summary>
    internal string RenderJson()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("match", Match.ToString().ToLowerInvariant());
            writer.WriteString("term", Term);
            WriteArray(writer, "lines", Lines);
            WriteArray(writer, "suggestions", Suggestions);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);

        static void WriteArray(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
        {
            writer.WriteStartArray(name);
            foreach (string value in values) writer.WriteStringValue(value);
            writer.WriteEndArray();
        }
    }

    internal static CatalogResult Exact(string term, IReadOnlyList<string> lines) => new(CatalogMatch.Exact, term, lines, []);

    internal static CatalogResult Ambiguous(string term, IReadOnlyList<string> candidates) => new(CatalogMatch.Ambiguous, term, candidates, []);

    /// <summary>A result whose lines are the whole answer — the icon index needs no suggestions of its own.</summary>
    internal static CatalogResult Answer(CatalogMatch match, string term, IReadOnlyList<string> lines) => new(match, term, lines, []);

    /// <summary>
    /// A miss whose explanation was written by the caller: <c>docs</c> answers in its own voice —
    /// nearest topic, nearest section — where the catalog answers in its own.
    /// </summary>
    internal static CatalogResult Missing(string term, IReadOnlyList<string> lines, IReadOnlyList<string> suggestions) =>
        new(CatalogMatch.Unknown, term, lines, suggestions);

    /// <summary>
    /// Unknown is never dead air: the explanation is the answer even when no name is close,
    /// so the caller learns to re-ask rather than reading silence as "no such API".
    /// </summary>
    internal static CatalogResult Unknown(string term, IReadOnlyList<string> suggestions)
    {
        var lines = new List<string>
        {
            suggestions.Count > 0
                ? $"nothing named \"{term}\" is in the CuiSharp catalog."
                : $"nothing named \"{term}\" is in the CuiSharp catalog, and no name is close.",
        };
        if (suggestions.Count > 0) lines.Add("nearest: " + string.Join(", ", suggestions));
        lines.Add("name the owner to narrow it — `api Ui.Button`, `api ChartOpts` — or `cui icons` for glyphs.");
        return new(CatalogMatch.Unknown, term, lines, suggestions);
    }
}
