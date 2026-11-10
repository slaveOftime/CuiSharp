using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

namespace CuiSharp.Tests;

public sealed class NativePackagingTests
{
    [Theory]
    [InlineData("win-x64", "cui.lib", "user32.lib")]
    [InlineData("win-arm64", "cui.lib", "user32.lib")]
    [InlineData("linux-x64", "libcui.a", "-lX11")]
    [InlineData("linux-arm64", "libcui.a", "-lX11")]
    [InlineData("osx-arm64", "libcui.a", "-framework Cocoa")]
    public async Task AnAotConsumerSelectsOnlyItsOwnArchiveAndBackend(string rid, string archive, string linkerArg)
    {
        using var fixture = new NativeFixture();
        string project = fixture.Consumer();
        var result = await RunAsync("msbuild", project, $"-p:RuntimeIdentifier={rid}", "-p:PublishAot=true",
            "-getItem:NativeLibrary,LinkerArg,DirectPInvoke");
        Assert.Equal(0, result.Exit);
        using var json = JsonDocument.Parse(result.Output);
        var items = json.RootElement.GetProperty("Items");
        var libraries = items.GetProperty("NativeLibrary").EnumerateArray().ToArray();
        Assert.Single(libraries);
        Assert.Equal(Path.GetFullPath(Path.Combine(fixture.Root, "runtimes", rid, "native", archive)),
            libraries[0].GetProperty("FullPath").GetString());
        Assert.Contains(items.GetProperty("LinkerArg").EnumerateArray(),
            item => item.GetProperty("Identity").GetString() == linkerArg);
        Assert.Equal("cui", Assert.Single(items.GetProperty("DirectPInvoke").EnumerateArray()).GetProperty("Identity").GetString());
    }

    [Theory]
    [InlineData("win-arm64")]
    [InlineData("linux-arm64")]
    public async Task AConsumerWithoutAnExplicitRidUsesTheSdkArchitecture(string rid)
    {
        using var fixture = new NativeFixture();
        var result = await RunAsync("msbuild", fixture.Consumer(), $"-p:NETCoreSdkRuntimeIdentifier={rid}",
            "-p:PublishAot=true", "-getProperty:_CuiSharpRid");
        Assert.Equal(0, result.Exit);
        Assert.Equal(rid, result.Output.Trim());
    }

