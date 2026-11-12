namespace CuiSharp.Tests;

/// <summary>
/// <c>bindings/coverage.txt</c> is the account of every C function CUI exports: wrapped, hand
/// written, or excluded with a reason. <c>PENDING</c> is the generator saying it does not yet know
/// what to do with something, which is unfinished work, not a known limitation — and a pending
/// option struct quietly stops being emitted, so the next build fails somewhere else entirely.
/// </summary>
public class CoverageAccountTests
{
    [Fact]
    public void NothingIsPending()
    {
        string[] pending = File.ReadAllLines(Path.Combine(TestPaths.RepositoryRoot, "bindings", "coverage.txt"))
            .Where(line => line.StartsWith("PENDING ", StringComparison.Ordinal))
            .ToArray();

        Assert.True(pending.Length == 0,
            "bindings/coverage.txt has unfinished entries; the sync rules in skills/sync-cui/SKILL.md say how to decide them:" +
            Environment.NewLine + string.Join(Environment.NewLine, pending));
    }

    [Fact]
    public void EveryExclusionSaysWhyItIsOne()
    {
        // EXCLUDED without a reason is a function nobody decided about, wearing a decision's name.
        string[] silent = File.ReadAllLines(Path.Combine(TestPaths.RepositoryRoot, "bindings", "coverage.txt"))
            .Where(line => line.StartsWith("EXCLUDED ", StringComparison.Ordinal))
            .Where(line => line[(line.IndexOf(':') + 1)..].Trim().Length < 20)
            .ToArray();

        Assert.Empty(silent);
    }
}
