using System.Text.RegularExpressions;
using CuiSharp.Tools;

namespace CuiSharp.Tests;

/// <summary>
/// The reference an assistant reads (<c>llms.txt</c>, generated from the same plans
/// that produce the code) and the guide a person reads (<c>skills/cui-dotnet/SKILL.md</c>,
/// written by hand) have to agree. A guide that calls <c>ui.Spark(...)</c> when the
/// library has <c>ui.Sparkline(...)</c> is worse than no guide: it produces code that
/// does not compile, confidently.
/// </summary>
public class DocumentationDriftTests
{
    /// <summary>Every `ui.Name` the guide mentions must exist, or be declared in the guide itself.</summary>
    [Fact]
    public void EveryCallInTheGuideExistsInTheReference()
    {
        string guide = Read("SKILL.md");
        string reference = Read("llms.txt");

        var missing = new List<string>();
        foreach (string name in Regex.Matches(guide, @"\bui\.([A-Z][A-Za-z0-9]*)").Select(m => m.Groups[1].Value).Distinct())
        {
            if (reference.Contains($"ui.{name}(", StringComparison.Ordinal)) continue;
            if (reference.Contains($"ui.{name}`", StringComparison.Ordinal)) continue;

            // A guide may show its own extension member: `public void Spark(...)`.
            if (Regex.IsMatch(guide, $@"public\s+\S+\s+{name}\s*\(")) continue;

            missing.Add(name);
        }

        Assert.True(missing.Count == 0,
            "SKILL.md calls API that llms.txt does not list: " + string.Join(", ", missing));
    }

    /// <summary>
    /// Every command line the docs hand a reader has to be a line the tool accepts. This is the
    /// test that was missing when the grammar was declared: the guide still said
    /// <c>dotnet cui click Button "Save type"</c> and <c>dotnet cui click editor &gt; Save</c>, and
    /// both had stopped parsing — the first because a label is one quoted argument, the second
    /// because an unquoted <c>&gt;</c> is the shell's redirection, not the query's path separator.
    /// A guide that prints a line the tool rejects turns a one-turn fix into a debugging session.
    /// </summary>
    [Fact]
    public void EveryCommandLineTheDocsShowIsOneTheToolAccepts()
    {
        string[] sources =
        [
            Find("README.md"),
            Find(Path.Combine("skills", "cui-dotnet", "SKILL.md")),
            Find(Path.Combine("skills", "cui-dotnet", "llms.txt")),
            Find(Path.Combine("src", "CuiSharp.Templates", "CuiSharp.App", "AGENTS.md")),
            Find(Path.Combine("src", "CuiSharp.Templates", "CuiSharp.App", "README.md")),
        ];

        var rejected = new List<string>();
        foreach (string source in sources)
        {
            foreach (string raw in File.ReadAllLines(source))
            {
                // A trailing comment is prose for the reader, not part of the command line, and a
                // line has to *be* a command line: one that starts with the command, not a
                // sentence that mentions it in passing.
                string line = Regex.Replace(raw, @"\s+#.*$", "").Trim();
                if (!line.StartsWith("dotnet cui ", StringComparison.Ordinal)) continue;
                if (CountDotnetCui(line) != 1) continue;

                // Placeholders (`<topic>`, `tree|find`), elisions and pipes are not command lines.
                if (line.Contains('<', StringComparison.Ordinal) || line.Contains('|', StringComparison.Ordinal)) continue;
                if (line.Contains('…', StringComparison.Ordinal) || line.Contains("...", StringComparison.Ordinal)) continue;

                string[] argv = Split(line[(line.IndexOf("dotnet cui", StringComparison.Ordinal) + "dotnet cui".Length)..]);
                Grammar.Parsed parsed = Grammar.Parse(argv);
                if (parsed.Errors.Count > 0)
                    rejected.Add($"{Path.GetFileName(source)}: `{line}` -> {string.Join("; ", parsed.Errors)}");
            }
        }

        Assert.True(
            rejected.Count == 0,
            "the docs show command lines the tool rejects:" + Environment.NewLine + "  "
                + string.Join(Environment.NewLine + "  ", rejected));
    }