    [Theory]
    [InlineData("linux")]
    [InlineData("win")]
    [InlineData("linux-musl-arm64")]
    [InlineData("win-arm64x")]
    public async Task APrefixOrAnUnsupportedRidFailsBeforeLinking(string rid)
    {
        using var fixture = new NativeFixture();
        var result = await RunAsync("msbuild", fixture.Consumer(), $"-p:RuntimeIdentifier={rid}",
            "-p:PublishAot=true", "-t:CuiSharpRequireStaticArchive");
        Assert.NotEqual(0, result.Exit);
        Assert.Contains("carries no native library", result.Output, StringComparison.Ordinal);
        Assert.Contains(rid, result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("win-arm64", "cui.lib")]
    [InlineData("linux-arm64", "libcui.a")]
    public async Task AMissingArmArchiveDoesNotFallBackToTheX64Archive(string rid, string archive)
    {
        using var fixture = new NativeFixture();
        string project = fixture.Consumer();
        File.Delete(Path.Combine(fixture.Root, "runtimes", rid, "native", archive));
        var result = await RunAsync("msbuild", project, $"-p:RuntimeIdentifier={rid}", "-p:PublishAot=true",
            "-t:CuiSharpRequireStaticArchive");
        Assert.NotEqual(0, result.Exit);
        Assert.Contains("has no static archive", result.Output, StringComparison.Ordinal);
        Assert.Contains(rid, result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("win-arm64")]
    [InlineData("linux-arm64")]
    public async Task AFlatArmBuildIsNotPackagedAsX64(string rid)
    {
        using var fixture = new NativeFixture();
        string native = Path.Combine(fixture.Root, "flat");
        Directory.CreateDirectory(native);
        foreach (string file in NativeFixture.Files(rid)) File.WriteAllText(Path.Combine(native, file), rid + file);
        string package = await fixture.PackAsync($"-p:CuiNativeDir={native}{Path.DirectorySeparatorChar}", $"-p:CuiNativeRid={rid}");
        using var zip = ZipFile.OpenRead(package);
        var packaged = zip.Entries.Where(e => e.FullName.StartsWith("runtimes/", StringComparison.Ordinal) &&
            e.FullName.Contains("/native/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, packaged.Length);
        Assert.All(packaged, e => Assert.StartsWith($"runtimes/{rid}/native/", e.FullName, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("win-arm64", "win-x64")]
    [InlineData("linux-arm64", "linux-x64")]
    public async Task ABuildStampPreventsRelabelingAnArmArchiveAsX64(string builtRid, string packagedRid)
    {
        using var fixture = new NativeFixture();
        string native = Path.Combine(fixture.Root, "flat");
        Directory.CreateDirectory(native);
        foreach (string file in NativeFixture.Files(builtRid)) File.WriteAllText(Path.Combine(native, file), builtRid + file);
        File.WriteAllText(Path.Combine(native, "native-provenance.txt"), "rid=" + builtRid);
        var result = await RunAsync("pack", Path.Combine(TestPaths.RepositoryRoot, "src", "CuiSharp", "CuiSharp.csproj"),
            "-c", "Release", "--no-build", "-o", fixture.Root, "-p:IncludeSymbols=false",
            $"-p:CuiNativeDir={native}{Path.DirectorySeparatorChar}", $"-p:CuiNativeRid={packagedRid}");
        Assert.NotEqual(0, result.Exit);
        Assert.Contains($"declares rid={builtRid}, but would be packaged as {packagedRid}", result.Output, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.nupkg"));
    }

    [Fact]
    public async Task AReleasePackageKeepsFiveDifferentArchivesInTheirOwnRids()
    {
        using var fixture = new NativeFixture();
        fixture.Consumer();
        string Dir(string rid) => Path.Combine(fixture.Root, "runtimes", rid, "native") + Path.DirectorySeparatorChar;
        string package = await fixture.PackAsync(
            $"-p:CuiNativeWinDir={Dir("win-x64")}", $"-p:CuiNativeWinArm64Dir={Dir("win-arm64")}",
            $"-p:CuiNativeLinuxDir={Dir("linux-x64")}", $"-p:CuiNativeLinuxArm64Dir={Dir("linux-arm64")}",
            $"-p:CuiNativeOsxDir={Dir("osx-arm64")}");
        using var zip = ZipFile.OpenRead(package);
        foreach (string rid in CuiNative.RidList)
            foreach (string file in NativeFixture.Files(rid))
            {
                var entry = zip.GetEntry($"runtimes/{rid}/native/{file}");
                Assert.NotNull(entry);
                using var reader = new StreamReader(entry.Open());
                Assert.Equal(rid + file, await reader.ReadToEndAsync());
            }
        using var provenance = new StreamReader(zip.GetEntry("runtimes/native-provenance.txt")!.Open());
        string recorded = await provenance.ReadToEndAsync();
        foreach (string rid in CuiNative.RidList)
        {
            Assert.Contains($"{rid} compiler=fixture-{rid}", recorded, StringComparison.Ordinal);
            foreach (string file in NativeFixture.Files(rid))
            {
                string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rid + file)));
                Assert.Contains($"{rid} {file} {hash}", recorded, StringComparison.OrdinalIgnoreCase);
            }
        }
        var fixes = zip.GetEntry("analyzers/dotnet/cs/CuiSharp.CodeFixes.dll");
        Assert.NotNull(fixes);
        // Identity, not bytes: the package is packed in Release whatever configuration the test ran
        // in, so comparing it with the loaded assembly would only compare build configurations.
        Assert.Equal("CuiSharp.CodeFixes", await PackedAssemblyName(zip, "analyzers/dotnet/cs/CuiSharp.CodeFixes.dll"));
        Assert.Equal("CuiSharp.Analyzers", await PackedAssemblyName(zip, "analyzers/dotnet/cs/CuiSharp.Analyzers.dll"));
        using var manifest = new StreamReader(Assert.Single(zip.Entries, e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).Open());
        Assert.DoesNotContain("<dependency id=\"CuiSharp.CodeFixes\"", await manifest.ReadToEndAsync(), StringComparison.Ordinal);
    }

    private static async Task<string> PackedAssemblyName(ZipArchive zip, string path)
    {
        string directory = Path.Combine(Path.GetTempPath(), "cui-packed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string file = Path.Combine(directory, Path.GetFileName(path));
            using (var entry = Assert.Single(zip.Entries, e => e.FullName == path).Open())
            using (var copy = File.Create(file)) await entry.CopyToAsync(copy);
            return AssemblyName.GetAssemblyName(file).Name!;
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task<(int Exit, string Output)> RunAsync(params string[] arguments)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        return (process.ExitCode, await stdout + await stderr);
    }

    private sealed class NativeFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "cui-native-test-" + Guid.NewGuid().ToString("N"));

        internal static string[] Files(string rid) => rid.StartsWith("win", StringComparison.Ordinal)
            ? ["cui.dll", "cui.lib"] : [rid.StartsWith("osx", StringComparison.Ordinal) ? "libcui.dylib" : "libcui.so", "libcui.a"];

        internal string Consumer()
        {
            string build = Path.Combine(Root, "build");
            Directory.CreateDirectory(build);
            File.Copy(Path.Combine(TestPaths.RepositoryRoot, "src", "CuiSharp", "build", "CuiSharp.targets"), Path.Combine(build, "CuiSharp.targets"));
            foreach (string rid in CuiNative.RidList)
            {
                string native = Path.Combine(Root, "runtimes", rid, "native");
                Directory.CreateDirectory(native);
                foreach (string file in Files(rid)) File.WriteAllText(Path.Combine(native, file), rid + file);
                File.WriteAllText(Path.Combine(native, "native-provenance.txt"), $"compiler=fixture-{rid}\nrid={rid}");
            }
            string project = Path.Combine(Root, "consumer.proj");
            File.WriteAllText(project, "<Project><Import Project=\"build/CuiSharp.targets\" /></Project>");
            return project;
        }

        internal async Task<string> PackAsync(params string[] nativeArguments)
        {
            Directory.CreateDirectory(Root);
            var result = await RunAsync(["pack", Path.Combine(TestPaths.RepositoryRoot, "src", "CuiSharp", "CuiSharp.csproj"),
                "-c", "Release", "--no-build", "-o", Root, "-p:IncludeSymbols=false", .. nativeArguments]);
            Assert.True(result.Exit == 0, result.Output);
            return Assert.Single(Directory.GetFiles(Root, "*.nupkg"));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
