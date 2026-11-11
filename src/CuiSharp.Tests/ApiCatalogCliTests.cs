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
    /// <summary>A path with no project and no app, for the proof described above.</summary>
    private static readonly string NoProjectHere = Path.Combine(Path.GetTempPath(), "cui-api-offline-" + Guid.NewGuid());

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = Cli.Run([.. args, "--project", NoProjectHere], output, error);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public void AnExactOptionTypeAnswersWithItsFieldDeclarationsWithoutAnyProject()
    {
        (int exit, string output, _) = Run("api", "ChartOpts", "-q");

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
        Assert.Contains("needs a name", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AFlagNeitherCommandKnowsIsReportedRatherThanSentOn()
    {
        (int exit, _, string error) = Run("api", "ChartOpts", "--pgae");

        Assert.Equal(1, exit);
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
        Assert.DoesNotContain("dotnet cui <command>", output, StringComparison.Ordinal);

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
        Assert.DoesNotContain("dotnet cui <command>", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitHelpStillAsksForUsageEvenBesideACatalogCommand()
    {
        (int exit, string output, _) = Run("api", "--help");
        Assert.Equal(0, exit);
        Assert.Contains("dotnet cui <command>", output, StringComparison.Ordinal);

        (exit, output, _) = Run("icons", "-h");
        Assert.Equal(0, exit);
        Assert.Contains("dotnet cui <command>", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIgnoredOptionNeverSwallowsTheFlagBehindIt()
    {
        // `--frames --json` lost its number. Eating the flag behind it would answer a
        // different command than the one that was typed and call the answer successful.
        (int exit, _, string error) = Run("api", "ChartOpts", "--frames", "--json");
        Assert.Equal(1, exit);
        Assert.Contains("--frames needs a value", error, StringComparison.Ordinal);

        (exit, _, error) = Run("api", "ChartOpts", "--wait-timeout", "--pgae");
        Assert.Equal(1, exit);
        Assert.Contains("--wait-timeout needs a value", error, StringComparison.Ordinal);

        // A value missing at the end of the line is the same mistake. No --project is needed
        // here: the catalog path never looks at a project, so the parse error is all there is.
        var answers = new StringWriter();
        var errors = new StringWriter();
        exit = Cli.Run(["api", "ChartOpts", "--app"], answers, errors);
        Assert.Equal(1, exit);
        Assert.Contains("--app needs a value", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ANonCatalogCommandKeepsItsOwnGrammarEvenWithAwkwardOptionValues()
    {
        // `tree --wait-text -loading` is legal in the ordinary flow: `-loading` is a value,
        // not a flag. The catalog scan reads argv before anyone knows which flow this is, so
        // it may hold a complaint but never voice one for a command it does not own.
        Assert.False(Cli.TryReadCatalogCommand(
            ["tree", "--wait-text", "-loading", "--help"], out Cli.CatalogRequest _));

        Cli.Options options = Cli.Options.Parse(["tree", "--wait-text", "-loading", "--help"]);
        Assert.Equal("tree", options.Command);
        Assert.Equal("waittext 10 -loading", options.WaitLine);
        Assert.True(options.Help);

        // End to end the same line reaches the ordinary path: the complaint that comes back
        // is about the project the test names, never about the value the scan held back.
        (int exit, _, string error) = Run("tree", "--wait-text", "-loading");
        Assert.Equal(1, exit);
        Assert.DoesNotContain("needs a value", error, StringComparison.Ordinal);
        Assert.Contains(NoProjectHere, error, StringComparison.Ordinal);

        // And `--json` stays the ordinary parser's unknown option — the catalog commands are
        // the only ones that answer machine-readable, and the scan must not grant it wider.
        (exit, _, error) = Run("tree", "--json");
        Assert.Equal(1, exit);
        Assert.Contains("Unknown option \"--json\"", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ACatalogCommandReportsTheOptionMistakesTheScanHeldBack()
    {
        // The escrow is only ever cashed for api/icons — and on either side of the command
        // word. After it: `--project --json` must name the option that lost its value, not
        // swallow --json and not answer any query at all.
        var output = new StringWriter();
        var errors = new StringWriter();
        int exit = Cli.Run(["api", "ChartOpts", "--project", "--json"], output, errors);
        Assert.Equal(1, exit);
        Assert.Contains("--project needs a value", errors.ToString(), StringComparison.Ordinal);
        Assert.Equal("", output.ToString());

        // Before it: a malformed flag ahead of the command word is the same mistake, and
        // again nothing is printed as if it were an answer.
        output = new StringWriter();
        errors = new StringWriter();
        exit = Cli.Run(["--wait-text", "--json", "api", "ChartOpts"], output, errors);
        Assert.Equal(1, exit);
        Assert.Contains("--wait-text needs a value", errors.ToString(), StringComparison.Ordinal);
        Assert.Equal("", output.ToString());

        // And a value the ordinary parser would refuse — a number that is not one, whatever
        // its dashes — is refused here in the parser's own words, not a catalog-side echo:
        // the scan borrows `--frames`/`--wait-timeout` validation rather than keeping a
        // second grammar that could disagree with the first.
        (int badExit, _, string badError) = Run("api", "ChartOpts", "--wait-timeout", "abc");
        Assert.Equal(1, badExit);
        Assert.Contains("--wait-timeout needs a finite number of seconds", badError, StringComparison.Ordinal);
        Assert.Contains("not \"abc\"", badError, StringComparison.Ordinal);
    }

    [Fact]
    public void ASwallowedOptionValueNeverStealsTheCommandWord()
    {
        // `--wait-text --app tree api --help`: the ordinary parser spends the word after a
        // flag on it, dash or not, so `--wait-text` eats `--app` and the command is `tree`.
        // The scan may complain about that eaten flag, but it must consume identically while
        // doing so — a scan whose position diverges calls this a catalog line and answers a
        // validation error for an ordinary tree run.
        Assert.False(Cli.TryReadCatalogCommand(
            ["--wait-text", "--app", "tree", "api", "--help"], out Cli.CatalogRequest _));

        Cli.Options options = Cli.Options.Parse(["--wait-text", "--app", "tree", "api", "--help"]);
        Assert.Equal("tree", options.Command);
        Assert.Equal("waittext 10 --app", options.WaitLine);
        Assert.True(options.Help);

        // End to end the line is what the ordinary grammar says it is: usage, exit 0.
        var output = new StringWriter();
        var errors = new StringWriter();
        int exit = Cli.Run(["--wait-text", "--app", "tree", "api", "--help"], output, errors);
        Assert.Equal(0, exit);
        Assert.Contains("dotnet cui <command>", output.ToString(), StringComparison.Ordinal);
        Assert.Equal("", errors.ToString());
    }

    [Fact]
    public void TheScanOwnsTheLineOnlyForTheCommandOptionsParseWouldName()
    {
        // `--project --app api ChartOpts --help`: the ordinary parser spends `--app` on
        // `--project`, so the first positional — the command — is `api`, and this line is
        // the catalog's business. It then names the malformed flag rather than printing
        // usage as if `api` had been swallowed into the option and never seen. (The earlier
        // scan did exactly that: it refused to eat a dash-value, slid `api` into a slot the
        // parser gives to nothing, and answered usage for a line it should have policed.)
        Assert.Throws<ToolException>(() => Cli.TryReadCatalogCommand(
            ["--project", "--app", "api", "ChartOpts", "--help"], out Cli.CatalogRequest _));

        var output = new StringWriter();
        var errors = new StringWriter();
        int exit = Cli.Run(["--project", "--app", "api", "ChartOpts", "--help"], output, errors);
        Assert.Equal(1, exit);
        Assert.Contains("--project needs a value", errors.ToString(), StringComparison.Ordinal);
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public void AWaitTheCallerOverwritesIsJudgedByTheValueThatSurvives()
    {
        // `--wait` is the one option whose value the parser does not judge where it reads it:
        // every occurrence overwrites the last, and the surviving value is checked after the
        // whole line. A scan that judges each occurrence condemns `--wait invalid --wait ready`
        // — a legal wait, and a legal `api` call — for a typo the caller already replaced.
        Cli.Options overwritten = Cli.Options.Parse(["api", "ChartOpts", "--wait", "invalid", "--wait", "ready"]);
        Assert.Equal("wait 10", overwritten.WaitLine);

        Assert.True(Cli.TryReadCatalogCommand(
            ["api", "ChartOpts", "--wait", "invalid", "--wait", "ready"], out Cli.CatalogRequest _));

        (int exit, string output, string error) = Run("api", "ChartOpts", "--wait", "invalid", "--wait", "ready");
        Assert.Equal(0, exit);
        Assert.Contains("ChartOpts  [struct]", output, StringComparison.Ordinal);
        Assert.Equal("", error);

        // With `--help` behind it the same line is the usage screen the ordinary grammar
        // grants it, not a validation error about a word no wait will ever use.
        (exit, output, error) = Run("api", "ChartOpts", "--wait", "invalid", "--wait", "ready", "--help");
        Assert.Equal(0, exit);
        Assert.Contains("dotnet cui <command>", output.ToString(), StringComparison.Ordinal);
        Assert.Equal("", error);

        // The filter still filters: the final `ready` is what the line waits on, so `icons
        // help` answers CircleHelp instead of a complaint about the overwritten value.
        (exit, output, error) = Run("icons", "help", "--wait", "invalid", "--wait", "ready");
        Assert.Equal(0, exit);
        Assert.Contains("CircleHelp", output, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet cui <command>", output, StringComparison.Ordinal);
        Assert.Equal("", error);

        // The other order is the mistake, and the scanner says so in the parser's own words:
        // the last `--wait` is the one the run would have waited on.
        Assert.Throws<ToolException>(() => Cli.TryReadCatalogCommand(
            ["api", "ChartOpts", "--wait", "ready", "--wait", "invalid"], out Cli.CatalogRequest _));

        (exit, output, error) = Run("api", "ChartOpts", "--wait", "ready", "--wait", "invalid");
        Assert.Equal(1, exit);
        Assert.Contains("--wait needs \"ready\", not \"invalid\"", error, StringComparison.Ordinal);
        Assert.Equal("", output);
    }

    [Fact]
    public void JsonAndIgnoredAppFlagsAnswerTheSameOffline()
    {
        (int exit, string output, _) = Run("--json", "api", "ChartOpts");
        Assert.Equal(0, exit);
        using (JsonDocument json = JsonDocument.Parse(output))
            Assert.Equal("exact", json.RootElement.GetProperty("match").GetString());

        (exit, output, _) = Run("api", "ChartOpts", "--app", "nowhere.exe");
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
            File.Copy(source, tool);
            File.Copy(Path.ChangeExtension(source, ".deps.json"), Path.ChangeExtension(tool, ".deps.json"));
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
