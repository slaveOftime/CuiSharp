using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CuiSharp.Tools;

namespace CuiSharp.Tests;

/// <summary>
/// <c>dotnet cui api</c> and <c>dotnet cui icons</c> answered offline. Every call passes
/// <c>--project</c> pointing at a directory that does not exist: the normal CLI path would
/// fail there with "No app found" before printing anything, so an exit code of 0 with the
/// right payload is itself the proof that the catalog answer never discovered a project,
/// built an app, started a process, reflected over CuiSharp, or loaded the native library.
/// Assertions are on what a person or script reads — declarations with signatures and
/// defaults, code points, exit codes — not on the catalog's internal arrays.
/// </summary>
public class ApiCatalogCliTests
{
    /// <summary>A path with no project and no app, for the proofs that need a real one.</summary>
    private static readonly string NoProjectHere = Path.Combine(Path.GetTempPath(), "cui-api-offline-" + Guid.NewGuid());

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = Cli.Run(args, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public void TheCatalogCommandsCannotBePointedAtAnApp()
    {
        // What was a promise in a comment is now the grammar: `api` and `icons` declare no way to
        // name a project, a published build or a live window, so no spelling of the line can make
        // the catalog resolve one.
        foreach (string argument in new[] { "--project", "--app", "--attach", "--no-build" })
        {
            (int exit, _, string error) = Run("api", "ChartOpts", argument);

            Assert.Equal(2, exit);
            Assert.Contains($"'{argument}'", error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnExactOptionTypeAnswersWithItsFieldDeclarationsWithoutAnyProject()
    {
        // Asked for as JSON-free text, with the only output switch a catalog command declares:
        // `-q` and `--frames` are app-command options now, and `api` refuses them outright.
        (int exit, string output, _) = Run("api", "ChartOpts", "--json=false");

        Assert.Equal(0, exit);
        Assert.Contains("ChartOpts  [struct]", output, StringComparison.Ordinal);
        // A field declaration a person can read, not an array of names.
        Assert.Contains("public bool Area", output, StringComparison.Ordinal);
        // The Issue-8 surface: the regeneration put the managed Normalize field and its
        // pointer to ChartNormalizer into the catalog the tool embeds.
        Assert.Contains("public NormalizeMode Normalize", output, StringComparison.Ordinal);
        Assert.Contains("ChartNormalizer", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOverloadsOfAMemberComeBackWithTheirSignaturesAndDefaults()
    {
        (int exit, string output, _) = Run("api", "Ui.LineChart");

        Assert.Equal(0, exit);
        int overloads = Regex.Matches(output, @"^\s*void LineChart\(", RegexOptions.Multiline).Count;
        Assert.True(overloads >= 2, $"LineChart has one span overload and one Series[] overload, saw:\n{output}");
        // The defaults are part of the answer: `= default` is how a caller knows the tail is optional.
        Assert.Contains("ChartOpts o = default", output, StringComparison.Ordinal);
        Assert.Contains("ReadOnlySpan<float> values", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownNameExitsNonzeroAndPointsAtTheNearestName()
    {
        (int exit, string output, _) = Run("api", "ChartOptz");

        Assert.Equal(1, exit);
        Assert.Contains("nothing named \"ChartOptz\"", output, StringComparison.Ordinal);
        Assert.Contains("ChartOpts", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAmbiguousMemberNamesItsQualifiedOwnersAndExitsNonzero()
    {
        (int exit, string output, _) = Run("api", "Run");

        Assert.Equal(1, exit);
        Assert.Contains("CuiApp.Run", output, StringComparison.Ordinal);
        Assert.Contains("CuiApplication.Run", output, StringComparison.Ordinal);
    }

    [Fact]
    public void IconsListEveryConstantWithItsCodePointAndPasteReadyLiteral()
    {
        (int exit, string output, _) = Run("icons");

        Assert.Equal(0, exit);
        string[] lines = [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries)];
        int constants = typeof(Icons).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Count(f => f.IsLiteral);
        Assert.Equal(constants, lines.Length);
        Assert.All(lines, line => Assert.Matches(@"U\+[0-9A-F]{4}", line));
        string check = lines.First(line => line.StartsWith("Check", StringComparison.Ordinal));
        string real = (string)typeof(Icons).GetField("Check")!.GetRawConstantValue()!;
        Assert.Contains($"U+{(int)real[0]:X4}", check, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIconFilterNarrowsTheListAndStillExitsZero()
    {
        (int exit, string output, _) = Run("icons", "chart");

        Assert.Equal(0, exit);
        string[] lines = [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries)];
        Assert.Contains(lines, line => line.StartsWith("ChartLine", StringComparison.Ordinal));
        Assert.All(lines, line => Assert.Contains("chart", line, StringComparison.OrdinalIgnoreCase));
        int constants = typeof(Icons).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Count(f => f.IsLiteral);
        Assert.True(lines.Length < constants, "the filter filtered");
    }

    [Fact]
    public void AnIconFilterThatHitsNothingIsActionableAndNonzero()
    {
        (int exit, string output, _) = Run("icons", "zzzznotanicon");

        Assert.Equal(1, exit);
        Assert.Contains("no icon matches \"zzzznotanicon\"", output, StringComparison.Ordinal);
        Assert.Contains("`cui icons` lists them all", output, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonIsMachineReadableForApiAndIcons()
    {
        (int exit, string output, _) = Run("api", "ChartOpts", "--json");
        Assert.Equal(0, exit);
        using (JsonDocument exact = JsonDocument.Parse(output))
        {
            Assert.Equal("exact", exact.RootElement.GetProperty("match").GetString());
            Assert.Equal("ChartOpts", exact.RootElement.GetProperty("term").GetString());
            Assert.True(exact.RootElement.GetProperty("lines").GetArrayLength() > 0);
        }

        (exit, output, _) = Run("icons", "zzzznotanicon", "--json");
        Assert.Equal(1, exit);
        using JsonDocument none = JsonDocument.Parse(output);
        Assert.Equal("unknown", none.RootElement.GetProperty("match").GetString());
    }

    [Fact]
    public void ApiWithoutATermIsAUsageErrorNotACrash()
    {
        (int exit, _, string error) = Run("api");

        Assert.Equal(2, exit);
        Assert.Contains("Required argument 'name' missing", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AFlagNeitherCommandKnowsIsReportedRatherThanSentOn()
    {
        (int exit, _, string error) = Run("api", "ChartOpts", "--pgae");

        Assert.Equal(2, exit);
        Assert.Contains("--pgae", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCliAnswersFromTheCatalogEmbeddedInItsOwnAssembly()
    {
        // The reader loads the resource, not the file; this pins that the bytes it loads are
        // the checked-in catalog, so the shipped tool and the skills folder cannot disagree.
        using Stream? data = typeof(Cli).Assembly.GetManifestResourceStream("CuiSharp.Tools.api-catalog.json");
        Assert.NotNull(data);
        string embedded = new StreamReader(data!).ReadToEnd();

        string checkedIn = File.ReadAllText(
            Path.Combine(TestPaths.RepositoryRoot, "skills", "cui-dotnet", "api", "catalog.json"));
        Assert.Equal(checkedIn.Replace("\r\n", "\n"), embedded.Replace("\r\n", "\n"));
    }

    [Fact]
    public void HelpAfterACatalogCommandIsTheQueryOrFilterNotTheUsage()
    {
        // `icons help` is a filter over the glyph index, and CircleHelp is its honest answer;
        // printing the manual there would hide a real constant behind a help screen.
        (int exit, string output, _) = Run("icons", "help");
        Assert.Equal(0, exit);
        Assert.Contains("CircleHelp", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Usage:", output, StringComparison.Ordinal);

        (exit, output, _) = Run("icons", "help", "--json");
        Assert.Equal(0, exit);
        using (JsonDocument json = JsonDocument.Parse(output))
        {
            Assert.Equal("exact", json.RootElement.GetProperty("match").GetString());
            Assert.Contains("CircleHelp", json.RootElement.GetProperty("lines").GetRawText(), StringComparison.Ordinal);
        }

        // `api help` asks what the catalog knows about "help" and gets the ordinary answer:
        // the two constants whose native name is help, an ambiguous list with exit 1 — a
        // lookup to read, not a usage screen to ignore.
        (exit, output, _) = Run("api", "help");
        Assert.Equal(1, exit);
        Assert.Contains("CircleHelp", output, StringComparison.Ordinal);
        Assert.Contains("DialogHelper", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Usage:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitHelpStillAsksForUsageEvenBesideACatalogCommand()
    {
        (int exit, string output, _) = Run("api", "--help");
        Assert.Equal(0, exit);
        Assert.Contains("Usage:", output, StringComparison.Ordinal);

        (exit, output, _) = Run("icons", "-h");
        Assert.Equal(0, exit);
        Assert.Contains("Usage:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOptionWhoseValueIsNotItsTypeIsReportedInsteadOfSwallowed()
    {
        // `--frames --json` used to lose its number and eat the flag behind it, answering a
        // different command than the one that was typed and calling that success. A typed
        // option now says what it wanted: a count, not `--json`.
        (int exit, _, string error) = Run("tree", "--frames", "--json");
        Assert.Equal(2, exit);
        Assert.Contains("'--frames'", error, StringComparison.Ordinal);
        Assert.Contains("whole number", error, StringComparison.Ordinal);

        (exit, _, error) = Run("tree", "--wait-timeout", "--pgae");
        Assert.Equal(2, exit);
        Assert.Contains("'--wait-timeout'", error, StringComparison.Ordinal);
        Assert.Contains("a number", error, StringComparison.Ordinal);

        // A value missing at the end of the line is the same mistake.
        (exit, _, error) = Run("tree", "--app");
        Assert.Equal(2, exit);
        Assert.Contains("'--app'", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonCatalogCommandKeepsItsOwnGrammarEvenWithAwkwardOptionValues()
    {
        // `tree --wait-text -loading` is legal: `-loading` is a value, not a flag, so text that
        // begins with a dash is still text to wait for.
        Grammar.Parsed parsed = Grammar.Parse(["tree", "--wait-text", "-loading"]);
        Assert.Empty(parsed.Errors);
        Assert.Equal("tree", parsed.Options!.Command);
        Assert.Equal("waittext 10 -loading", parsed.Options.WaitLine);

        (int exit, _, string error) = Run("tree", "--project", NoProjectHere, "--wait-text", "-loading");
        Assert.Equal(1, exit);
        Assert.DoesNotContain("needs a value", error, StringComparison.Ordinal);
        Assert.Contains(NoProjectHere, error, StringComparison.Ordinal);

        // And `--json` exists only where there is JSON to answer with, so an app command cannot
        // be handed a switch it would only ignore.
        (exit, _, error) = Run("tree", "--json");
        Assert.Equal(2, exit);
        Assert.Contains("'--json'", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ACatalogCommandReportsMalformedOptionsThroughTheUnifiedParser()
    {
        // A value that merely looks like a flag is still a value — `--wait-text -loading` needs
        // that rule — so what is refused is a value the option's declared type cannot hold.
        var output = new StringWriter();
        var errors = new StringWriter();
        int exit = Cli.Run(["tree", "--wait-timeout", "abc"], output, errors);
        Assert.Equal(2, exit);
        Assert.Contains("'--wait-timeout'", errors.ToString(), StringComparison.Ordinal);
        Assert.Contains("abc", errors.ToString(), StringComparison.Ordinal);
        Assert.Equal("", output.ToString());

        (int badExit, _, string badError) = Run("tree", "--frames", "many");
        Assert.Equal(2, badExit);
        Assert.Contains("whole number", badError, StringComparison.Ordinal);
    }

    [Fact]
    public void ARepeatedOptionIsRefusedRatherThanSilentlyOverwritten()
    {
        // `--wait ready --wait invalid` used to be decided by whichever came last. Two answers
        // to the same question mean the line was not settled, so neither answer is acted on.
        var output = new StringWriter();
        var errors = new StringWriter();

        Assert.Equal(2, Cli.Run(["tree", "--frames", "2", "--frames", "3"], output, errors));
        Assert.Contains("'--frames'", errors.ToString(), StringComparison.Ordinal);
        Assert.Equal("", output.ToString());

        // A wrong value for a one-value option is the same class of mistake, said differently.
        output = new StringWriter();
        errors = new StringWriter();
        Assert.Equal(2, Cli.Run(["tree", "--wait", "invalid"], output, errors));
        Assert.Contains("'ready'", errors.ToString(), StringComparison.Ordinal);
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public void JsonAndTextAnswerTheSameQuestionsOffline()
    {
        (int exit, string output, _) = Run("api", "ChartOpts", "--json");
        Assert.Equal(0, exit);
        using (JsonDocument json = JsonDocument.Parse(output))
            Assert.Equal("exact", json.RootElement.GetProperty("match").GetString());

        // The same name in text, from the same call, with nothing built or started: an app flag
        // is not accepted at all now, so there is no "ignored" path left to be identical to.
        (exit, output, _) = Run("api", "ChartOpts");
        Assert.Equal(0, exit);
        Assert.Contains("ChartOpts  [struct]", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameNowhereNearTheCatalogSaysNoNameIsClose()
    {
        (int exit, string output, _) = Run("api", "qqzzxvw");
        Assert.Equal(1, exit);
        Assert.Contains("no name is close", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheJsonAnswerRoundTripsEveryCharacterTheWriterMustEscape()
    {
        // The renderer writes by hand (Utf8JsonWriter, no contract resolver), so this is
        // where "still valid JSON, still exactly these strings" is pinned: quotes,
        // backslashes, control characters and non-ASCII, in the term and in the lines.
        string term = "a \"quoted\" \\ back\\slash term with\nnewline and\ttab and ünïcode";
        CatalogResult result = CatalogResult.Exact(term, ["line with \"quotes\" and \\slashes\\", "line with <html> and a \u0001 control"]);

        using JsonDocument doc = JsonDocument.Parse(result.RenderJson());
        Assert.Equal("exact", doc.RootElement.GetProperty("match").GetString());
        Assert.Equal(term, doc.RootElement.GetProperty("term").GetString());
        JsonElement lines = doc.RootElement.GetProperty("lines");
        Assert.Equal(2, lines.GetArrayLength());
        Assert.Equal("line with \"quotes\" and \\slashes\\", lines[0].GetString());
        Assert.Equal("line with <html> and a \u0001 control", lines[1].GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("suggestions").GetArrayLength());
    }

    [Fact]
    public void TheOfflineJsonAnswersWithReflectionBasedSerializationDisabled()
    {
        // The switch that disables reflection-based JSON is read at process start, so the
        // only honest proof is a child: this build's tool dll, a runtimeconfig that turns
        // the default resolver off, and the offline commands. A renderer that reaches for
        // JsonSerializer's default resolver dies in this child — unhandled
        // InvalidOperationException, not an answer; the hand-written one answers.
        string source = Path.Combine(
            TestPaths.RepositoryRoot, "src", "CuiSharp.Tools", "bin", ConfigurationUnderTest(), "net10.0",
            "CuiSharp.Tools.dll");
        Assert.True(File.Exists(source), $"{source} is missing: dotnet test builds it through the ProjectReference");

        string directory = Path.Combine(Path.GetTempPath(), "cui-json-no-reflection-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            string tool = Path.Combine(directory, "CuiSharp.Tools.dll");
            // Everything beside the tool, not just the tool: its own dependencies have to travel
            // or the child dies before it can answer anything.
            foreach (string file in Directory.GetFiles(Path.GetDirectoryName(source)!))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(file, Path.Combine(directory, name), overwrite: true);
            }

            File.WriteAllText(Path.ChangeExtension(tool, ".runtimeconfig.json"), """
                {
                  "runtimeOptions": {
                    "tfm": "net10.0",
                    "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.0" },
                    "configProperties": { "System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault": false }
                  }
                }
                """);

            foreach (string[] command in new[] { new[] { "api", "ChartOpts", "--json" }, new[] { "icons", "help", "--json" } })
            {
                (int exit, string output, string error) = RunChild(tool, command);
                Assert.True(exit == 0, $"`{string.Join(' ', command)}` exited {exit} with reflection off: {error}");
                using JsonDocument json = JsonDocument.Parse(output);
                Assert.Equal("exact", json.RootElement.GetProperty("match").GetString());
            }
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>The configuration this test assembly was built in — the only one whose tool dll may run.</summary>
    private static string ConfigurationUnderTest()
    {
        string? directory = Path.GetDirectoryName(typeof(ApiCatalogCliTests).Assembly.Location);
        ArgumentException.ThrowIfNullOrEmpty(directory);
        string[] parts = directory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int i = 0; i < parts.Length - 1; i++)
            if (parts[i].Equals("bin", StringComparison.OrdinalIgnoreCase)) return parts[i + 1];

        throw new InvalidOperationException($"{directory} does not live under a bin{Path.DirectorySeparatorChar}<configuration> directory");
    }

    /// <summary>
    /// Runs the child with both pipes drained at once: a crash fills one, and a reader that
    /// waits on the other first would call that a hang instead of reading the exception.
    /// </summary>
    private static (int Exit, string Out, string Error) RunChild(string toolDll, string[] arguments)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in new string[] { toolDll }.Concat(arguments)) info.ArgumentList.Add(argument);

        using Process process = Process.Start(info) ?? throw new InvalidOperationException("could not start the child");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(1)))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("the child did not answer within one minute");
        }

        return (process.ExitCode, output.GetAwaiter().GetResult(), errors.GetAwaiter().GetResult());
    }
}
