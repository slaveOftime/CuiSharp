using System.Text.RegularExpressions;

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
        string fields = Read(Path.Combine("docs", "api", "Options.md"));

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
