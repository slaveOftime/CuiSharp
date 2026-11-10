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
