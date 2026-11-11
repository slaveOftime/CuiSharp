using CuiSharp.Tools;

namespace CuiSharp.Tests;

/// <summary>
/// Which program <c>dotnet cui</c> decides to drive. Guessing here runs someone else's app: the
/// newest DLL under <c>bin/Release</c> was a dependency as often as it was the app, and two app
/// projects in one folder used to resolve to whichever the file system listed first.
/// </summary>
public class ToolResolutionTests
{
    private const string App = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    [Fact]
    public void TwoAppProjectsInOneFolderAreNotGuessed()
    {
        using var folder = new Scratch();
        folder.Project("First");
        folder.Project("Second");

        var error = Assert.Throws<ToolException>(() => AppLocator.FindProject(folder.Path));
        Assert.Contains("First.csproj", error.Message, StringComparison.Ordinal);
        Assert.Contains("Second.csproj", error.Message, StringComparison.Ordinal);
        Assert.Contains("--project", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATestProjectNextToTheAppIsNotAnAppCandidate()
    {
        using var folder = new Scratch();
        string app = folder.Project("Shop");
        folder.Project("Shop.Tests");

        Assert.Equal(app, AppLocator.FindProject(folder.Path));
    }

    [Fact]
    public void AMultiTargetedProjectNeedsAFramework()
    {
        const string report = """
            {
              "Properties": {
                "TargetPath": "",
                "TargetFramework": "",
                "TargetFrameworks": "net10.0;net9.0",
                "RuntimeIdentifier": ""
              }
            }
            """;

        var error = Assert.Throws<ToolException>(() => AppLocator.ParseDescription(report, framework: null));
        Assert.Contains("net10.0;net9.0", error.Message, StringComparison.Ordinal);
        Assert.Contains("--framework", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBuildTargetIsReadFromMSBuildRatherThanGuessed()
    {
        const string report = """
            {
              "Properties": {
                "TargetPath": "/work/app/bin/Release/net10.0/App.dll",
                "TargetFramework": "net10.0",
                "TargetFrameworks": "",
                "RuntimeIdentifier": ""
              }
            }
            """;

        AppLocator.BuildTarget target = AppLocator.ParseDescription(report, framework: null);

        Assert.Equal("/work/app/bin/Release/net10.0/App.dll", target.TargetPath);
        Assert.Equal("net10.0", target.Framework);
        Assert.Equal("", target.Rid);
    }

    [Fact]
    public void AMSBuildAnswerThatIsNotJSONIsReportedNotThrown()
    {
        // A JSON-shaped answer that fails to parse is a host or version problem; the user gets
        // the raw text back, not a JsonException stack trace.
        var error = Assert.Throws<ToolException>(() => AppLocator.ParseDescription("{ properties: broken", framework: null));
        Assert.Contains("Could not read MSBuild's answer", error.Message, StringComparison.Ordinal);
        Assert.Contains("properties: broken", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    // Valid JSON whose shape is not the shape MSBuild answers in: reading it would otherwise
    // throw InvalidOperationException out of the JSON reader.
    [InlineData("""{"Properties":[]}""", "`Properties` is Array")]
    [InlineData("""{"Properties":{"TargetPath":42}}""", "`TargetPath` is Number")]
    [InlineData("""[1,2]""", "not an object")]
    public void AnAnswerOfTheWrongShapeIsReportedNotThrown(string report, string expected)
    {
        var error = Assert.Throws<ToolException>(() => AppLocator.ParseDescription(report, framework: null));
        Assert.Contains("Could not read MSBuild's answer", error.Message, StringComparison.Ordinal);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALongUnreadableAnswerIsQuotedUpToItsBound()
    {
        string report = "{" + new string('x', 900);

        var error = Assert.Throws<ToolException>(() => AppLocator.ParseDescription(report, framework: null));

        Assert.Contains("Could not read MSBuild's answer", error.Message, StringComparison.Ordinal);
        Assert.True(error.Message.Length < 1000, $"the raw text was not bounded: {error.Message.Length} characters");
    }

    [Fact]
    public void AnOversizedNameInAnAnswerDoesNotStretchTheDiagnosis()
    {
        // The answer is where the property names come from, so a hostile or broken host can put
        // five thousand characters into one: the quoted name and the whole message are bounded,
        // and what is left still says which property and what was wrong with it.
        string name = new('p', 5000);
        string report = @"{""Properties"":{""" + name + @""":42}}";

        var error = Assert.Throws<ToolException>(() => AppLocator.ParseDescription(report, framework: null));

        Assert.Contains("Could not read MSBuild's answer", error.Message, StringComparison.Ordinal);
        Assert.Contains("is Number, not a string", error.Message, StringComparison.Ordinal);
        // The reason is still readable end to end, which only holds because the name was cut.
        Assert.Contains(new string('p', 60) + "…` is Number, not a string", error.Message, StringComparison.Ordinal);
        Assert.True(error.Message.Length < 800, $"the diagnosis was not bounded: {error.Message.Length} characters");
    }

    [Fact]
    public void ADrainFailureIsDiagnosedOnlyWhenAllOfItIsTheChildsOwn()
    {
        string[] arguments = ["build", "App.cs"];

        // The child's own failure — its pipe broke, it went away — is something the user can act on.
        var own = Assert.IsType<ToolException>(
            AppLocator.DrainFailure("dotnet", arguments, new IOException("pipe closed"), cleanup: null));
        Assert.Contains("dotnet build App.cs", own.Message, StringComparison.Ordinal);
        Assert.Contains("pipe closed", own.Message, StringComparison.Ordinal);

        // A bug mixed in with the child's failure is still a bug: nothing to report in its place,
        // so Run rethrows the aggregate with its stack instead of dressing it up as a build
        // problem.
        Assert.Null(AppLocator.DrainFailure("dotnet", arguments,
            new AggregateException(new IOException("pipe closed"), new NotSupportedException("a bug here")),
            cleanup: null));

        // An aggregate that names nothing proves nothing.
        Assert.Null(AppLocator.DrainFailure("dotnet", arguments, new AggregateException(), cleanup: null));
    }

    [Fact]
    public void AFailingCleanupIsToldAlongsideTheFailureItFollowed()
    {
        string[] arguments = ["build", "App.cs"];
        Exception cleanup = new AggregateException(
            new System.ComponentModel.Win32Exception(5, "a descendant would not die"));

        // The child's own failure, and the stop failing too: one diagnosis naming both, because a
        // process left running is the user's problem and the build report is their clue.
        var both = Assert.IsType<ToolException>(
            AppLocator.DrainFailure("dotnet", arguments, new IOException("pipe closed"), cleanup));
        Assert.Contains("pipe closed", both.Message, StringComparison.Ordinal);
        Assert.Contains("a descendant would not die", both.Message, StringComparison.Ordinal);

        // A bug of this tool's, and a failed stop: the bug keeps its place at the front and the
        // cleanup travels with it, so neither is lost and neither is quietly relabelled.
        Exception bug = new NotSupportedException("a bug here");
        var pair = Assert.IsType<AggregateException>(
            AppLocator.DrainFailure("dotnet", arguments, bug, cleanup));
        Assert.Same(bug, pair.InnerExceptions[0]);
        Assert.Same(cleanup, pair.InnerExceptions[1]);

        // A failure of either kind whose long message comes from the child is still quoted to a
        // bound, cleanup included.
        var bounded = Assert.IsType<ToolException>(AppLocator.DrainFailure("dotnet", arguments,
            new IOException(new string('y', 5000)), new IOException(new string('x', 5000))));
        Assert.Contains("Stopping it failed too", bounded.Message, StringComparison.Ordinal);
        Assert.Contains(new string('y', 400) + "…", bounded.Message, StringComparison.Ordinal);
        Assert.Contains(new string('x', 400) + "…", bounded.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('y', 401), bounded.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 401), bounded.Message, StringComparison.Ordinal);
        Assert.True(
            bounded.Message.Length < 1000,
            $"the failure was not bounded: {bounded.Message.Length} characters");
    }

    [Fact]
    public void ASoleProjectIsTheAppEvenWhenItsNameSaysTests()
    {
        // One project in a folder is the app whoever it is called: "Contests" and "Latest" contain
        // the letters of a test-project name without being one, and there is nothing to choose.
        using var folder = new Scratch();
        string only = folder.Project("Contests");

        Assert.Equal(only, AppLocator.FindProject(folder.Path));
    }

    [Fact]
    public void AShebangAndACommentHeaderDoNotHideTheDirectives()
    {
        Assert.True(AppLocator.HasFileBasedDirective(
            ["#!/usr/bin/env dotnet", "#:property TargetFramework=net10.0", "System.Console.WriteLine();"]));
        Assert.True(AppLocator.HasFileBasedDirective(
            ["/* one app,", "   one file */", "#:package CuiSharp@*"]));
        Assert.True(AppLocator.HasFileBasedDirective(
            ["#!/usr/bin/env dotnet", "// a note", "/* done */", "", "#:sdk Microsoft.NET.Sdk"]));

        // The same header over ordinary code is still not a file-based app, and code before the
        // header ends the search: a directive below real statements is not the SDK's to read.
        Assert.False(AppLocator.HasFileBasedDirective(["#!/usr/bin/env dotnet", "System.Console.WriteLine();"]));
        Assert.False(AppLocator.HasFileBasedDirective(["System.Console.WriteLine();", "#:package CuiSharp@*"]));
        Assert.False(AppLocator.HasFileBasedDirective(["/* unterminated", "#:package CuiSharp@*"]));
    }

    [Fact]
    public void AShebangAppIsChosenFromAFolderOfSources()
    {
        using var folder = new Scratch();
        folder.Source("Helpers", "public static class Helpers { public static void Help() { } }");
        string app = folder.Source(
            "App",
            "#!/usr/bin/env dotnet\n#:property TargetFramework=net10.0\nSystem.Console.WriteLine();\n");

        Assert.Equal(app, AppLocator.FindProject(folder.Path));
    }

    [Fact]
    public void AnAppThatWasNeverBuiltIsReportedInsteadOfRunningSomethingElse()
    {
        using var folder = new Scratch();
        folder.Project("Never");

        // --no-build used to search for the newest runnable DLL; with nothing built it now says so.
        var error = Assert.Throws<ToolException>(() =>
            AppLocator.Resolve(folder.Path, noBuild: true, TextWriter.Null));

        Assert.Contains("has not been built", error.Message, StringComparison.Ordinal);
        Assert.Contains("Never.dll", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANamedCsFileIsTheAppEvenBesideAProject()
    {
        using var folder = new Scratch();
        folder.Project("Library");
        string source = folder.Source("Chosen");

        Assert.Equal(source, AppLocator.FindProject(source));
    }

    [Fact]
    public void AProjectFolderWinsOverLooseSources()
    {
        using var folder = new Scratch();
        string project = folder.Project("Shop");
        folder.Source("Program");

        Assert.Equal(project, AppLocator.FindProject(folder.Path));
    }

    [Fact]
    public void TheOnlySourceFileInAProjectlessFolderIsTheApp()
    {
        using var folder = new Scratch();
        string source = folder.Source("Program");

        Assert.Equal(source, AppLocator.FindProject(folder.Path));
    }

    [Fact]
    public void ADirectiveNamesTheAppWhenSeveralSourcesShareTheFolder()
    {
        using var folder = new Scratch();
        folder.Source("Helpers", "public static class Helpers { public static void Help() { } }");
        string app = folder.Source("demo_app", "#:package CuiSharp@*\nSystem.Console.WriteLine();");

        Assert.Equal(app, AppLocator.FindProject(folder.Path));
    }

    [Fact]
    public void SeveralSourcesWithoutDirectivesAreNotGuessed()
    {
        using var folder = new Scratch();
        folder.Source("Alpha");
        folder.Source("Beta");

        var error = Assert.Throws<ToolException>(() => AppLocator.FindProject(folder.Path));
        Assert.Contains("Alpha.cs", error.Message, StringComparison.Ordinal);
        Assert.Contains("Beta.cs", error.Message, StringComparison.Ordinal);
        Assert.Contains("--project", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatIsNotAnAppIsReportedNotCrashed()
    {
        using var folder = new Scratch();
        string notes = Path.Combine(folder.Path, "notes.txt");
        File.WriteAllText(notes, "hello");

        var error = Assert.Throws<ToolException>(() => AppLocator.FindProject(notes));
        Assert.Contains("notes.txt", error.Message, StringComparison.Ordinal);
        Assert.Contains(".csproj", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APathThatDoesNotExistIsReportedNotCrashed()
    {
        string missing = Path.Combine(Path.GetTempPath(), "cui-tool-tests", "no-such-place-" + Guid.NewGuid().ToString("N"));

        var error = Assert.Throws<ToolException>(() => AppLocator.FindProject(missing));
        Assert.Contains("neither a file nor a folder", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileBasedAppBuildsAndItsTargetPathIsVerified()
    {
        // The real thing, not a stub: a package-free file-based app built by the same SDK the
        // tool would use, resolved through the same build-and-describe path a user takes.
        using var folder = new Scratch();
        string source = folder.Source("CuiFileApp" + Guid.NewGuid().ToString("N"), "System.Console.WriteLine();");

        string built = AppLocator.Resolve(source, noBuild: false, TextWriter.Null)!;

        Assert.EndsWith(".dll", built, StringComparison.Ordinal);
        Assert.True(File.Exists(built), $"the build did not produce {built}");
        Assert.True(
            File.Exists(Path.ChangeExtension(built, ".runtimeconfig.json")),
            "the resolved target is not a runnable app");

        // The evaluation the --no-build path uses must find the same target without building.
        Assert.Equal(built, AppLocator.Resolve(source, noBuild: true, TextWriter.Null));
    }

    [Fact]
    public void AFileBasedAppToldNotToBuildIsReportedInsteadOfBuilt()
    {
        using var folder = new Scratch();
        string source = folder.Source("NeverBuilt" + Guid.NewGuid().ToString("N"), "System.Console.WriteLine();");

        var error = Assert.Throws<ToolException>(() => AppLocator.Resolve(source, noBuild: true, TextWriter.Null));
        Assert.Contains("has not been built", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSourceFoldersGlobalJsonSelectsTheSdk()
    {
        // The tool runs the SDK from the app's folder, so a global.json beside the app applies
        // even though the tool process itself sits somewhere else. An SDK nobody has cannot be
        // satisfied, and the version in the failure is the proof the right global.json was read.
        using var folder = new Scratch();
        folder.Source("Program");
        File.WriteAllText(
            Path.Combine(folder.Path, "global.json"),
            """{"sdk":{"version":"99.0.0"}}""");

        var error = Assert.Throws<ToolException>(() =>
            AppLocator.Resolve(folder.Path, noBuild: false, TextWriter.Null));
        Assert.Contains("99.0.0", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A temporary folder of synthetic projects, deleted with the test.</summary>
    private sealed class Scratch : IDisposable
    {
        internal Scratch()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cui-tool-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        internal string Project(string name)
        {
            string file = System.IO.Path.Combine(Path, name + ".csproj");
            File.WriteAllText(file, App);
            return file;
        }

        internal string Source(string name, string content = "System.Console.WriteLine();")
        {
            string file = System.IO.Path.Combine(Path, name + ".cs");
            File.WriteAllText(file, content);
            return file;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp folder is not a test failure.
            }
        }
    }
}