    private static int CountDotnetCui(string line)
    {
        int count = 0;
        for (int at = line.IndexOf("dotnet cui", StringComparison.Ordinal); at >= 0;
             at = line.IndexOf("dotnet cui", at + 1, StringComparison.Ordinal))
        {
            count++;
            at += "dotnet cui".Length - 1;
        }

        return count;
    }

    /// <summary>
    /// Words the way a shell hands them over: a quoted word stays one word, in single quotes or
    /// double. A query that must name a kind carries its own double quotes, so the single form is the
    /// one a reader can embed: <c>dotnet cui click 'Tab "Markets"'</c>.
    /// </summary>
    private static string[] Split(string text)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        char quote = '\0';

        foreach (char c in text)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    words.Add(current.ToString());
                }

                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (quote != '\0')
        {
            throw new FormatException("Unclosed quote in documented command line.");
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return [.. words];
    }

    [Theory]
    [InlineData("click 'Tab \"Markets\"'", "Tab \"Markets\"")]
    [InlineData("click \"editor > Save\"", "editor > Save")]
    [InlineData("click 'Save user\"s changes'", "Save user\"s changes")]
    [InlineData("click \"Save user's changes\"", "Save user's changes")]
    public void ShellQuotingPreservesTheQueryPassedToTheTool(string command, string query)
    {
        Assert.Equal(new[] { "click", query }, Split(command));
    }

    [Theory]
    [InlineData("click 'Save")]
    [InlineData("click \"Save")]
    public void AnUnclosedShellQuoteDoesNotPassTheDocumentationGate(string command)
    {
        Assert.Throws<FormatException>(() => Split(command));
    }

    private static string Find(string relative)
    {
        string path = Path.Combine(TestPaths.RepositoryRoot, relative);
        Assert.True(File.Exists(path), $"{path} is missing");
        return path;
    }

    /// <summary>The reference is generated, so it must at least mention every option type.</summary>
    [Fact]
    public void EveryOptionTypeInTheReferenceHasAFieldsPage()
    {
        string reference = Read("llms.txt");
        var options = Regex.Matches(reference, @"\b([A-Z][A-Za-z0-9]*Opts)\b")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToArray();

        Assert.NotEmpty(options);
        string fields = Read(Path.Combine("api", "Options.md"));

        var missing = options.Where(o => !fields.Contains($"## `{o}`", StringComparison.Ordinal)).ToArray();
        Assert.True(missing.Length == 0, "option types with no fields documented: " + string.Join(", ", missing));
    }

    /// <summary>
    /// The guide described options as <c>init</c> properties and showed an input result as raw C
    /// flags, while the generator emits public fields and a typed <c>InputResult</c>. Both compile
    /// in a reader's head and neither compiles in a project.
    /// </summary>
    [Fact]
    public void TheGuideDescribesOptionsAndInputResultsAsTheyAre()
    {
        string guide = Read("SKILL.md");

        Assert.DoesNotContain("init` properties", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("CuiFlags.CUI_CHANGED", guide, StringComparison.Ordinal);
        Assert.Contains(".Changed", guide, StringComparison.Ordinal);
        Assert.Contains("## Options are structs of fields", guide, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guide is what tells an assistant which loop to work in, so the fast repeatable one has to
    /// come before the one-shot tool. A guide that opens with `dotnet cui` teaches a process per
    /// command and a check nobody can rerun.
    /// </summary>
    [Fact]
    public void TheGuidePutsTheTestDriverBeforeTheOneShotTool()
    {
        string guide = Read("SKILL.md");

        int workflow = guide.IndexOf("## How to work on a UI", StringComparison.Ordinal);
        Assert.True(workflow >= 0, "the guide needs a workflow section: how to work on a UI");

        string steps = guide[workflow..];
        int driver = steps.IndexOf("TestApp.Start", StringComparison.Ordinal);
        int tool = steps.IndexOf("dotnet cui tree", StringComparison.Ordinal);

        Assert.True(driver >= 0 && tool > driver,
            "the workflow must rank the test driver above `dotnet cui`");
        Assert.Contains("dotnet watch", steps, StringComparison.Ordinal);
    }

    private static string Read(string relative)
    {
        string path = Path.Combine(AppContext.BaseDirectory, relative);
        Assert.True(File.Exists(path), $"missing {relative}; the csproj should copy it next to the tests");
        return File.ReadAllText(path);
    }
}
