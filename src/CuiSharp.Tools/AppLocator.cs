using System.Diagnostics;
using System.Text;
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
/// <para>
/// A file-based app (a bare <c>.cs</c>) is found the way <c>dotnet run</c> finds one — its
/// <c>#:directive</c> header read past any shebang and comments — and is evaluated with
/// <c>dotnet build … -getProperty:…</c>; <c>dotnet msbuild</c> cannot read it because a .cs file
/// is not XML (MSB4025). Both helper processes run with the source directory as their working
/// directory, so a <c>global.json</c> or <c>NuGet.config</c> beside the app selects the SDK and
/// feeds the restore, exactly as it would for the developer.
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
    /// <param name="path">A project file, a file-based <c>.cs</c> app, or the directory to look in.</param>
    /// <param name="noBuild">Use the last build instead of building.</param>
    /// <param name="log">Where a failed build's own report goes.</param>
    /// <param name="request">Configuration, framework and output directory for the build.</param>
    internal static string? Resolve(string? path, bool noBuild, TextWriter log, BuildRequest? request = null)
    {
        request ??= new BuildRequest();
        string directory;
        try
        {
            directory = Path.GetFullPath(path ?? Directory.GetCurrentDirectory());
        }
        catch (Exception failure) when (failure is ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new ToolException($"`{path}` is not a path this tool can look in: {failure.Message}");
        }

        string? project = FindProject(directory);
        if (project is null) return null;

        if (!noBuild) Build(project, request, log);

        // --no-build must stay honest: Describe only evaluates, so on a .cs app it reads the
        // TargetPath the SDK *would* use without building anything, and a missing file below is
        // the real "never built" answer rather than a secretly rebuilt one.
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

    /// <summary>
    /// The app at <paramref name="path"/>: a named <c>.csproj</c> or <c>.cs</c>, or in a directory
    /// the project there — the only one, or the one that is not a test project when several sit
    /// together — or, with no project at all, the file-based app. Ambiguity and unknown files are
    /// reported, never guessed.
    /// </summary>
    internal static string? FindProject(string path)
    {
        if (File.Exists(path)) return ExplicitProject(path);
        if (!Directory.Exists(path))
        {
            throw new ToolException(
                $"`{path}` is neither a file nor a folder. Name a .csproj, a file-based .cs app, " +
                "or the folder that holds one.");
        }

        // A project wins over loose sources: a folder with a .csproj *is* a project, whatever .cs
        // files sit beside it.
        string[] projects = ListFiles(path, "*.csproj");
        if (projects.Length == 1) return projects[0];
        if (projects.Length > 1)
        {
            // More than one project: a test project is never the app, and the name is all this has
            // to go on. Picking one of two app projects silently can drive the wrong program, so
            // the tool asks instead.
            string[] candidates =
            [
                .. projects.Where(p => !Path.GetFileNameWithoutExtension(p)
                    .Contains("Tests", StringComparison.OrdinalIgnoreCase)),
            ];
            if (candidates.Length == 1) return candidates[0];
            if (candidates.Length > 1)
            {
                throw new ToolException(
                    $"More than one project could be the app: {string.Join(", ", candidates.Select(Path.GetFileName))}. " +
                    "Name one with --project.");
            }

            throw new ToolException(
                $"`{path}` holds only test projects ({string.Join(", ", projects.Select(Path.GetFileName))}). " +
                "Name the app with --project.");
        }

        // No project: a file-based app is the .cs with file directives (`#:package`, `#:sdk`,
        // `#:property`), or — in a folder kept to one source — the only .cs there. Anything
        // looser is a question, not a guess.
        string[] sources = ListFiles(path, "*.cs");
        if (sources.Length == 0) return null;
        string[] withDirectives = [.. sources.Where(IsFileBasedApp)];
        string[] appCandidates = withDirectives.Length > 0 ? withDirectives : sources;
        if (appCandidates.Length == 1) return appCandidates[0];

        throw new ToolException(
            $"More than one C# file could be the app: {string.Join(", ", appCandidates.Select(Path.GetFileName))}. " +
            "Name the one to run with --project.");
    }

    /// <summary>A file the caller named, checked so a wrong one is a message rather than a crash.</summary>
    private static string ExplicitProject(string path)
    {
        if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return path;

        throw new ToolException(
            $"`{Path.GetFileName(path)}` is not something the tool can run. Name a .csproj, a " +
            "file-based .cs app, or the folder that holds one.");
    }

    /// <summary>
    /// Lists a folder, turning an unreadable one into a message about that folder. Silently
    /// returning fewer files here would quietly change which app the tool picks.
    /// </summary>
    private static string[] ListFiles(string directory, string pattern)
    {
        try
        {
            return Directory.GetFiles(directory, pattern);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new ToolException($"Could not look for an app in `{directory}`: {failure.Message}");
        }
    }

    /// <summary>
    /// Whether a source file opens with file-based-app directives before any code, reading them
    /// past the header a real file-based app carries: a <c>#!</c> shebang line, <c>//</c> lines and
    /// <c>/* … */</c> blocks all come before the first <c>#:directive</c>.
    /// </summary>
    private static bool IsFileBasedApp(string file)
    {
        try
        {
            return HasFileBasedDirective(File.ReadLines(file));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // Guessing here would decide the app on the strength of a file it could not read.
            throw new ToolException(
                $"Could not read `{Path.GetFileName(file)}` to see whether it is the app: {failure.Message}");
        }
    }

    /// <summary>The directive test on lines, so the header handling is testable without a disk.</summary>
    internal static bool HasFileBasedDirective(IEnumerable<string> lines)
    {
        bool insideBlockComment = false;
        foreach (string raw in lines)
        {
            string text = raw;
            while (true)
            {
                if (insideBlockComment)
                {
                    int closed = text.IndexOf("*/", StringComparison.Ordinal);
                    if (closed < 0) break;          // the whole line is still comment
                    text = text[(closed + 2)..];
                    insideBlockComment = false;
                    continue;
                }

                text = text.TrimStart();
                // A shebang or a line comment says nothing: it is the line, not the first content.
                if (text.Length == 0
                    || text.StartsWith("#!", StringComparison.Ordinal)
                    || text.StartsWith("//", StringComparison.Ordinal))
                {
                    break;
                }

                if (text.StartsWith("/*", StringComparison.Ordinal))
                {
                    int closed = text.IndexOf("*/", 2, StringComparison.Ordinal);
                    if (closed < 0)
                    {
                        insideBlockComment = true;
                        break;
                    }

                    text = text[(closed + 2)..];
                    continue;
                }

                // First real content: a file-based app names itself with a directive here.
                return text.StartsWith("#:package", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith("#:sdk", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith("#:property", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    /// <summary>What a project builds, as MSBuild itself evaluates it.</summary>
    internal sealed record BuildTarget(string TargetPath, string Framework, string Frameworks, string Rid);

    /// <summary>
    /// Asks the SDK where the app lands. Evaluation only, so this answers for a project that was
    /// built by something else — a CI build, a publish, an IDE — and never builds anything itself.
    /// A <c>.cs</c> file is evaluated with <c>dotnet build -getProperty:</c>; <c>dotnet msbuild</c>
    /// refuses it (MSB4025) because a .cs file is not XML.
    /// </summary>
    internal static BuildTarget Describe(string project, BuildRequest request)
    {
        bool fileBased = project.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
        List<string> arguments = fileBased
            ? ["build", project, "-c", request.Configuration, "--nologo"]
            : ["msbuild", project, "-nologo", $"-p:Configuration={request.Configuration}"];
        arguments.AddRange(
        [
            "-getProperty:TargetPath",
            "-getProperty:TargetFramework",
            "-getProperty:TargetFrameworks",
            "-getProperty:RuntimeIdentifier",
        ]);
        if (request.Framework is not null) arguments.Add($"-p:TargetFramework={request.Framework}");
        if (request.BaseOutputPath is not null) arguments.Add($"-p:BaseOutputPath={request.BaseOutputPath}");

        (int exit, string report, string complaints) = Run("dotnet", arguments, SourceDirectory(project));
        if (exit != 0)
        {
            // MSBuild reports some failures (and the SDK resolution error of a bad global.json)
            // on stdout, some on stderr: the user has to see both, not whichever pipe happened
            // to carry the one line that explains the failure.
            throw new ToolException(
                $"Could not read what `{Path.GetFileName(project)}` builds:" +
                $"{Environment.NewLine}{complaints.Trim()}{Environment.NewLine}{report.Trim()}");
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
        if (!text.StartsWith('{') && !text.StartsWith('['))
        {
            // A single property: the value, or nothing when MSBuild had none to give. Anything
            // JSON-shaped goes to the reader, which reports a shape it cannot use.
            return new BuildTarget(text, framework ?? "", "", "");
        }

        Dictionary<string, string> properties;
        try
        {
            properties = ReadProperties(text);
        }
        catch (JsonException malformed)
        {
            throw new ToolException(UnreadableAnswer(text, malformed.Message));
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
    /// The <c>Properties</c> object of MSBuild's JSON answer. Anything shaped differently — an
    /// array where the object belongs, a number where a string belongs — is a host or version
    /// problem and is answered with the text that proves it, not with a framework exception.
    /// </summary>
    private static Dictionary<string, string> ReadProperties(string text)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        using JsonDocument document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ToolException(UnreadableAnswer(
                text, $"the answer is {document.RootElement.ValueKind}, not an object"));
        }

        if (!document.RootElement.TryGetProperty("Properties", out JsonElement found)) return properties;
        if (found.ValueKind != JsonValueKind.Object)
        {
            throw new ToolException(UnreadableAnswer(
                text, $"`Properties` is {found.ValueKind}, not an object of properties"));
        }

        foreach (JsonProperty property in found.EnumerateObject())
        {
            if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                throw new ToolException(UnreadableAnswer(
                    text, $"`{ShortName(property.Name)}` is {property.Value.ValueKind}, not a string"));
            }

            properties[property.Name] = property.Value.GetString() ?? "";
        }

        return properties;
    }

    /// <summary>
    /// The one failure wording for an answer this tool cannot read. Both halves of it come from
    /// outside — the quoted text and any name quoted inside the reason — so the whole diagnostic
    /// carries one budget: the reason is what the user acts on, the quote is only evidence.
    /// </summary>
    private static string UnreadableAnswer(string text, string why)
    {
        string diagnostic =
            $"Could not read MSBuild's answer about what the project builds ({why}):" +
            $"{Environment.NewLine}" +
            ShortText(text);
        return diagnostic.Length > DiagnosticBudget ? diagnostic[..DiagnosticBudget] + "…" : diagnostic;
    }

    /// <summary>
    /// How much of a broken answer gets quoted, how long the whole message may be, and how long a
    /// name quoted inside it may be.
    /// </summary>
    private const int ExcerptBudget = 400;
    private const int DiagnosticBudget = 640;
    private const int NameBudget = 60;

    /// <summary>
    /// A name out of a broken answer, quoted up to a bound. It identifies the property, and past
    /// that bound it is just bytes the answer happened to contain.
    /// </summary>
    private static string ShortName(string name) => ShortText(name, NameBudget);

    /// <summary>
    /// Text from outside this tool — a build report, one exception's message — quoted up to a
    /// bound, so no one can decide how long the tool's own diagnosis is.
    /// </summary>
    private static string ShortText(string text, int budget = ExcerptBudget) =>
        text.Length > budget ? text[..budget] + "…" : text;

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
        (int exit, string report, string complaints) = Run("dotnet", arguments, SourceDirectory(project));
        if (exit != 0)
        {
            log.Write(report);
            throw new ToolException($"`{Path.GetFileName(project)}` did not build:" +
                $"{Environment.NewLine}{complaints.Trim()}{Environment.NewLine}{report.Trim()}");
        }
    }

    /// <summary>The folder the SDK should be run from: the one holding the app's configuration.</summary>
    private static string SourceDirectory(string project) =>
        Path.GetDirectoryName(Path.GetFullPath(project)) ?? Directory.GetCurrentDirectory();

    /// <summary>
    /// Runs a helper process, draining both of its outputs at once. The outputs are read as UTF-8
    /// whatever this console's code page is: a build report with a non-ASCII path or message must
    /// arrive intact, on Windows especially.
    /// </summary>
    private static (int Exit, string Out, string Error) Run(string host, IReadOnlyList<string> arguments, string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workingDirectory is not null) info.WorkingDirectory = workingDirectory;
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (string argument in arguments) info.ArgumentList.Add(argument);

        // A helper that cannot start or whose pipes break is a diagnosis the user can act on —
        // "dotnet is not on the PATH", a deleted folder — not a stack trace. Only the IO-shaped
        // failures are translated; anything else is a bug and stays visible.
        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException($"`{host}` produced no process.");
        }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ToolException($"Could not run `{host} {string.Join(' ', arguments)}`: {failure.Message}");
        }

        using (process)
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            try
            {
                Task.WaitAll(output, errors);
                process.WaitForExit();
            }
            catch (Exception failure)
            {
                // The child is stopped first, whatever the failure turns out to be: disposing a
                // Process does not stop it, and a hung dotnet must not outlive the message. What
                // stopping failed with, if anything, is carried alongside the original rather
                // than instead of it — the failure that started this is the one to act on.
                Exception? cleanup = StopHelper(process);

                if (DrainFailure(host, arguments, failure, cleanup) is { } broken) throw broken;

                // Not the child's doing, and nothing left over: this tool's own bug, rethrown so
                // the stack that says where survives.
                throw;
            }

            return (process.ExitCode, output.Result, errors.Result);
        }
    }

    /// <summary>
    /// Stops a helper process that outlived its output and hands back what stopping failed with,
    /// if anything. The caller is already failing for another reason: swallowing here would hide
    /// a child that is still running, and letting it fly would replace the failure that matters.
    /// </summary>
    private static Exception? StopHelper(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            return null;
        }
        catch (Exception gone)
            when (gone is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException
                or AggregateException)
        {
            // Exactly what Kill is documented to throw for: nothing to stop, the process could
            // not be terminated, it is not a local process, or not every descendant went down
            // with it. The try holds the Kill and nothing else, so nothing of ours hides here.
            return gone;
        }
    }

    /// <summary>
    /// What a helper whose output could not be read ends up throwing, once the attempt to stop it
    /// is accounted for — or null when the caller must rethrow the failure itself because it is
    /// not the helper's doing.
    /// <list type="bullet">
    /// <item>Every failure the child's own: a <see cref="ToolException"/> saying so, with the
    /// cleanup failure named too when stopping the child failed, since a stray process is the
    /// user's problem now.</item>
    /// <item>A bug among them and no cleanup failure: null, so the caller's bare rethrow keeps the
    /// stack that explains the bug.</item>
    /// <item>A bug and a failed cleanup: an <see cref="AggregateException"/> carrying both, with
    /// the bug first — neither story is dropped and neither replaces the other.</item>
    /// </list>
    /// </summary>
    internal static Exception? DrainFailure(
        string host, IReadOnlyList<string> arguments, Exception failure, Exception? cleanup)
    {
        string? diagnosis = DrainDiagnosis(host, arguments, failure);
        if (diagnosis is not null)
        {
            return new ToolException(cleanup is null
                ? diagnosis
                : diagnosis + $" Stopping it failed too: {ShortText(cleanup.Message)}");
        }

        return cleanup is null ? null : new AggregateException(failure, cleanup);
    }

    /// <summary>
    /// The user-actionable wording when every one of a helper's failures is the helper's own — its
    /// pipe breaking, the child going away — and null when one of them is not, which means this
    /// tool broke and dressing the failure up as a build problem would hide where. An aggregate
    /// that names nothing proves nothing.
    /// </summary>
    private static string? DrainDiagnosis(string host, IReadOnlyList<string> arguments, Exception failure)
    {
        Exception[] inners = failure is AggregateException aggregate
            ? [.. aggregate.Flatten().InnerExceptions]
            : [failure];
        if (inners.Length == 0 || inners.Any(e => e is not (IOException or InvalidOperationException)))
            return null;

        return $"`{host} {string.Join(' ', arguments)}` broke while its output was read: " +
            $"{ShortText(inners[0].Message)}";
    }
}
