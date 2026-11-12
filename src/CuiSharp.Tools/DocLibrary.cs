using System.Reflection;
using System.Text;

namespace CuiSharp.Tools;

/// <summary>
/// The documentation bundle the tool carries: the guide, the reference, the behaviour pages and
/// the generated API pages, read out of this assembly's own resources. One lookup serves an
/// assistant or a person in a directory that holds no app at all — no repository, no NuGet cache,
/// no network, and nothing built or started.
/// </summary>
/// <remarks>
/// A <em>topic</em> is the task an assistant comes with ("test this", "debug this"), answered by a
/// named set of sections of the shipped documents. Topics name sections rather than repeat them, so
/// there is one copy of every sentence in the repository and this file cannot drift from it.
/// </remarks>
internal sealed class DocLibrary
{
    /// <summary>The prefix every bundled document's resource name carries; it is fixed in the csproj.</summary>
    internal const string ResourcePrefix = "CuiSharp.Tools.docs.";

    private const int MaxSearchHits = 8;

    /// <summary>How much of a hit's own text a search answer shows before it points at the read.</summary>
    private const int SnippetLength = 180;

    private readonly List<Doc> docs;
    private readonly List<Topic> topics;

    private DocLibrary(List<Doc> docs, List<Topic> topics)
    {
        this.docs = docs;
        this.topics = topics;
    }

    internal IReadOnlyList<Doc> Docs => docs;

    internal IReadOnlyList<Topic> Topics => topics;

