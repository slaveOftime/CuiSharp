using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
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

    [Theory]
    [InlineData("win-x64", null, "false", "true")]
    [InlineData(null, null, "true", "")]
    // A published app that a tool should drive asks for the channel back, and the size profile is
    // not what provides it: the two switches answer separately.
    [InlineData("win-x64", "-p:CuiSharpHeadless=true", "true", "true")]
    public async Task TheSizeProfileAppliesToASelfContainedPublishAndToNothingElse(
        string? rid, string? overrides, string headless, string profile)
    {
        using var fixture = new NativeFixture();
        var arguments = new List<string> { "msbuild", fixture.Consumer(), "-p:PublishAot=true" };
        if (rid is not null) arguments.Add($"-p:RuntimeIdentifier={rid}");
        if (overrides is not null) arguments.Add(overrides);
        arguments.AddRange(["-getProperty:CuiSharpHeadless", "-getProperty:CuiSharpSizeProfile",
            "-getItem:RuntimeHostConfigurationOption"]);
        var result = await RunAsync([.. arguments]);
        Assert.Equal(0, result.Exit);
        using var json = JsonDocument.Parse(result.Output);
        var properties = json.RootElement.GetProperty("Properties");
        Assert.Equal(headless, properties.GetProperty("CuiSharpHeadless").GetString());
        Assert.Equal(profile, properties.GetProperty("CuiSharpSizeProfile").GetString());

        // The property is only worth anything if it reaches the trimmer as a feature switch: a
        // build that set it and then never wrote the switch would look identical from here.
        var switchItem = Assert.Single(json.RootElement.GetProperty("Items")
            .GetProperty("RuntimeHostConfigurationOption").EnumerateArray());
        Assert.Equal("CuiSharp.Headless", switchItem.GetProperty("Identity").GetString());
        Assert.Equal(headless, switchItem.GetProperty("Value").GetString());
    }

    /// <summary>
    /// The framework switches the profile owns are only set while it is on, so an app that turns it
    /// off gets the object a plain AOT publish would have had. Left empty, the SDK's own default
    /// applies — which is the point: the package stays out of a decision it was told to leave alone.
    /// </summary>
    [Fact]
    public async Task TurningTheProfileOffLeavesTheFrameworkSwitchesToTheSdk()
    {
        using var fixture = new NativeFixture();
        var result = await RunAsync("msbuild", fixture.Consumer(), "-p:PublishAot=true",
            "-p:RuntimeIdentifier=win-x64", "-p:CuiSharpSizeProfile=false",
            "-getProperty:StackTraceSupport", "-getProperty:UseSystemResourceKeys",
            "-getProperty:EventSourceSupport", "-getProperty:CuiSharpHeadless");
        Assert.Equal(0, result.Exit);
        using var json = JsonDocument.Parse(result.Output);
        var properties = json.RootElement.GetProperty("Properties");
        Assert.Empty(properties.GetProperty("StackTraceSupport").GetString()!);
        Assert.Empty(properties.GetProperty("UseSystemResourceKeys").GetString()!);
        Assert.Empty(properties.GetProperty("EventSourceSupport").GetString()!);

        // The channel is not part of the diagnostic set: turning telemetry back on must not quietly
        // put the IO stack back into every binary.
        Assert.Equal("false", properties.GetProperty("CuiSharpHeadless").GetString());
    }

    /// <summary>
    /// CuiSharpVerboseErrors is the app saying it would rather read <c>Argument out of range</c>
    /// than <c>Arg_ResourceKey</c>, and see where it came from. It keeps those two and nothing else.
    /// </summary>
    [Fact]
    public async Task AVerboseAppKeepsItsExceptionMessages()
    {
        using var fixture = new NativeFixture();
        var result = await RunAsync("msbuild", fixture.Consumer(), "-p:PublishAot=true",
            "-p:RuntimeIdentifier=win-x64", "-p:CuiSharpVerboseErrors=true",
            "-getProperty:StackTraceSupport", "-getProperty:UseSystemResourceKeys",
            "-getProperty:EventSourceSupport");
        Assert.Equal(0, result.Exit);
        using var json = JsonDocument.Parse(result.Output);
        var properties = json.RootElement.GetProperty("Properties");
        Assert.Empty(properties.GetProperty("StackTraceSupport").GetString()!);
        Assert.Empty(properties.GetProperty("UseSystemResourceKeys").GetString()!);
        Assert.Equal("false", properties.GetProperty("EventSourceSupport").GetString());
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
        // MSBuild's node reuse leaves worker processes running after the build
        // exits, and they inherit the redirected pipes: ReadToEnd would wait on
        // handles that never close — fifteen idle minutes, or forever. Reuse off,
        // and every read bounded, so a wedge fails instead of parking a release.
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        using var done = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        Task<string> stdout = ReadAsync(process.StandardOutput, done.Token);
        Task<string> stderr = ReadAsync(process.StandardError, done.Token);
        try { await process.WaitForExitAsync(done.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        // The process is gone; its pipes may not be. Ten seconds is enough for
        // the children that die with their parent — partial output still asserts,
        // and a stuck read names itself here instead of hanging the run.
        using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return (process.HasExited ? process.ExitCode : -1,
            await stdout.WaitAsync(drain.Token) + await stderr.WaitAsync(drain.Token));
    }

    // Partial output is the point: a cancelled read keeps what arrived.
    private static async Task<string> ReadAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer, token)) > 0) text.Append(buffer, 0, read);
        }
        catch (OperationCanceledException) { }
        return text.ToString();
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
            // The whole folder, not one file: the package's build targets import each other, and a
            // fixture that copied only the entry point would test a package nobody can use.
            foreach (string file in Directory.GetFiles(Path.Combine(TestPaths.RepositoryRoot, "src", "CuiSharp", "build")))
                File.Copy(file, Path.Combine(build, Path.GetFileName(file)));
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
