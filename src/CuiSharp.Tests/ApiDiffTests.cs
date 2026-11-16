using System.Diagnostics;

namespace CuiSharp.Tests;

public class ApiDiffTests
{
    [Fact]
    public void MovingDeclarationsAndChangingCommentsDoesNotInventNewApis()
    {
        string root = Path.Combine(Path.GetTempPath(), "cui-api-diff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "include", "cui"));
        try
        {
            Run(root, "git", "-c", "init.templateDir=", "init", "-q");
            string header = Path.Combine(root, "include", "cui", "cui.h");
            File.WriteAllText(header, """
                /* ---- Voice ---- */
                CUI_API void cui_kept(int value);
                CUI_API void cui_removed(void);
                CUI_API void cui_spinner(float size);
                typedef struct cui_item { int value; } cui_item;
                """);
            File.WriteAllText(Path.Combine(root, "include", "cui", "cui_app.h"), "");
            File.WriteAllText(Path.Combine(root, "include", "cui", "cui_render.h"), "");
            Commit(root);
            string before = Run(root, "git", "rev-parse", "HEAD").Trim();
            File.WriteAllText(header, """
                /* ---- Voice ---- */
                /* CUI_API void cui_example_only(void); */
                CUI_API void (cui_spinner)(float size);
                typedef struct cui_item {
                    int value;
                } cui_item;
                /* ---- Creative controls ---- */
                CUI_API void
                    cui_added(void);
                typedef struct cui_new { int value; } cui_new;
                """);
            // A declaration moved into a different header, not a new export.
            File.WriteAllText(Path.Combine(root, "include", "cui", "cui_app.h"), "CUI_API void cui_kept(int value);");
            Commit(root);
            string after = Run(root, "git", "rev-parse", "HEAD").Trim();
            string script = Path.Combine(TestPaths.RepositoryRoot, "skills", "cui-sync", "scripts", "cui-api-diff.ps1");
            string output = Run(root, "pwsh", "-NoProfile", "-File", script, "-Submodule", root, "-From", before, "-To", after);
            Assert.Contains("functions added (1)", output, StringComparison.Ordinal);
            Assert.Contains("  cui_added", output, StringComparison.Ordinal);
            Assert.Contains("functions removed (1)", output, StringComparison.Ordinal);
            Assert.Contains("  cui_removed", output, StringComparison.Ordinal);
            Assert.Contains("structs added (1)", output, StringComparison.Ordinal);
            Assert.Contains("  cui_new", output, StringComparison.Ordinal);
            Assert.DoesNotContain("structs removed", output, StringComparison.Ordinal);
            Assert.DoesNotContain("cui_example_only", output, StringComparison.Ordinal);
            Assert.Contains("new header sections (1)", output, StringComparison.Ordinal);
            Assert.Contains("  Creative controls", output, StringComparison.Ordinal);
        }
        finally
        {
            // Git objects are read-only on Windows; cleanup must not mask the test's verdict.
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Commit(string root)
    {
        Run(root, "git", "add", ".");
        Run(root, "git", "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
            "-c", "core.hooksPath=", "commit", "-q", "--no-gpg-sign", "-m", "Fixture");
    }

    private static string Run(string root, string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException(executable);
        }
        Assert.True(process.ExitCode == 0, stderr.GetAwaiter().GetResult());
        return stdout.GetAwaiter().GetResult();
    }
}
