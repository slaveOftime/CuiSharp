using System.Text.Json;
using System.Text.RegularExpressions;
using CuiSharp.Hosting;

namespace CuiSharp.Tests;

/// <summary>
/// The template ships files that reference the published package, so the version it
/// pins has to move with the package. When it does not, <c>dotnet new cui-app</c>
/// produces a solution that fails on its first build — the failure every user of the
/// template hits, and none of the repository's own builds would notice.
/// </summary>
public class TemplateVersionTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "template");

    [Fact]
    public void TheTemplatePinsTheCurrentPackageVersion()
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "template.json")));

        string? pinned = json.RootElement
            .GetProperty("symbols")
            .GetProperty("CuiSharpVersion")
            .GetProperty("defaultValue")
            .GetString();

        Assert.Equal(ThisPackageVersion(), pinned);
    }

    [Theory]
    [InlineData("app.csproj")]
    [InlineData("tests.csproj")]
    public void BothTemplateProjectsTakeTheStampedVersion(string project)
    {
        string text = File.ReadAllText(Path.Combine(Root, project));

        // The token is what the template engine replaces; a literal version here would
        // silently stop following the package.
        Assert.Contains("__CUISHARP_VERSION__", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// dotnet new evaluates a Condition attribute whose properties it can resolve — a
    /// template symbol or a value like $(OS) — at scaffold time: a false condition
    /// deletes the whole element, a true one loses its guard. An evaluated
    /// <c>'$(CuiSharpVersion)' == ''</c> is how a generated project ended up with a
    /// package reference with no version (NU1015). Conditions on names the engine does
    /// not know (MSBuild-only properties like RuntimeIdentifier) pass through untouched,
    /// so the rule is: never name a template symbol inside a Condition.
    /// </summary>
    [Theory]
    [InlineData("app.csproj")]
    [InlineData("tests.csproj")]
    public void NoConditionReferencesANameTheTemplateEngineEvaluates(string project)
    {
        // Comments are documentation, not attributes: a comment may spell out the
        // condition this test forbids in order to explain it.
        string text = Regex.Replace(File.ReadAllText(Path.Combine(Root, project)), "<!--.*?-->", "", RegexOptions.Singleline);

        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "template.json")));
        string[] symbols = json.RootElement.GetProperty("symbols")
            .EnumerateObject().Select(p => p.Name).ToArray();

        foreach (Match condition in Regex.Matches(text, @"Condition=""([^""]*)"""))
        {
            foreach (Match reference in Regex.Matches(condition.Groups[1].Value, @"\$\(([A-Za-z_][A-Za-z0-9_]*)\)"))
            {
                string name = reference.Groups[1].Value;
                Assert.False(
                    symbols.Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase)),
                    $"Condition '{condition.Groups[1].Value}' names the template symbol '{name}'; " +
                    "dotnet new will evaluate it and rewrite the project.");
            }
        }
    }

    private static string ThisPackageVersion() =>
        typeof(CuiApplication).Assembly.GetName().Version!.ToString(3);
}
