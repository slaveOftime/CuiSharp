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

    private static string Read(string relative)
    {
        string path = Path.Combine(AppContext.BaseDirectory, relative);
        Assert.True(File.Exists(path), $"missing {relative}; the csproj should copy it next to the tests");
        return File.ReadAllText(path);
    }
}
