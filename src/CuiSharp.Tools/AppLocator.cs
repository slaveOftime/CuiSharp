using System.Diagnostics;
using System.Text.Json;

namespace CuiSharp.Tools;

/// <summary>
/// Finds the app: builds the project next to the caller (or the one it named) and answers the
/// assembly to run.
/// </summary>
/// <remarks>
/// <para>
/// A published executable is used as it is — an agent checking a release build must inspect the
/// release build. Otherwise the project is built in Release, because the frame of a Debug build
/// is not the frame a user sees.
/// </para>
/// <para>
/// The assembly to run is what MSBuild evaluates <c>TargetPath</c> to, asked for explicitly with
/// the configuration, the framework and the RID the build used. Guessing it — the newest DLL with
/// a <c>runtimeconfig.json</c> under <c>bin/Release</c> — ran the wrong app whenever a dependency
/// was newer than the app, and could not tell a RID-specific folder from an ordinary one.
/// </para>
/// </remarks>
internal static class AppLocator
{
    /// <summary>How to build the app: which configuration, and where its output goes.</summary>
    /// <param name="Configuration">MSBuild configuration. Release unless a session says otherwise.</param>
    /// <param name="Framework">The target framework, when the project has more than one.</param>
    /// <param name="BaseOutputPath">
    /// Where the build writes, when a session wants the app's binary to itself: a dev session must
    /// not overwrite the Release build a test or a publish is using.
    /// </param>
    internal sealed record BuildRequest(
        string Configuration = "Release",
        string? Framework = null,
        string? BaseOutputPath = null);

    /// <summary>The assembly to run, or null when there is no project here.</summary>
    /// <param name="path">A project file, or the directory to look in.</param>
    /// <param name="noBuild">Use the last build instead of building.</param>
    /// <param name="log">Where a failed build's own report goes.</param>
    /// <param name="request">Configuration, framework and output directory for the build.</param>
    internal static string? Resolve(string? path, bool noBuild, TextWriter log, BuildRequest? request = null)
    {
        request ??= new BuildRequest();
        string directory = Path.GetFullPath(path ?? Directory.GetCurrentDirectory());
        string? project = FindProject(directory);
        if (project is null) return null;

        if (!noBuild) Build(project, request, log);

        BuildTarget target = Describe(project, request);
        if (target.TargetPath.Length == 0)
            throw new ToolException($"`{Path.GetFileName(project)}` did not say what it builds (`TargetPath` is empty).");
        if (!File.Exists(target.TargetPath))
        {
            throw new ToolException(
                $"`{Path.GetFileName(project)}` has not been built: {target.TargetPath} is missing. " +
                "Build it, or drop --no-build.");
        }

        // A library has no runtimeconfig, so it is not something that can be started.
        if (!File.Exists(Path.ChangeExtension(target.TargetPath, ".runtimeconfig.json")))
        {
            throw new ToolException(
                $"`{Path.GetFileName(project)}` builds a library, not an app: {target.TargetPath} has no " +
                "runtimeconfig. Point the tool at the app project.");
        }

        return target.TargetPath;
    }