    /// <summary>The version of CuiSharp these pages describe, for a reader deciding whether they apply.</summary>
    internal static string Version
    {
        get
        {
            string? version = typeof(DocLibrary).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(version)) return "unknown";
            int metadata = version.IndexOf('+');
            return metadata < 0 ? version : version[..metadata];
        }
    }

    /// <summary>
    /// Reads every bundled page once. The set is discovered from the assembly's resources rather
    /// than listed here, so a document added to the csproj is searchable without a second edit —
    /// and a document that vanishes is a missing answer instead of a stale one.
    /// </summary>
    internal static DocLibrary LoadEmbedded()
    {
        Assembly assembly = typeof(DocLibrary).Assembly;
        string[] names = [.. assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))];

        if (names.Length == 0)
            throw new ToolException(
                "this build of cui carries no bundled documentation; the CuiSharp.Tools package is "
                + "broken — reinstall it, or read the pages at the repository's skills/cui-dotnet.");

        var docs = new List<Doc>();
        foreach (string name in names)
        {
            string id = DocId(name);
            if (id.Length == 0) continue;

            using Stream? data = assembly.GetManifestResourceStream(name);
            if (data is null) continue;

            using var reader = new StreamReader(data, Encoding.UTF8);
            string text = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
            docs.Add(Doc.Parse(id, FileName(id), text));
        }

        // The narrative pages lead, in the order a reader meets them; the generated API pages
        // follow alphabetically. Search breaks a tie the same way, so a question about a rule
        // answers with the guide before a page that merely mentions it.
        docs.Sort(CompareDocs);
        return new DocLibrary(docs, BundledTopics.Define(docs));
    }

    /// <summary>`CuiSharp.Tools.docs.api.Layout.md` is `api/Layout`; `…docs.SKILL.md` is `SKILL`.</summary>
    private static string DocId(string resourceName)
    {
        string rest = resourceName[ResourcePrefix.Length..];
        int dot = rest.LastIndexOf('.');
        if (dot > 0) rest = rest[..dot];
        if (rest.Length == 0) return "";

        return rest.StartsWith("api.", StringComparison.Ordinal)
            ? "api/" + rest["api.".Length..]
            : rest;
    }

    /// <summary>A document id as a path a person reads: `SKILL.md`, `api/Layout.md`, `llms.txt`.</summary>
    private static string FileName(string id) =>
        id.StartsWith("api/", StringComparison.Ordinal) ? id + ".md"
            : id == "llms" ? "llms.txt"
            : id + ".md";

    private static int CompareDocs(Doc a, Doc b)
    {
        int rank = Rank(a.Id) - Rank(b.Id);
        return rank != 0 ? rank : string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The narrative pages in the order the guide introduces them; everything else is equal.</summary>
    private static int Rank(string id) => id switch
    {
        "SKILL" => 0,
        "llms" => 1,
        "widgets" => 2,
        "widgets-more" => 3,
        "hosting" => 4,
        "native-abi" => 5,
        _ => 10,
    };

    internal Doc? FindDoc(string id)
    {
        // `SKILL.md`, `llms.txt`, `api/Layout.md` are what the index prints, so they are what a
        // reader types back; the id is the same document with the extension dropped.
        string name = id;
        foreach (string extension in new[] { ".md", ".txt" })
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) name = name[..^extension.Length];
        }

        return docs.FirstOrDefault(d => string.Equals(d.Id, name, StringComparison.OrdinalIgnoreCase));
    }

    private Topic? FindTopic(string name) =>
        topics.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The entry point an assistant is told to run first: the topics by task, the documents by
    /// kind, and the two commands that come next. It answers from the bundle, so it is the same
    /// page whether the reader has the repository open or not.
    /// </summary>
    internal CatalogResult List()
    {
        var lines = new List<string>
        {
            $"dotnet cui docs — CuiSharp {Version}, bundled with this tool. Offline: it reads no",
            "project, builds nothing, starts nothing and touches no network.",
            "",
            "Start with the task you have:",
        };

        int width = topics.Max(t => t.Name.Length);
        foreach (Topic topic in topics)
            lines.Add($"  {topic.Name.PadRight(width)}  {topic.Summary}");

        lines.Add("");
        lines.Add("Or read a document whole:");
        int fileWidth = docs.Where(d => !d.Id.StartsWith("api/", StringComparison.Ordinal)).Max(d => d.FileName.Length) + 2;
        foreach (Doc doc in docs)
        {
            if (doc.Id.StartsWith("api/", StringComparison.Ordinal)) continue;
            lines.Add($"  {doc.FileName.PadRight(fileWidth)}  {doc.Summary}");
        }

        int apiPages = docs.Count(d => d.Id.StartsWith("api/", StringComparison.Ordinal));
        if (apiPages > 0)
        {
            lines.Add($"  {"api/<Section>".PadRight(fileWidth)}  one page per generated section, {apiPages} pages; "
                + "`dotnet cui docs read api/Layout`");
        }

        lines.Add("");
        lines.Add("Then:");
        lines.Add("  dotnet cui docs read <topic|doc|doc#section>   one topic, page or section");
        lines.Add("  dotnet cui docs search <words>                 ranked sections, with the read to run next");
        lines.Add("  dotnet cui api <Name>                          exact declarations: `api ChartOpts`");
        lines.Add("  dotnet cui icons [filter]                      the glyph index");
        lines.Add("  dotnet cui tree | find | click | png           inspect and drive a real app");

        return CatalogResult.Exact("docs", lines);
    }

    /// <summary>
    /// Answers one reference: a topic name, a document id, or `doc#section`. A topic is the shape
    /// an instruction file uses ("read the debugging topic"); the other two are what its answer
    /// tells the reader to run next.
    /// </summary>
    internal CatalogResult Read(string reference)
    {
        string clean = reference.Trim();
        if (clean.Length == 0) return CatalogResult.Unknown(reference, []);

        if (FindTopic(clean) is { } topic) return RenderTopic(topic);

        int hash = clean.IndexOf('#');
        string docId = hash < 0 ? clean : clean[..hash];
        string? section = hash < 0 ? null : clean[(hash + 1)..].Trim().TrimStart('#');
        if (section is { Length: 0 }) section = null;

        Doc? doc = FindDoc(docId);
        if (doc is null) return Unresolved(reference, docId);

        if (section is null)
        {
            var whole = new List<string>
            {
                $"{doc.Id}  ·  CuiSharp {Version}  ·  read: dotnet cui docs read {doc.Id}",
                "",
                doc.Text.TrimEnd(),
            };
            return CatalogResult.Exact(reference, whole);
        }

        Section? found = doc.FindSection(section);
        if (found is null)
        {
            List<string> anchors = [.. doc.SectionSuggestions(section)];
            var missing = new List<string> { $"{doc.Id} has no section \"{section}\"." };
            if (anchors.Count > 0) missing.Add("nearest: " + string.Join(", ", anchors));
            missing.Add($"`dotnet cui docs read {doc.Id}` prints the whole page, headings included.");
            return CatalogResult.Missing(reference, missing, anchors);
        }

        var lines = new List<string>
        {
            $"{doc.Id} § {found.Heading}  ·  CuiSharp {Version}"
                + $"  ·  read: dotnet cui docs read {doc.Id}#{found.Anchor}",
            "",
            doc.SubtreeText(found).TrimEnd(),
        };
        return CatalogResult.Exact(reference, lines);
    }

    /// <summary>
    /// Ranks the sections that answer a question. Every word has to appear somewhere in the
    /// section for it to be a hit; when nothing satisfies that, the words are ranked one by one so
    /// a slightly wrong question still gets the right page instead of an empty answer.
    /// </summary>
    internal CatalogResult Search(string query)
    {
        string[] words = [.. query.Split([' ', '\t', ',', '.', '?', '!'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.ToLowerInvariant())
            .Where(w => w.Length > 1 && !IsFiller(w))
            .Distinct()];

        if (words.Length == 0)
            return CatalogResult.Missing(query, ["`docs search` needs a word to look for, not just filler: `dotnet cui docs search dialog keyboard`."], []);

        List<Entry> entries = Index();
        List<Hit> all = Score(entries, words, requireEveryWord: true);
        if (all.Count == 0) all = Score(entries, words, requireEveryWord: false);

        // Rank, then cut: the scan walks the bundle in reading order, so taking the first eight
        // it found would answer with whichever section happens to come first in the guide.
        // OrderBy is stable, and the score already carries the bundled order as its tie-break.
        all = [.. all.OrderByDescending(h => h.Score)];
        if (all.Count == 0)
        {
            List<string> related = [.. NearestTopics(words[0])];
            var missing = new List<string> { $"no bundled section mentions \"{query.Trim()}\"." };
            if (related.Count > 0) missing.Add("these topics do: " + string.Join(", ", related));
            missing.Add("`dotnet cui docs` lists every topic and document; `dotnet cui api <Name>` looks up a declaration.");
            return CatalogResult.Missing(query, missing, related);
        }

        List<Hit> top = [.. all.Take(MaxSearchHits)];
        var lines = new List<string>
        {
            $"{all.Count} section{(all.Count == 1 ? "" : "s")} for \"{query.Trim()}\" in the bundled CuiSharp {Version} docs:",
            "",
        };

        foreach (Hit hit in top)
        {
            lines.Add($"  {hit.Doc.FileName} § {hit.Section.Heading}");
            lines.Add($"    read: dotnet cui docs read {hit.Doc.Id}#{hit.Section.Anchor}");
            if (Snippet(hit) is { Length: > 0 } snippet) lines.Add($"    {snippet}");
            lines.Add("");
        }

        if (all.Count > top.Count)
            lines.Add($"{all.Count - top.Count} more; narrow the words, or read a topic whole (`dotnet cui docs`).");

        // A hit is a section, and a section is not a task. When the task exists, name it: the
        // topic is the answer an assistant actually wants, and the sections above are its index.
        List<string> matchingTopics = [.. topics.Where(t => t.Sections.Any(s => top.Any(h => h.Doc == s.Doc && h.Section == s.Section)))
            .Select(t => t.Name)];
        if (matchingTopics.Count > 0)
            lines.Add("whole topic: " + string.Join(", ", matchingTopics.Select(t => $"dotnet cui docs read {t}")));

        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return CatalogResult.Exact(query, lines);
    }

    /// <summary>
    /// Words that carry no information about which section answers a question. Dropping them is
    /// what lets `await in draw` rank the async rule rather than every section that happens to say
    /// "in": a keyword search over prose is only useful when the keywords are the content ones.
    /// </summary>
    private static bool IsFiller(string word) => word switch
    {
        "a" or "an" or "and" or "are" or "as" or "at" or "be" or "but" or "by" or "can" or "do"
            or "does" or "for" or "from" or "how" or "in" or "into" or "is" or "it" or "its" or "me"
            or "my" or "not" or "of" or "on" or "or" or "our" or "should" or "that" or "the" or "then"
            or "this" or "to" or "use" or "was" or "what" or "when" or "where" or "which" or "why"
            or "with" or "you" => true,
        _ => false,
    };

    /// <summary>
    /// Ranks the sections that answer a question. Words are weighted by how rare they are in the
    /// bundle: `decimate` decides what a question is about, `chart` — which half the pages say —
    /// hardly does. A word in a section's own heading weighs most of all, because a section titled
    /// "Publishing for size" is about size while a page that says "size" once is about something
    /// else entirely.
    /// </summary>
    private static List<Hit> Score(List<Entry> entries, string[] words, bool requireEveryWord)
    {
        Dictionary<string, double> weight = Weigh(entries, words);
        var hits = new List<Hit>();

        foreach (Entry entry in entries)
        {
            double score = 0;
            double missing = 0;
            bool inHeading = false;
            int named = 0;
            foreach (string word in words)
            {
                if (!weight.TryGetValue(word, out double value)) { missing += 2; continue; }

                if (InHeading(entry.Section, word))
                {
                    score += value * 6;
                    inHeading = true;
                    named++;
                }
                else if (entry.Section.Heading.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    // `charts` contains `chart` and is still weaker than the word itself: a plural
                    // in a heading is a passing mention, not the subject.
                    score += value * 1.5;
                    inHeading = true;
                }
                else if (entry.Body.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    score += value * 3;
                }
                else
                {
                    // Bounded by a constant, not by the word's value: a rare word must not veto a
                    // heading that names the question, only weaken it.
                    missing += Math.Min(value, 3);
                }
            }

            // A heading that carries every word of the question is the subject, not a mention:
            // "Publishing for size" answers `aot size` although it never says AOT, and no page
            // that merely happens to use both words outranks it.
            if (named == words.Length) score += 40;

            // Owing a word still costs a little: a heading that says "chart" is not the answer to
            // `decimate chart`, but the section that does decimate is found behind it.
            score -= missing;

            // A section whose heading carries a word is about that word even when the question
            // also used a second word it never says: `dialog keyboard` must reach the Dialog page,
            // which describes its keyboard in prose this line cannot see.
            if (missing > 0 && requireEveryWord && !inHeading) continue;
            if (score <= 0) continue;

            // Break a tie by the order the pages were bundled in, which leads with the guide.
            hits.Add(new Hit(entry.Doc, entry.Section, (int)Math.Round(score * 1000) - Math.Min(entry.Order, 99), words));
        }

        return hits;
    }

    /// <summary>
    /// How much each word says about which section answers a question, from how many sections
    /// could have said it. A word no section uses is dropped, which is what makes the difference
    /// between "this page uses the word once" and "this page is about the word".
    /// </summary>
    private static Dictionary<string, double> Weigh(List<Entry> entries, string[] words)
    {
        var weight = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (string word in words)
        {
            int sections = 0;
            foreach (Entry entry in entries)
            {
                if (entry.Section.Heading.Contains(word, StringComparison.OrdinalIgnoreCase)
                    || entry.Body.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    sections++;
                }
            }

            if (sections > 0) weight[word] = Math.Log(1.0 + ((double)entries.Count / sections));
        }

        return weight;
    }

    /// <summary>
    /// Every section of every page with its own body text, built once per search: a section is
    /// asked about once per word while ranking and again while weighing, and re-slicing the page
    /// for each question would read the whole bundle several times over.
    /// </summary>
    private List<Entry> Index()
    {
        var entries = new List<Entry>();
        for (int order = 0; order < docs.Count; order++)
        {
            Doc doc = docs[order];
            foreach (Section section in doc.Sections)
                entries.Add(new Entry(doc, section, doc.BodyText(section), order));
        }

        return entries;
    }

    private readonly record struct Entry(Doc Doc, Section Section, string Body, int Order);

    /// <summary>
    /// Whether a heading names a word: the word itself, or the word a heading inflects —
    /// `publish` is what "Publishing for size" is about, and refusing the prefix would rank the
    /// guide's table of contents above the page it lists.
    /// </summary>
    private static bool InHeading(Section section, string word)
    {
        foreach (string token in section.HeadingWords)
        {
            if (string.Equals(token, word, StringComparison.Ordinal)) return true;
            if (word.Length >= 4 && token.StartsWith(word, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>
    /// The line that best answers the question: the one carrying the most of its words. A section
    /// is often a table or a list where only one row is the answer, and showing the first line
    /// instead would send a reader to a paragraph that never mentions what they asked about.
    /// </summary>
    private static string Snippet(Hit hit)
    {
        string? best = null;
        int bestHits = 0;
        string? first = null;

        foreach (string raw in hit.Doc.BodyText(hit.Section).Split('\n'))
        {
            string line = Collapse(raw);
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("```", StringComparison.Ordinal)) continue;
            if (line.StartsWith('|') && line.Replace("|", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).Trim().Length == 0)
                continue;

            first ??= line;
            int hits = hit.Words.Count(w => line.Contains(w, StringComparison.OrdinalIgnoreCase));
            if (hits > bestHits)
            {
                bestHits = hits;
                best = line;
            }
        }

        return Clip(best ?? first ?? "");
    }

    private static string Collapse(string line) =>
        string.Join(' ', line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));

    private static string Clip(string line) =>
        line.Length <= SnippetLength ? line : line[..(SnippetLength - 1)].TrimEnd() + "…";

    private CatalogResult RenderTopic(Topic topic)
    {
        var lines = new List<string>
        {
            $"Topic: {topic.Name} — {topic.Summary}",
            $"Read: dotnet cui docs read {topic.Name}  ·  CuiSharp {Version}  ·  offline",
        };

        if (topic.Sections.Count > 1 || !string.IsNullOrEmpty(topic.Next))
            lines.Add($"Sources: {string.Join(", ", topic.Sections.Select(s => s.ToString()))}");
        lines.Add("");

        foreach (Source source in topic.Sections)
        {
            lines.Add(source.Title);
            lines.Add("");
            lines.Add(source.Text.TrimEnd());
            lines.Add("");
        }

        if (!string.IsNullOrEmpty(topic.Next)) lines.Add(topic.Next);
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return CatalogResult.Exact(topic.Name, lines);
    }

    /// <summary>
    /// An unresolved topic is never dead air: the topics and documents that are close come back,
    /// so the next call is one edit away and nothing has to be guessed from silence.
    /// </summary>
    private CatalogResult Unresolved(string reference, string term)
    {
        var suggestions = new List<string>();
        foreach (Topic topic in topics)
            if (Close(term, topic.Name) && !suggestions.Contains(topic.Name, StringComparer.OrdinalIgnoreCase))
                suggestions.Add(topic.Name);
        foreach (Doc doc in docs)
            if (Close(term, doc.Id) && !suggestions.Contains(doc.Id, StringComparer.OrdinalIgnoreCase))
                suggestions.Add(doc.Id);

        var lines = new List<string>
        {
            suggestions.Count > 0
                ? $"nothing bundled with CuiSharp is called \"{reference.Trim()}\"."
                : $"nothing bundled with CuiSharp is called \"{reference.Trim()}\", and no topic or page is close.",
        };

        if (suggestions.Count > 0) lines.Add("nearest: " + string.Join(", ", suggestions.Take(6)));
        lines.Add("`dotnet cui docs` lists every topic and document; `dotnet cui docs search <words>` finds a section.");
        return CatalogResult.Missing(reference, lines, [.. suggestions.Take(6)]);
    }

    /// <summary>A topic typed roughly still resolves: `debuging`, `test`, `perf`.</summary>
    private static bool Close(string typed, string name)
    {
        if (name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return true;
        if (typed.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return true;

        int budget = Math.Max(1, typed.Length / 3);
        return Math.Abs(name.Length - typed.Length) <= budget
            && EditDistance(typed.ToLowerInvariant(), name.ToLowerInvariant()) <= budget;
    }

    private IEnumerable<string> NearestTopics(string word)
    {
        foreach (Topic topic in topics)
            if (topic.Summary.Contains(word, StringComparison.OrdinalIgnoreCase)) yield return topic.Name;
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

    private readonly record struct Hit(Doc Doc, Section Section, int Score, string[] Words);
}

/// <summary>One bundled Markdown page, split into the sections a reader can be pointed at.</summary>
internal sealed class Doc
{
    private readonly string[] lines;

    private Doc(string id, string fileName, string title, string summary, string text, string[] lines, List<Section> sections)
    {
        Id = id;
        FileName = fileName;
        Title = title;
        Summary = summary;
        Text = text;
        this.lines = lines;
        Sections = sections;
    }

    /// <summary>`SKILL`, `llms`, `widgets`, `api/Layout` — what <c>docs read</c> takes.</summary>
    internal string Id { get; }

    internal string FileName { get; }

    internal string Title { get; }

    /// <summary>The first sentence of the page or its H1, for the index and the topic sources.</summary>
    internal string Summary { get; }

    internal string Text { get; }

    internal IReadOnlyList<Section> Sections { get; }

    /// <summary>The text directly under a heading, down to the next heading of any level.</summary>
    internal string BodyText(Section section) => Join(section.BodyStart, section.BodyEnd);

    /// <summary>The heading and everything under it, down to the next heading of its own level or above.</summary>
    internal string SubtreeText(Section section) => Join(section.Start, section.End);

    private string Join(int start, int end)
    {
        if (start >= end) return "";
        var text = new StringBuilder();
        for (int i = start; i < end; i++)
        {
            if (i > start) text.Append('\n');
            text.Append(lines[i]);
        }

        return text.ToString();
    }

    /// <summary>
    /// Finds a section by its anchor or by its heading as typed, so `SKILL#Pitfalls`,
    /// `SKILL#pitfalls` and `SKILL#the-one-rule` are the same answer.
    /// </summary>
    internal Section? FindSection(string reference)
    {
        string wanted = reference.Trim();
        foreach (Section section in Sections)
        {
            if (string.Equals(section.Anchor, wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(section.Heading, wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Section.Slug(section.Heading), Section.Slug(wanted), StringComparison.Ordinal))
            {
                return section;
            }
        }

        // A partial heading is a reasonable way to ask: `SKILL#pitfall`, `SKILL#driving`.
        foreach (Section section in Sections)
            if (section.Heading.Contains(wanted, StringComparison.OrdinalIgnoreCase)) return section;

        return null;
    }

    internal IEnumerable<string> SectionSuggestions(string reference)
    {
        string typed = reference.Trim();
        return Sections
            .Where(s => s.Heading.Contains(typed, StringComparison.OrdinalIgnoreCase)
                || s.Anchor.Contains(typed, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Anchor)
            .Take(5);
    }

    /// <summary>
    /// Splits the page at its ATX headings, ignoring anything inside a fence: a `#` line in a C#
    /// sample or a shell transcript is not a section, and treating it as one would hand a reader
    /// half a code block.
    /// </summary>
    internal static Doc Parse(string id, string fileName, string text)
    {
        string[] lines = text.Split('\n');
        var headings = new List<(int Line, int Level, string Heading)>();
        bool fenced = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                fenced = !fenced;
                continue;
            }

            if (fenced || !TryHeading(line, out int level, out string heading)) continue;
            headings.Add((i, level, heading));
        }

        var sections = new List<Section>();
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        for (int k = 0; k < headings.Count; k++)
        {
            (int start, int level, string heading) = headings[k];
            int bodyEnd = k + 1 < headings.Count ? headings[k + 1].Line : lines.Length;

            int end = lines.Length;
            for (int j = k + 1; j < headings.Count; j++)
            {
                if (headings[j].Level > level) continue;
                end = headings[j].Line;
                break;
            }

            string anchor = Unique(Section.Slug(heading), anchors);
            sections.Add(new Section(anchor, heading, level, start, start + 1, bodyEnd, end));
        }

        string title = sections.FirstOrDefault(s => s.Level == 1)?.Heading ?? id;
        return new Doc(id, fileName, title, Summarize(text, title), text, lines, sections);

        static string Unique(string anchor, HashSet<string> taken)
        {
            if (taken.Add(anchor)) return anchor;
            for (int n = 1; ; n++)
            {
                string candidate = anchor + "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (taken.Add(candidate)) return candidate;
            }
        }
    }

    private static bool TryHeading(string line, out int level, out string heading)
    {
        level = 0;
        heading = "";
        int at = 0;
        while (at < line.Length && at < 6 && line[at] == '#') at++;
        if (at == 0 || at >= line.Length || line[at] != ' ') return false;

        level = at;
        heading = line[at..].Trim();
        return heading.Length > 0;
    }

    /// <summary>
    /// The page's own one-liner: its blockquote lede, or else the first prose paragraph under the
    /// title. Every bundled page carries one, so the index reads like a table of contents rather
    /// than a list of file names.
    /// </summary>
    private static string Summarize(string text, string title)
    {
        string[] lines = text.Split('\n');
        var quoted = new List<string>();
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith('>'))
            {
                quoted.Add(trimmed.TrimStart('>').Trim());
                continue;
            }

            if (quoted.Count > 0) break;
        }

        string summary = quoted.Count > 0
            ? string.Join(' ', quoted.Where(q => q.Length > 0))
            : "";

        if (summary.Length == 0)
        {
            bool pastTitle = false;
            var prose = new List<string>();
            bool fenced = false;
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("```", StringComparison.Ordinal)) { fenced = !fenced; continue; }
                if (fenced) continue;
                if (trimmed.StartsWith('#')) { if (pastTitle) break; pastTitle = true; continue; }
                if (!pastTitle || trimmed.Length == 0) { if (prose.Count > 0) break; continue; }
                prose.Add(trimmed);
            }

            summary = string.Join(' ', prose);
        }

        if (summary.Length == 0) summary = title;
        int stop = summary.IndexOf(". ", StringComparison.Ordinal);
        if (stop > 20) summary = summary[..(stop + 1)];
        return summary.Length <= 120 ? summary : summary[..119].TrimEnd() + "…";
    }
}

/// <summary>One heading in a page, with the line spans a snippet and a subtree read need.</summary>
internal sealed record Section(
    string Anchor, string Heading, int Level, int Start, int BodyStart, int BodyEnd, int End)
{
    private string[]? words;

    /// <summary>The heading's own words, so `pitfalls` beats a page that merely says "pitfall".</summary>
    internal string[] HeadingWords => words ??= [.. Heading
        .Split([' ', '`', '\'', '"', ',', '.', ':', '(', ')', '/', '·'], StringSplitOptions.RemoveEmptyEntries)
        .Select(w => w.ToLowerInvariant())];

    public override string ToString() => Heading;

    /// <summary>GitHub's anchor rule, so a link written for GitHub resolves here too.</summary>
    internal static string Slug(string heading)
    {
        var slug = new StringBuilder(heading.Length);
        foreach (char c in heading.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) && c < 128) slug.Append(c);
            else if (c is ' ' or '-' or '_') slug.Append('-');
        }

        string text = slug.ToString();
        while (text.Contains("--", StringComparison.Ordinal)) text = text.Replace("--", "-", StringComparison.Ordinal);
        return text.Trim('-');
    }
}

