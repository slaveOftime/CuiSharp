using System.Text.Json;
using CuiSharp.Tools;
using DocSource = CuiSharp.Tools.Source;

namespace CuiSharp.Tests;

/// <summary>
/// <c>dotnet cui docs</c>: the guide, the reference and the behaviour pages answered from bytes
/// inside the tool. Like the catalog commands, every call here passes <c>--project</c> pointing at
/// a directory that does not exist — an ordinary command would fail there with "No app found", so
/// an answer with exit 0 is itself the proof that reading the documentation discovered no project,
/// built nothing, started no process and opened no socket.
/// </summary>
public class DocsCommandTests
{
    private static readonly string NoProjectHere = Path.Combine(Path.GetTempPath(), "cui-docs-offline-" + Guid.NewGuid());

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = Cli.Run([.. args, "--project", NoProjectHere], output, error);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public void TheIndexNamesTheTopicsAndTheDocumentsAndStartsNoAppToDoIt()
    {
        (int exit, string output, _) = Run("docs");

        Assert.Equal(0, exit);
        Assert.Contains("dotnet cui docs read", output, StringComparison.Ordinal);
        // The tasks an assistant arrives with, each a name it can pass back to `docs read`.
        foreach (string topic in new[] { "getting-started", "writing-ui", "testing", "debugging", "layout", "publishing" })
            Assert.Contains(topic, output, StringComparison.Ordinal);

        // The pages themselves, by the name a reader types.
        foreach (string document in new[] { "SKILL.md", "llms.txt", "widgets.md", "hosting.md", "native-abi.md" })
            Assert.Contains(document, output, StringComparison.Ordinal);

        // A page it carries must be reachable, or the index is advertising something absent.
        Assert.Contains("api/Layout", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The topics are pointers into the shipped pages, so a heading renamed in the guide leaves a
    /// topic that resolves to nothing. That is the drift this test exists to catch — and it is the
    /// only reason a topic can be broken without anybody noticing, because the guide itself still
    /// reads correctly.
    /// </summary>
    [Fact]
    public void EveryTopicResolvesToSectionsOfABundledPage()
    {
        DocLibrary docs = DocLibrary.LoadEmbedded();

        Assert.NotEmpty(docs.Topics);
        foreach (Topic topic in docs.Topics)
        {
            Assert.True(topic.Sections.Count > 0, $"topic \"{topic.Name}\" resolves to no section at all");
            foreach (DocSource source in topic.Sections)
            {
                Assert.Contains(source.Doc, docs.Docs);
                if (source.Section is { } section)
                {
                    Assert.Contains(section, source.Doc.Sections);
                    Assert.True(source.Text.Length > 0, $"{topic.Name} -> {source.Title} is empty");
                }
            }

            // And it reads: the answer a caller gets is the topic, not a miss with a suggestion.
            (int exit, string output, string error) = Run("docs", "read", topic.Name);
            Assert.True(exit == 0, $"`docs read {topic.Name}` exited {exit}: {error}");
            Assert.StartsWith("Topic: " + topic.Name, output, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The tool answers from the same files the CuiSharp package ships, embedded rather than
    /// copied. This pins that the bytes inside the tool are the checked-in ones, so an edit to the
    /// guide cannot leave the tool answering from last release's snapshot.
    /// </summary>
    [Theory]
    [InlineData("CuiSharp.Tools.docs.SKILL.md", "SKILL.md")]
    [InlineData("CuiSharp.Tools.docs.llms.txt", "llms.txt")]
    [InlineData("CuiSharp.Tools.docs.widgets.md", "widgets.md")]
    [InlineData("CuiSharp.Tools.docs.widgets-more.md", "widgets-more.md")]
    [InlineData("CuiSharp.Tools.docs.hosting.md", "hosting.md")]
    [InlineData("CuiSharp.Tools.docs.native-abi.md", "native-abi.md")]
    [InlineData("CuiSharp.Tools.docs.api.Layout.md", "api/Layout.md")]
    public void ABundledPageIsTheCheckedInFile(string resource, string relative)
    {
        using Stream? data = typeof(Cli).Assembly.GetManifestResourceStream(resource);
        Assert.NotNull(data);
        string embedded = new StreamReader(data!).ReadToEnd();

        string checkedIn = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "skills", "cui-dotnet", relative));
        Assert.Equal(checkedIn.Replace("\r\n", "\n"), embedded.Replace("\r\n", "\n"));
    }

    [Fact]
    public void AWholeDocumentIsReadByTheNameTheIndexPrints()
    {
        (int exit, string output, _) = Run("docs", "read", "SKILL.md");

        Assert.Equal(0, exit);
        // The guide itself, not a description of it: the first rule a reader has to obey.
        Assert.Contains("Rebuild the entire UI every frame", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// One section, not the page: a reader who asks for the pitfalls must not be handed forty
    /// thousand characters of guide and left to find them.
    /// </summary>
    [Fact]
    public void ASectionReadIsThatSectionAndStopsAtTheNextHeading()
    {
        (int exit, string pitfalls, _) = Run("docs", "read", "SKILL#pitfalls");

        Assert.Equal(0, exit);
        Assert.Contains("Focus and scroll jump between rows", pitfalls, StringComparison.Ordinal);
        Assert.DoesNotContain("## The one rule", pitfalls, StringComparison.Ordinal);
        Assert.DoesNotContain("## Async work", pitfalls, StringComparison.Ordinal);
        Assert.DoesNotContain("## Publishing for size", pitfalls, StringComparison.Ordinal);

        // The heading as typed, the anchor, and a prefix of it are the same request.
        foreach (string reference in new[] { "SKILL#Pitfalls", "SKILL#pitfalls", "SKILL#PITFALL" })
        {
            (int sameExit, string same, string error) = Run("docs", "read", reference);
            Assert.True(sameExit == 0, $"`docs read {reference}` exited {sameExit}: {error}");
            Assert.Contains("Focus and scroll jump between rows", same, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A search that ranks the guide's table of contents above the page it lists is a search a
    /// reader stops trusting. The section whose own heading names the question comes first.
    /// </summary>
    [Fact]
    public void SearchRanksTheSectionWhoseHeadingNamesTheQuestionFirst()
    {
        (int exit, string output, _) = Run("docs", "search", "publish size");

        Assert.Equal(0, exit);
        Assert.Equal("SKILL.md § Publishing for size", RankedHeadings(output)[0]);

        (exit, output, _) = Run("docs", "search", "pitfalls");
        Assert.Equal(0, exit);
        Assert.Equal("SKILL.md § Pitfalls", RankedHeadings(output)[0]);
    }

    /// <summary>
    /// A question can use a word the answering page never does. `aot size` is answered by the
    /// publish page, which is about size and describes no AOT switch; finding it is the difference
    /// between a search that works and one that only matches strings.
    /// </summary>
    [Fact]
    public void SearchFindsThePageThatAnswersAQuestionItDoesNotName()
    {
        (int exit, string output, _) = Run("docs", "search", "aot size");

        Assert.Equal(0, exit);
        Assert.Contains("SKILL.md § Publishing for size", output, StringComparison.Ordinal);
        Assert.Contains("dotnet cui docs read SKILL#publishing-for-size", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every hit carries the command that opens it, so a search answer is never a dead end: an
    /// assistant can read the section without guessing an anchor.
    /// </summary>
    [Fact]
    public void EverySearchHitCarriesTheReadThatOpensIt()
    {
        (int exit, string output, _) = Run("docs", "search", "decimate chart");

        Assert.Equal(0, exit);
        Assert.Contains("read: dotnet cui docs read SKILL#long-series-and-live-data", output, StringComparison.Ordinal);
        // A snippet of the answer, not just the heading.
        Assert.Contains("decimate", output, StringComparison.OrdinalIgnoreCase);

        int reads = CountOccurrences(output, "    read: dotnet cui docs read ");
        int headings = CountOccurrences(output, "  § ") + CountOccurrences(output, "\n  ");
        Assert.True(reads > 0 && reads <= headings + 1, $"every hit needs a read line; saw {reads} for\n{output}");
    }

    /// <summary>
    /// A hit is a section, and a section is not a task. When the bundled topics cover the question,
    /// the answer names them, because that is what an instruction file tells an assistant to read.
    /// </summary>
    [Fact]
    public void ASearchAnswerNamesTheTopicThatCoversTheQuestion()
    {
        (int exit, string output, _) = Run("docs", "search", "decimate chart");

        Assert.Equal(0, exit);
        Assert.Contains("whole topic:", output, StringComparison.Ordinal);
        Assert.Contains("dotnet cui docs read performance", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The debugging topic is the page an instruction file points at instead of restating the
    /// workflow, so it has to carry the three ways to reach an app and the rule that evidence is
    /// not a fix. Asserting the shipped text is the point: a topic that quietly lost a section
    /// would still resolve and still read, and still mislead.
    /// </summary>
    [Fact]
    public void TheDebuggingTopicCarriesTheWorkflowItIsPointedAtFor()
    {
        (int exit, string debugging, _) = Run("docs", "read", "debugging");

        Assert.Equal(0, exit);
        Assert.Contains("SKILL.md § How to work on a UI", debugging, StringComparison.Ordinal);
        Assert.Contains("SKILL.md § Driving a running app", debugging, StringComparison.Ordinal);
        // Headless one-shots, one process for a script, and a live window a person is watching.
        Assert.Contains("--stdin", debugging, StringComparison.Ordinal);
        Assert.Contains("--attach", debugging, StringComparison.Ordinal);
        Assert.Contains("one process", debugging, StringComparison.Ordinal);
        // And the rule that keeps a debugging session from being the only evidence.
        Assert.Contains("belongs in the test suite", debugging, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownTopicNamesTheNearestOneAndSaysItIsMissing()
    {
        (int exit, string output, _) = Run("docs", "read", "debuging");

        Assert.Equal(1, exit);
        Assert.Contains("nothing bundled with CuiSharp is called \"debuging\"", output, StringComparison.Ordinal);
        Assert.Contains("debugging", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ASectionThatDoesNotExistSendsTheReaderToThePage()
    {
        (int exit, string output, _) = Run("docs", "read", "SKILL#nosuchsection");

        Assert.Equal(1, exit);
        Assert.Contains("SKILL has no section \"nosuchsection\"", output, StringComparison.Ordinal);
        Assert.Contains("dotnet cui docs read SKILL", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AQuestionNothingAnswersSaysSoInTheToolsOwnVoice()
    {
        (int exit, string output, _) = Run("docs", "search", "zzqqxxvv");

        Assert.Equal(1, exit);
        Assert.Contains("no bundled section mentions \"zzqqxxvv\"", output, StringComparison.Ordinal);
        Assert.DoesNotContain("is in the CuiSharp catalog", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AQuestionMadeOnlyOfFillerWordsAsksForARealOne()
    {
        (int exit, string output, _) = Run("docs", "search", "the and of");

        Assert.Equal(1, exit);
        Assert.Contains("needs a word to look for", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadAndSearchWithoutTheirArgumentAreUsageErrors()
    {
        (int exit, _, string error) = Run("docs", "read");
        Assert.Equal(2, exit);
        Assert.Contains("`docs read` needs a topic", error, StringComparison.Ordinal);

        (exit, _, error) = Run("docs", "search");
        Assert.Equal(2, exit);
        Assert.Contains("`docs search` needs words", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// An assistant that drops the verb still gets the page: `docs debugging` is the same request
    /// as `docs read debugging`, and correcting the grammar instead would be a wasted turn.
    /// </summary>
    [Fact]
    public void TheVerbMayBeDroppedEntirely()
    {
        (int exit, string output, _) = Run("docs", "testing");

        Assert.Equal(0, exit);
        Assert.StartsWith("Topic: testing", output, StringComparison.Ordinal);
        Assert.Contains("TestApp.Start", output, StringComparison.Ordinal);
    }

    [Fact]
    public void DocsAreMachineReadableWithJson()
    {
        (int exit, string output, _) = Run("docs", "search", "decimate", "--json");

        Assert.Equal(0, exit);
        using JsonDocument json = JsonDocument.Parse(output);
        Assert.Equal("exact", json.RootElement.GetProperty("match").GetString());
        Assert.Equal("decimate", json.RootElement.GetProperty("term").GetString());
        JsonElement lines = json.RootElement.GetProperty("lines");
        Assert.True(lines.GetArrayLength() > 0);
        Assert.Contains("docs read SKILL#", lines.GetRawText(), StringComparison.Ordinal);

        (exit, output, _) = Run("docs", "--json");
        Assert.Equal(0, exit);
        using JsonDocument index = JsonDocument.Parse(output);
        Assert.Equal("exact", index.RootElement.GetProperty("match").GetString());

        (exit, output, _) = Run("docs", "read", "nosuchtopic", "--json");
        Assert.Equal(1, exit);
        using JsonDocument missing = JsonDocument.Parse(output);
        Assert.Equal("unknown", missing.RootElement.GetProperty("match").GetString());
        Assert.True(missing.RootElement.GetProperty("suggestions").GetArrayLength() >= 0);
    }

    [Fact]
    public void AnApiPageIsABundledDocumentLikeAnyOther()
    {
        (int exit, string output, _) = Run("docs", "read", "api/Layout");

        Assert.Equal(0, exit);
        Assert.Contains("api/Layout", output, StringComparison.Ordinal);
        Assert.Contains("Generated by", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two paths can read the bundle, and only one of them is a real answer. Pinning that the
    /// reader parses sections rather than printing the file is what makes `docs read doc#section`
    /// a section instead of the whole page with a heading somewhere in it.
    /// </summary>
    [Fact]
    public void TheReaderSplitsPagesAtHeadingsAndIgnoresFences()
    {
        DocLibrary docs = DocLibrary.LoadEmbedded();
        Doc skill = docs.Docs.Single(d => d.Id == "SKILL");

        Assert.True(skill.Sections.Count > 10, "the guide has sections");
        Section section = Assert.Single(skill.Sections, s => s.Heading == "Pitfalls");
        Assert.Equal("pitfalls", section.Anchor);

        // Every anchor is unique within its page, or `docs read doc#anchor` could open the wrong
        // section than the one a search answer printed.
        foreach (Doc doc in docs.Docs)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Section s in doc.Sections)
                Assert.True(seen.Add(s.Anchor), $"{doc.Id} repeats the anchor \"{s.Anchor}\"");
        }

        // A heading inside a fence is sample code, not a section: the C# a page shows is full of
        // `#` and `##` and none of it may become something `docs read` can open.
        Assert.DoesNotContain(skill.Sections, s => s.Heading.Contains("region", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(skill.Sections, s => s.Anchor.Length == 0);
    }

    [Fact]
    public void TheUsageScreenListsTheDocumentationCommands()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = Cli.Run(["docs", "--help"], output, error);

        Assert.Equal(0, exit);
        string usage = output.ToString();
        Assert.Contains("docs read <what>", usage, StringComparison.Ordinal);
        Assert.Contains("docs search <words>", usage, StringComparison.Ordinal);
    }

    /// <summary>The hit headings of a search answer, in the order the answer ranked them.</summary>
    private static string[] RankedHeadings(string output) =>
        [.. output.Split('\n')
            .Where(line => line.StartsWith("  ", StringComparison.Ordinal) && line.Contains(" § ", StringComparison.Ordinal))
            .Select(line => line.Trim())];

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        for (int at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
