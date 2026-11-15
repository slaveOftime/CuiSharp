using CuiSharp.Tools;

namespace CuiSharp.Tests;

/// <summary>
/// What <c>dotnet cui doctor</c> reads from MSBuild and a restore, and what it says about it. The
/// command is deliberately allowed to answer with warnings: a floating package version and a
/// missing icon are facts about the app, not a reason to hide the rest of the report.
/// </summary>
public class DoctorTests
{
    [Fact]
    public void AFileBasedAppsWildcardVersionIsReportedAsFloating()
    {
        const string report = """
            {
              "Properties": {
                "TargetFramework": "net10.0",
                "ProjectAssetsFile": ""
              },
              "Items": {
                "PackageReference": [
                  { "Identity": "CuiSharp", "Version": "*" }
                ],
                "ProjectReference": []
              }
            }
            """;
        AppLocator.Inspection inspection = AppLocator.ParseInspection(fileBased: true, report);
        var checks = new List<Doctor.Check>();

        Doctor.CheckReferences(checks, inspection, "0.4.2");

        Doctor.Check warning = Assert.Single(checks, check => check.Level == Doctor.Level.Warning);
        Assert.Contains("floats", warning.Message, StringComparison.Ordinal);
        Assert.Contains("pin", warning.Fix, StringComparison.Ordinal);

        var doctorReport = new Doctor.Report("0.4.2", "0.4.2", "app.cs", checks);
        Assert.Contains("\"ok\":true", Doctor.RenderJson(doctorReport, strict: false), StringComparison.Ordinal);
        Assert.Contains("\"ok\":false", Doctor.RenderJson(doctorReport, strict: true), StringComparison.Ordinal);
    }

    [Fact]
    public void ARestoreThatChoseAnotherVersionIsComparedWithTheTool()
    {
        string folder = Directory.CreateTempSubdirectory("cui-doctor-assets-").FullName;
        try
        {
            string assets = Path.Combine(folder, "project.assets.json");
            File.WriteAllText(assets, """
                {
                  "version": 3,
                  "libraries": {
                    "CuiSharp/0.4.3": { "type": "package" },
                    "CuiSharp.Testing/0.4.3": { "type": "package" }
                  }
                }
                """);
            var inspection = new AppLocator.Inspection(
                false,
                new Dictionary<string, string> { ["ProjectAssetsFile"] = assets },
                [new Dictionary<string, string> { ["Identity"] = "CuiSharp", ["Version"] = "0.4.2" }],
                []);
            var checks = new List<Doctor.Check>();

            Doctor.CheckReferences(checks, inspection, "0.4.2");

            // The declared version agrees with the tool, but the restore under the tree does not:
            // the app is about to run code the catalog does not describe.
            Doctor.Check warning = Assert.Single(checks, check => check.Level == Doctor.Level.Warning);
            Assert.Contains("0.4.3", warning.Message, StringComparison.Ordinal);
            Assert.Contains("dotnet tool update", warning.Fix, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AnIconPropertyThatNamesNoFileIsAnError()
    {
        string folder = Directory.CreateTempSubdirectory("cui-doctor-icon-").FullName;
        try
        {
            string project = Path.Combine(folder, "App.csproj");
            File.WriteAllText(project, "<Project />");

            Doctor.Check missing = Doctor.IconCheck(project, "App.ico");
            Assert.Equal(Doctor.Level.Error, missing.Level);
            Assert.Contains("does not exist", missing.Message, StringComparison.Ordinal);

            File.WriteAllBytes(Path.Combine(folder, "App.ico"), [0, 0, 1, 0, 1, 0]);
            Doctor.Check present = Doctor.IconCheck(project, "App.ico");
            Assert.Equal(Doctor.Level.Ok, present.Level);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void NativeAotWarningsAreNamedWithoutQuotingTheWholePublish()
    {
        const string log = """
            App -> C:\src\App.dll
            C:\src\Json.cs(12,5): warning IL2026: Using member 'JsonSerializer.Serialize' is not trim safe.
            C:\src\Json.cs(12,5): warning IL2026: Using member 'JsonSerializer.Serialize' is not trim safe.
            C:\src\Frame.cs(9,1): warning CUI008: reflection-based JSON serialization under AOT.
            C:\src\Other.cs(2,2): warning CS0168: the variable is declared but never used.
            C:\src\App.cs(3,3): error IL3050: A member was found that requires dynamic code.
            """;

        string[] warnings = Doctor.AotWarnings(log);

        Assert.Equal(2, warnings.Length);
        Assert.Contains("IL2026", warnings[0], StringComparison.Ordinal);
        Assert.Contains("CUI008", warnings[1], StringComparison.Ordinal);
    }

    [Fact]
    public void AWindowsAuthoredProjectReferenceIsStillRecognised()
    {
        // A csproj has no reason to rewrite its separators when it is built on Linux.
        Assert.True(Doctor.IsCuiSharpProject(
            new Dictionary<string, string> { ["Identity"] = @"..\..\src\CuiSharp\CuiSharp.csproj" }));
        Assert.True(Doctor.IsCuiSharpProject(
            new Dictionary<string, string> { ["FullPath"] = "/src/CuiSharp/CuiSharp.csproj" }));
        Assert.False(Doctor.IsCuiSharpProject(
            new Dictionary<string, string> { ["Identity"] = "..\\src\\CuiSharp.Testing\\CuiSharp.Testing.csproj" }));
    }

    [Fact]
    public void VersionMetadataDoesNotMakeAnOlderCatalogLookNewer()
    {
        Assert.True(Doctor.SameVersion("0.4.2", "0.4.2"));
        Assert.True(Doctor.SameVersion("0.4.2.0", "0.4.2"));
        Assert.True(Doctor.SameVersion("0.4.2+fc6d4e4", "0.4.2"));
        Assert.False(Doctor.SameVersion("0.4.1", "0.4.2"));
    }

    [Fact]
    public void AotRefusesAnAppThatNeverOptedInInsteadOfForcingTheToolchainOnIt()
    {
        string folder = Directory.CreateTempSubdirectory("cui-doctor-noaot-").FullName;
        try
        {
            string project = Path.Combine(folder, "NoAot.csproj");
            File.WriteAllText(project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="CuiSharp" Version="0.4.2" />
                  </ItemGroup>
                </Project>
                """);
            var output = new StringWriter();
            var error = new StringWriter();

            int exit = Cli.Run(["doctor", "--project", project, "--aot"], output, error);

            Assert.Equal(1, exit);
            Assert.Contains("does not enable PublishAot", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("NativeAOT publish for", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TheDoctorReportsTheProjectAndItsCuiSharpPackage()
    {
        string folder = Directory.CreateTempSubdirectory("cui-doctor-project-").FullName;
        try
        {
            string project = Path.Combine(folder, "DoctorProbe.csproj");
            File.WriteAllText(project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="CuiSharp" Version="0.4.2" />
                  </ItemGroup>
                </Project>
                """);
            var output = new StringWriter();
            var error = new StringWriter();

            int exit = Cli.Run(["doctor", "--project", project, "--json"], output, error);

            Assert.True(exit == 0, $"the package should pass its checks: {error}");
            string json = output.ToString();
            Assert.Contains("\"project\":", json, StringComparison.Ordinal);
            Assert.Contains("DoctorProbe.csproj", json, StringComparison.Ordinal);
            Assert.Contains("CuiSharp 0.4.2 (package)", json, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
