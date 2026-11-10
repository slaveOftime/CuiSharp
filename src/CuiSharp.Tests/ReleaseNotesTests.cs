using System.Text.RegularExpressions;

namespace CuiSharp.Tests;

/// <summary>
/// Release notes used to be written twice — a section in <c>CHANGELOG.md</c> and a separate page
/// pasted into the GitHub release — and the changelog lost the race: `v0.1.0` was tagged and
/// published while its section still said "unreleased". There is one source now, and these tests
/// keep it that way.
/// </summary>
public class ReleaseNotesTests
{
    [Fact]
    public void TheVersionBeingBuiltHasADatedSection()
    {
        string version = Version();
        string? heading = Section(version);

        Assert.True(heading is not null, $"CHANGELOG.md has no section for the version in Directory.Build.props ({version})");
        Assert.Matches(@"^## \[" + Regex.Escape(version) + @"\] — \d{4}-\d{2}-\d{2}$", heading!);
    }

    [Fact]
    public void NoSectionIsLeftSayingUnreleased()
    {
        // `[Unreleased]` is the bucket for work in progress; a numbered version that still says
        // "unreleased" is a release that was tagged without its notes.
        var stale = Regex.Matches(Changelog(), @"^## \[(\d+\.\d+\.\d+)\] — unreleased$", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToArray();

        Assert.True(stale.Length == 0, "tagged without notes: " + string.Join(", ", stale));
    }

    [Fact]
    public void TheReleasePageIsTheChangelogSection()
    {
        string workflow = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, ".github", "workflows", "release.yml"));

        // The body comes from the changelog, through the one script that reads it…
        Assert.Contains("tools/release-notes.ps1", workflow, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(TestPaths.RepositoryRoot, "tools", "release-notes.ps1")));

        // …and not from a second file edited alongside it.
        Assert.DoesNotContain(".github/release-notes.md", workflow, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(TestPaths.RepositoryRoot, ".github", "release-notes.md")));
    }

    private static string Version()
    {
        string props = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "Directory.Build.props"));
        Match match = Regex.Match(props, @"<Version>([^<]+)</Version>");
        Assert.True(match.Success, "Directory.Build.props has no <Version>");
        return match.Groups[1].Value;
    }

    private static string Changelog() =>
        // One line ending: with CRLF, an anchored `$` never matches the line it looks like it should.
        File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "CHANGELOG.md")).ReplaceLineEndings("\n");

    /// <summary>The heading of one version's section, or null when it has none.</summary>
    private static string? Section(string version) =>
        Regex.Matches(Changelog(), @"^## \[[^\]]+\] — [^\r\n]+$", RegexOptions.Multiline)
            .Select(m => m.Value)
            .FirstOrDefault(heading => heading.StartsWith($"## [{version}]", StringComparison.Ordinal));
}