/// <summary>
/// One part of one document, as a topic names it: a section, or the whole page when the topic is
/// the page itself (`llms`, `widgets`).
/// </summary>
internal sealed record Source(Doc Doc, Section? Section)
{
    internal string Title => Section is null ? Doc.FileName : $"{Doc.FileName} § {Section.Heading}";

    internal string Text => Section is null ? Doc.Text : Doc.SubtreeText(Section);

    public override string ToString() => Title;
}

/// <summary>
/// The tasks an assistant arrives with, each answered by sections that already exist in the
/// bundled pages. Naming a section instead of copying it is what keeps one copy of every rule:
/// a topic cannot say something the guide does not.
/// </summary>
internal static class BundledTopics
{
    /// <summary>
    /// A topic is a name, the task it answers, the sections it is made of and what to do after
    /// reading it. The sections are `doc#anchor`; every one is checked by a test, so a heading
    /// renamed in the guide fails the build instead of leaving a dead pointer in the tool.
    /// </summary>
    private static readonly (string Name, string Summary, string[] Sections, string Next)[] Definitions =
    [
        ("getting-started",
            "install, draw a first frame, choose a file-based or hosted app",
            ["SKILL#a-whole-app", "SKILL#the-one-rule", "SKILL#cheat-sheet"],
            "Next: `dotnet cui docs read writing-ui`."),

        ("writing-ui",
            "the rules that make a frame: scopes, sizing, options, state, ids, pitfalls",
            ["SKILL#frame-local-types-and-text", "SKILL#containers-are-using-scopes", "SKILL#sizing",
             "SKILL#options-are-structs-of-fields", "SKILL#colours-come-from-the-theme",
             "SKILL#state-goes-in-ref", "SKILL#containers-that-may-be-closed", "SKILL#pitfalls"],
            "Next: `dotnet cui docs read testing`, then look a signature up with `dotnet cui api <Name>`."),

        ("custom-controls",
            "draw your own control as an extension member, and the canvas primitives",
            ["SKILL#custom-controls"],
            "Next: `dotnet cui docs search canvas` for the primitives a control draws with."),

        ("hosting",
            "components, dependency injection, Mutation, and who owns a redraw",
            ["hosting", "SKILL#async-work"],
            "Next: `dotnet cui docs read testing`."),

        ("testing",
            "drive a real component with no window: frames on demand, clicks, pixels, assertions",
            ["SKILL#testing-without-a-window", "SKILL#how-to-work-on-a-ui", "hosting#testing-a-hosted-app"],
            "Every fix that matters ends here: a reproduction in the test suite, not a shell history."),

        ("debugging",
            "find out why the app does something: inspect a frame, interact, capture, pin a test",
            ["SKILL#how-to-work-on-a-ui", "SKILL#driving-a-running-app"],
            "A `tree`, a `click` or a `png` is evidence, not a fix: end with a test that fails without the change."),

        ("layout",
            "sizing, nesting, alignment and the rectangles a container actually got",
            ["SKILL#sizing", "SKILL#containers-are-using-scopes", "SKILL#containers-that-may-be-closed", "api/Layout"],
            "Then `dotnet cui layout` prints the rectangles, the observed union bounds and overflow."),

        ("live-debugging",
            "attach to a window a person is watching, on Windows, and what that cannot promise",
            ["SKILL#driving-a-running-app"],
            "The opt-in, the discovery and the cancellation rules are the same page's bullets — read them before scripting it."),

        ("performance",
            "0 B per frame, virtualized rows, decimated charts, and the numbers that prove it",
            ["SKILL#long-series-and-live-data", "SKILL#frame-numbers", "SKILL#pitfalls"],
            "Measure before claiming: `CUI_TRACE=trace.txt` writes the frame costs a WinExe cannot print."),

        ("theming",
            "the theme, the accent, the design tokens and the rules that stop a UI looking generic",
            ["SKILL#theming", "SKILL#colours-come-from-the-theme", "SKILL#design-rules"],
            "Next: `dotnet cui api ThemeStyle` for every writable token."),

        ("translation",
            "the one name table the date widgets read, and how a surface owns it",
            ["SKILL#translating-the-date-names"],
            "Text your font does not cover draws nothing: check `UseSystemFontFallback` on the same page."),

        ("publishing",
            "one AOT executable, the four size switches, and what each gate keeps",
            ["SKILL#publishing-for-size", "native-abi"],
            "A published app drops the headless channel by default: publish with `-p:CuiSharpHeadless=true` before pointing `--app` at it."),

        ("signatures",
            "every declaration, one line each, generated from the headers",
            ["llms"],
            "One name at a time is faster: `dotnet cui api ChartOpts`."),

        ("widgets",
            "what each component does, its keyboard, and what its return value means",
            ["widgets"],
            "More components, grouped by task: `dotnet cui docs read widgets-more`."),
    ];