    /// <summary>A project file in <paramref name="directory"/>, preferring the only one there.</summary>
    internal static string? FindProject(string directory)
    {
        if (File.Exists(directory) && directory.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return directory;

        string[] projects = Directory.GetFiles(directory, "*.csproj");
        if (projects.Length == 0) return null;
        if (projects.Length == 1) return projects[0];

        // A test project is never the app. Everything else is a real question: picking one of two
        // app projects silently can drive the wrong program, so the tool asks instead.
        string[] candidates =
        [
            .. projects.Where(p => !Path.GetFileNameWithoutExtension(p)
                .Contains("Tests", StringComparison.OrdinalIgnoreCase)),
        ];
        if (candidates.Length == 1) return candidates[0];

        throw new ToolException(
            $"More than one project could be the app: {string.Join(", ", candidates.Select(Path.GetFileName))}. " +
            "Name one with --project.");
    }

    /// <summary>What a project builds, as MSBuild itself evaluates it.</summary>
    internal sealed record BuildTarget(string TargetPath, string Framework, string Frameworks, string Rid);

    /// <summary>
    /// Asks MSBuild where the app lands. Evaluation only, so this answers for a project that was
    /// built by something else — a CI build, a publish, an IDE.
    /// </summary>
    internal static BuildTarget Describe(string project, BuildRequest request)
    {
        List<string> arguments =
        [
            "msbuild", project, "-nologo",
            "-getProperty:TargetPath",
            "-getProperty:TargetFramework",
            "-getProperty:TargetFrameworks",
            "-getProperty:RuntimeIdentifier",
            $"-p:Configuration={request.Configuration}",
        ];
        if (request.Framework is not null) arguments.Add($"-p:TargetFramework={request.Framework}");
        if (request.BaseOutputPath is not null) arguments.Add($"-p:BaseOutputPath={request.BaseOutputPath}");

        (int exit, string report, string complaints) = Run("dotnet", arguments);
        if (exit != 0)
        {
            throw new ToolException(
                $"Could not read what `{Path.GetFileName(project)}` builds:{Environment.NewLine}{complaints.Trim()}");
        }

        return ParseDescription(report, request.Framework);
    }

    /// <summary>
    /// The properties out of MSBuild's own text. One property prints its value, several print a
    /// JSON object; both are answered, so the parsing is testable without a project on disk.
    /// </summary>
    internal static BuildTarget ParseDescription(string report, string? framework)
    {
        string text = report.Trim();
        if (!text.StartsWith('{'))
        {
            // A single property: the value, or nothing when MSBuild had none to give.
            return new BuildTarget(text, framework ?? "", "", "");
        }

        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        using (JsonDocument document = JsonDocument.Parse(text))
        {
            if (document.RootElement.TryGetProperty("Properties", out JsonElement found))
            {
                foreach (JsonProperty property in found.EnumerateObject())
                    properties[property.Name] = property.Value.GetString() ?? "";
            }
        }

        string value(string name) => properties.GetValueOrDefault(name, "");
        string frameworks = value("TargetFrameworks");
        string resolved = value("TargetFramework");
        if (frameworks.Length > 0 && resolved.Length == 0)
        {
            throw new ToolException(
                $"The project targets several frameworks ({frameworks}). Choose one with --framework.");
        }

        return new BuildTarget(value("TargetPath"), resolved, frameworks, value("RuntimeIdentifier"));
    }

    /// <summary>
    /// Builds the project in Release. A quiet build says nothing on success, so the report is kept
    /// only for the failure message.
    /// </summary>
    private static void Build(string project, BuildRequest request, TextWriter log)
    {
        List<string> arguments =
            ["build", project, "-c", request.Configuration, "--nologo", "-v", "quiet"];
        if (request.Framework is not null) arguments.AddRange(["-f", request.Framework]);
        if (request.BaseOutputPath is not null) arguments.Add($"-p:BaseOutputPath={request.BaseOutputPath}");

        // The package compiles the headless channel out of anything it publishes (docs/SIZE.md).
        // This build is the one place that must have it, and saying so here means the tool keeps
        // working whatever the package's default becomes.
        arguments.Add("-p:CuiSharpHeadless=true");

        // Both outputs are drained at the same time: reading one to the end before the other
        // deadlocks a child that fills the pipe it is not being read from, which is exactly what a
        // failing build does.
        (int exit, string report, string complaints) = Run("dotnet", arguments);
        if (exit != 0)
        {
            log.Write(report);
            throw new ToolException($"`{Path.GetFileName(project)}` did not build:" +
                $"{Environment.NewLine}{complaints.Trim()}{Environment.NewLine}{report.Trim()}");
        }
    }

    /// <summary>Runs a helper process, draining both of its outputs at once.</summary>
    private static (int Exit, string Out, string Error) Run(string host, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);

        using Process? process = Process.Start(info);
        if (process is null) throw new ToolException($"Could not run `{host} {string.Join(' ', arguments)}`.");

        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        Task.WaitAll(output, errors);
        process.WaitForExit();

        return (process.ExitCode, output.Result, errors.Result);
    }
}
