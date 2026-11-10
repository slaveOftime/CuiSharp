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
    /// <summary>
    /// The version being built has a section, dated once the release is cut and "unreleased"
    /// while the work is still going in. A version with no section at all is a build nobody
    /// can write notes for.
    /// </summary>
    [Fact]
    public void TheVersionBeingBuiltHasASection()
    {
        string version = Version();
        string? heading = Section(version);

        Assert.True(heading is not null, $"CHANGELOG.md has no section for the version in Directory.Build.props ({version})");
        Assert.Matches(@"^## \[" + Regex.Escape(version) + @"\] — (unreleased|\d{4}-\d{2}-\d{2})$", heading!);
    }

    /// <summary>
    /// The changelog may carry a numbered section marked unreleased — that is work in progress —
    /// but the release must never publish one: the guard lives in <c>release-notes.ps1</c>, which
    /// <c>release.yml</c> runs, and this proves it refuses an unreleased section and accepts the
    /// dated one. Checked against a fixture, so the test survives the real section being dated.
    /// </summary>
    [Fact]
    public void TheReleaseRefusesAnUnreleasedSection()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cuisharp-release-notes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "CHANGELOG.md");
            string script = Path.Combine(TestPaths.RepositoryRoot, "tools", "release-notes.ps1");

            File.WriteAllText(path, Unreleased + "\n" + Dated + "\n");
            (int code, string output) = Run(script, "-Version", "0.3.1", "-Path", path);
            Assert.NotEqual(0, code);
            Assert.Contains("unreleased", output, StringComparison.OrdinalIgnoreCase);

            File.WriteAllText(path, Dated);
            (code, output) = Run(script, "-Version", "0.3.1", "-Path", path);
            Assert.Equal(0, code);
            Assert.Contains("shipped", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private const string Unreleased = """
        # Changelog

        ## [0.3.1] — unreleased

        ### Added

        - shipped
        """;

    private const string Dated = """
        # Changelog

        ## [0.3.1] — 2026-11-10

        ### Added

        - shipped
        """;

    private static (int Code, string Output) Run(string script, params string[] arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "pwsh",
            ArgumentList = { "-NoProfile", "-File", script },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);

        using var process = System.Diagnostics.Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
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