    internal static List<Topic> Define(IReadOnlyList<Doc> docs)
    {
        var topics = new List<Topic>();
        foreach ((string name, string summary, string[] sections, string next) in Definitions)
        {
            var sources = new List<Source>();
            foreach (string reference in sections)
            {
                Source? source = Resolve(docs, reference);
                // A definition that no longer resolves is dropped here and caught by the test
                // that walks every topic; failing the build there beats a runtime surprise.
                if (source is not null) sources.Add(source);
            }

            if (sources.Count > 0) topics.Add(new Topic(name, summary, sources, next));
        }

        return topics;
    }

    private static Source? Resolve(IReadOnlyList<Doc> docs, string reference)
    {
        int hash = reference.IndexOf('#');
        string docId = hash < 0 ? reference : reference[..hash];
        Doc? doc = docs.FirstOrDefault(d => string.Equals(d.Id, docId, StringComparison.OrdinalIgnoreCase));
        if (doc is null) return null;
        // A document with no section named is the whole document: a topic that says `llms`
        // wants every signature, not the first heading of the page.
        if (hash < 0) return new Source(doc, null);

        return doc.FindSection(reference[(hash + 1)..]) is { } section ? new Source(doc, section) : null;
    }
}

/// <summary>A named task and the shipped sections that answer it.</summary>
internal sealed record Topic(string Name, string Summary, IReadOnlyList<Source> Sections, string Next);
