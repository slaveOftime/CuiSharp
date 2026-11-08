using System.Text.Json;
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

    private static string ThisPackageVersion() =>
        typeof(CuiApplication).Assembly.GetName().Version!.ToString(3);
}
