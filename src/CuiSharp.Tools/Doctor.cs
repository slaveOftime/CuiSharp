using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CuiSharp.Tools;

/// <summary>
/// <c>dotnet cui doctor</c>: the questions that decide whether this tool and an app agree before
/// a screenshot or a bug report is worth taking — the tool's own version, the reference the app
/// resolves, the icon it thinks it ships, and whether a NativeAOT publish is configured (and, with
/// <c>--aot</c>, what that publish actually warned about).
/// <para>
/// The checks are deliberately separate from the driver: doctor evaluates a project with MSBuild
/// and reads one restore file, but starts no app and needs no native library.
/// </para>
/// </summary>
internal static class Doctor
{
    internal enum Level
    {
        Ok,
        Info,
        Warning,
        Error,
    }

    internal sealed record Check(string Name, Level Level, string Message, string? Fix = null);

    internal sealed record Report(
        string Tool,
        string Catalog,
        string? Project,
        IReadOnlyList<Check> Checks)
    {
        internal bool Failed => Checks.Any(check => check.Level == Level.Error);

        internal bool Warned => Checks.Any(check => check.Level == Level.Warning);
    }

    /// <summary>Runs the checks and writes the report; the return value is the process exit code.</summary>
    internal static int Run(Cli.Options options, TextWriter output)
    {
        Report report = Examine(options);
        output.WriteLine(options.Json ? RenderJson(report, options.Strict) : Render(report, options.Strict));
        return report.Failed || (options.Strict && report.Warned) ? 1 : 0;
    }

    /// <summary>Everything doctor knows, without a process and without a frame.</summary>
    internal static Report Examine(Cli.Options options)
    {
        string tool = DocLibrary.Version;
        string catalog;
        var checks = new List<Check>();
        try
        {
            catalog = Cli.Catalog.Version;
            checks.Add(new Check(
                "tool",
                SameVersion(tool, catalog) ? Level.Ok : Level.Error,
                SameVersion(tool, catalog)
                    ? $"CuiSharp.Tools {tool}, API catalog {catalog}"
                    : $"CuiSharp.Tools is {tool} but its bundled API catalog is {catalog}: this install is inconsistent",
                SameVersion(tool, catalog)
                    ? null
                    : "reinstall the tool: dotnet tool update -g CuiSharp.Tools"));
        }
        catch (ToolException broken)
        {
            catalog = "unknown";
            checks.Add(new Check("tool", Level.Error, broken.Message, "reinstall the tool: dotnet tool update -g CuiSharp.Tools"));
        }

        string? project = null;
        try
        {
            project = AppLocator.FindProject(options.Project ?? Directory.GetCurrentDirectory());
        }
        catch (ToolException failure)
        {
            checks.Add(new Check("project", Level.Error, failure.Message, "name the app with --project"));
            return new Report(tool, catalog, null, checks);
        }

        if (project is null)
        {
            checks.Add(new Check(
                "project",
                Level.Error,
                $"no .csproj and no file-based .cs app at {DisplayPath(options.Project ?? Directory.GetCurrentDirectory())}",
                "name the app with --project <path>"));
            return new Report(tool, catalog, null, checks);
        }

        AppLocator.Inspection inspection;
        try
        {
            inspection = AppLocator.Inspect(
                project,
                new AppLocator.BuildRequest(Framework: options.Framework),
                RuntimeInformation.RuntimeIdentifier);
        }
        catch (ToolException failure)
        {
            checks.Add(new Check("project", Level.Error, failure.Message, "fix the project, then run doctor again"));
            return new Report(tool, catalog, Path.GetFullPath(project), checks);
        }

        string framework = inspection.Property("TargetFramework");
        string frameworks = inspection.Property("TargetFrameworks");
        string kind = inspection.FileBased ? "file-based app" : "project";
        checks.Add(new Check(
            "project",
            Level.Ok,
            $"{DisplayPath(project)} ({kind}{(framework.Length > 0 ? ", " + framework : "")})"));

        if (frameworks.Length > 0 && framework.Length == 0)
        {
            checks.Add(new Check(
                "project",
                Level.Error,
                $"targets several frameworks ({frameworks}); doctor reads one at a time",
                "choose one: dotnet cui doctor --framework <tfm>"));
        }

        CheckReferences(checks, inspection, catalog);
        checks.Add(IconCheck(project, inspection.Property("ApplicationIcon")));
        checks.AddRange(AotChecks(project, inspection, options, framework));
        return new Report(tool, catalog, Path.GetFullPath(project), checks);
    }

    /// <summary>
    /// The app's CuiSharp reference. A package is the normal case; a project reference is how the
    /// repository builds its own samples, and it is not a version disagreement when the source is
    /// what the app runs.
    /// </summary>
    internal static void CheckReferences(
        List<Check> checks, AppLocator.Inspection inspection, string catalog)
    {
        List<IReadOnlyDictionary<string, string>> packages = [.. inspection.PackagesNamed("CuiSharp")];
        List<IReadOnlyDictionary<string, string>> projects =
            [.. inspection.ProjectReferences.Where(IsCuiSharpProject)];

        if (packages.Count == 0 && projects.Count == 0)
        {
            checks.Add(new Check(
                "reference",
                Level.Error,
                "the app has no CuiSharp package or project reference",
                "add one: dotnet add package CuiSharp"));
            return;
        }

        string? resolved = ResolvedVersion(inspection.Property("ProjectAssetsFile"));
        foreach (IReadOnlyDictionary<string, string> package in packages)
        {
            string declared = FirstNonEmpty(
                package.GetValueOrDefault("Version", ""),
                package.GetValueOrDefault("VersionOverride", ""));
            bool floating = declared.Length == 0 || declared.Contains('*');

            if (declared.Length == 0)
            {
                checks.Add(new Check(
                    "reference",
                    Level.Info,
                    "CuiSharp's version is supplied outside PackageReference (central package management?)",
                    null));
            }
            else if (floating)
            {
                string now = resolved is null ? "" : $"; the last restore resolved {resolved}";
                checks.Add(new Check(
                    "reference",
                    Level.Warning,
                    $"CuiSharp@{declared} floats to the newest stable{now}",
                    "pin it for a build that does not move under you: #:package CuiSharp@<version> or <PackageReference Version=\"<version>\">"));
            }
            else
            {
                checks.Add(new Check("reference", Level.Ok, $"CuiSharp {declared} (package)"));
            }

            string effective = resolved ?? (floating ? "" : declared);
            if (effective.Length > 0 && !SameVersion(effective, catalog))
            {
                checks.Add(new Check(
                    "reference",
                    Level.Warning,
                    $"the app resolves CuiSharp {effective}, but this tool carries the {catalog} API catalog",
                    $"update the tool: dotnet tool update -g CuiSharp.Tools --version {effective}"));
            }
        }

        foreach (IReadOnlyDictionary<string, string> reference in projects)
        {
            string path = reference.GetValueOrDefault("FullPath", reference.GetValueOrDefault("Identity", "CuiSharp.csproj"));
            checks.Add(new Check("reference", Level.Ok, $"CuiSharp project reference: {DisplayPath(path)}"));
        }

        string localVersion = inspection.Property("CuiSharpVersion");
        if (projects.Count > 0 && localVersion.Length > 0 && !SameVersion(localVersion, catalog))
        {
            checks.Add(new Check(
                "reference",
                Level.Warning,
                $"the project's CuiSharpVersion is {localVersion}, but this tool carries the {catalog} API catalog",
                $"update the tool: dotnet tool update -g CuiSharp.Tools --version {localVersion}"));
        }
    }

    /// <summary>
    /// Whether a project reference names CuiSharp. The full path MSBuild evaluated is preferred;
    /// the raw identity is normalised because a Windows-authored csproj keeps backslashes when it
    /// is read on Linux or macOS.
    /// </summary>
    internal static bool IsCuiSharpProject(IReadOnlyDictionary<string, string> reference)
    {
        string full = reference.GetValueOrDefault("FullPath", "");
        string candidate = (full.Length > 0 ? full : reference.GetValueOrDefault("Identity", ""))
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFileNameWithoutExtension(candidate).Equals("CuiSharp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The executable icon: a path the build accepts but a file that is not there is a warning on
    /// a good day and a publish failure on Windows, so it is checked as a file, not a property.
    /// </summary>
    internal static Check IconCheck(string project, string icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
        {
            return new Check(
                "icon",
                Level.Info,
                "no ApplicationIcon; the executable uses the platform default",
                null);
        }

        string directory = Path.GetDirectoryName(Path.GetFullPath(project)) ?? Directory.GetCurrentDirectory();
        string relative = icon
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(directory, relative));
        }
        catch (Exception failure) when (failure is ArgumentException or IOException or NotSupportedException)
        {
            return new Check(
                "icon",
                Level.Error,
                $"ApplicationIcon `{icon}` is not a path this machine can resolve: {failure.Message}",
                "point ApplicationIcon at a .ico beside the project");
        }

        if (!File.Exists(full))
        {
            return new Check(
                "icon",
                Level.Error,
                $"ApplicationIcon `{icon}` points at {DisplayPath(full)}, which does not exist",
                "restore the .ico or remove ApplicationIcon");
        }

        return new Check("icon", Level.Ok, $"{DisplayPath(full)} ({new FileInfo(full).Length.ToString("N0", CultureInfo.InvariantCulture)} B)");
    }

    /// <summary>
    /// NativeAOT configuration, and with <c>--aot</c> the publish itself. The base check reads the
    /// property a publish with the current RID would see, so the templates' condition on
    /// <c>RuntimeIdentifier</c> is judged the way the build judges it.
    /// </summary>
    private static IEnumerable<Check> AotChecks(
        string project, AppLocator.Inspection inspection, Cli.Options options, string framework)
    {
        bool configured = IsTrue(inspection.Property("PublishAot"));
        string rid = FirstNonEmpty(inspection.Property("RuntimeIdentifier"), RuntimeInformation.RuntimeIdentifier);
        if (!configured)
        {
            yield return new Check(
                "aot",
                options.Aot ? Level.Error : Level.Info,
                options.Aot
                    ? "this app does not enable PublishAot, so --aot has no native compilation to inspect"
                    : "PublishAot is off, so a publish keeps the .NET runtime beside the app",
                "add <PublishAot>true</PublishAot> under the RuntimeIdentifier condition, or #:property PublishAot=true");
            yield break;
        }

        yield return new Check(
            "aot",
            Level.Ok,
            options.Aot
                ? $"PublishAot for {rid}; IL2xxx/IL3xxx warnings appear only in a publish"
                : $"PublishAot for {rid}; run `dotnet cui doctor --aot` to check IL2xxx/IL3xxx warnings");

        if (!options.Aot) yield break;

        if (framework.Length == 0)
        {
            yield return new Check(
                "aot",
                Level.Error,
                "cannot publish without a resolved target framework",
                "choose one: dotnet cui doctor --framework <tfm> --aot");
            yield break;
        }

        DirectoryInfo scratch = Directory.CreateTempSubdirectory("cui-doctor-");
        try
        {
            (int exit, string report, string complaints) = AppLocator.PublishAot(
                project, new AppLocator.BuildRequest(Framework: options.Framework), rid, scratch.FullName);
            string[] warnings = AotWarnings(report + Environment.NewLine + complaints);

            if (exit != 0)
            {
                yield return new Check(
                    "aot",
                    Level.Error,
                    $"NativeAOT publish for {rid} failed:{Environment.NewLine}{Clip((complaints + Environment.NewLine + report).Trim())}",
                    $"run it yourself for the full log: dotnet publish -c Release -r {rid}");
            }
            else if (warnings.Length == 0)
            {
                yield return new Check("aot", Level.Ok, $"NativeAOT publish for {rid}: no IL2xxx/IL3xxx warnings");
            }
            else
            {
                for (int i = 0; i < Math.Min(warnings.Length, 12); i++)
                {
                    yield return new Check("aot", Level.Warning, Clip(warnings[i], 320));
                }

                if (warnings.Length > 12)
                {
                    yield return new Check("aot", Level.Warning, $"{warnings.Length - 12} more AOT warnings; run the publish for the full log");
                }
            }
        }
        finally
        {
            try
            {
                scratch.Delete(recursive: true);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // A temporary publish left behind is not a reason to lose the report it produced.
            }
        }
    }

    /// <summary>
    /// The NativeAOT, trim and CuiSharp analyzer warnings in a publish log, de-duplicated in the
    /// order they appeared. Only warnings a person can act on by name are kept; the rest of a
    /// verbose publish would drown the one line that matters.
    /// </summary>
    internal static string[] AotWarnings(string report)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        foreach (string raw in report.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (!Regex.IsMatch(line, @"\bwarning\s+(IL[23]\d{3}|CUI\d{3})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                continue;
            if (seen.Add(line)) warnings.Add(line);
        }

        return [.. warnings];
    }

    /// <summary>The version a restore actually chose, from the assets file the evaluation named.</summary>
    internal static string? ResolvedVersion(string assetsFile)
    {
        if (string.IsNullOrEmpty(assetsFile) || !File.Exists(assetsFile)) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(assetsFile));
            if (!document.RootElement.TryGetProperty("libraries", out JsonElement libraries)
                || libraries.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (JsonProperty library in libraries.EnumerateObject())
            {
                const string prefix = "CuiSharp/";
                if (library.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return library.Name[prefix.Length..];
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException)
        {
            // A stale or half-written assets file cannot answer, and doctor still has the declared
            // reference and the tool version to compare; the tree is not worth a stack trace.
        }

        return null;
    }

    /// <summary>
    /// Whether two package versions name the same build. File versions carry an extra zero
    /// (`0.4.2.0`), and a restore may carry a prerelease suffix; those are not a mismatch.
    /// </summary>
    internal static bool SameVersion(string left, string right)
    {
        static string Normalize(string value)
        {
            string version = value.Trim();
            if (version.StartsWith('v') || version.StartsWith('V')) version = version[1..];

            int metadata = version.IndexOf('+');
            if (metadata >= 0) version = version[..metadata];

            string numeric = version;
            string suffix = "";
            int prerelease = version.IndexOf('-');
            if (prerelease >= 0)
            {
                numeric = version[..prerelease];
                suffix = version[prerelease..];
            }

            if (Version.TryParse(numeric, out Version? parsed))
            {
                numeric = parsed.Revision > 0 ? parsed.ToString(4)
                    : parsed.Build >= 0 ? parsed.ToString(3)
                    : parsed.ToString(2);
            }

            return numeric + suffix;
        }

        return Normalize(left).Equals(Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTrue(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("1", StringComparison.Ordinal);

    private static string FirstNonEmpty(string first, string second) =>
        first.Length > 0 ? first : second;

    private static string Clip(string text, int budget = 1_200) =>
        text.Length > budget ? text[..budget] + "…" : text;

    /// <summary>A path a person can read: relative while it is under the current directory.</summary>
    internal static string DisplayPath(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), full);
            if (relative == ".") return ".";
            bool outside = relative == ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Path.IsPathRooted(relative);
            return outside ? full : relative;
        }
        catch (Exception failure) when (failure is ArgumentException or IOException or NotSupportedException)
        {
            return path;
        }
    }

    internal static string Render(Report report, bool strict = false)
    {
        var text = new StringBuilder();
        text.Append("CuiSharp doctor  tool ")
            .Append(report.Tool)
            .Append("  catalog ")
            .Append(report.Catalog)
            .AppendLine();

        foreach (Check check in report.Checks)
        {
            string level = check.Level switch
            {
                Level.Ok => "ok",
                Level.Info => "info",
                Level.Warning => "warn",
                _ => "fail",
            };
            text.Append(level.PadRight(5))
                .Append("  ")
                .Append(check.Name.PadRight(10))
                .AppendLine(Indent(check.Message, 17));
            if (check.Fix is { Length: > 0 } fix)
            {
                text.Append("       ")
                    .Append("fix".PadRight(10))
                    .AppendLine(Indent(fix, 17));
            }
        }

        int errors = report.Checks.Count(check => check.Level == Level.Error);
        int warnings = report.Checks.Count(check => check.Level == Level.Warning);
        text.AppendLine();
        text.Append(errors > 0
            ? $"{errors} error{(errors == 1 ? "" : "s")}, {warnings} warning{(warnings == 1 ? "" : "s")}."
            : warnings > 0
                ? strict
                    ? $"{warnings} warning{(warnings == 1 ? "" : "s")} (--strict)."
                    : $"{warnings} warning{(warnings == 1 ? "" : "s")}; --strict makes doctor fail on them."
                : "no problems found.");
        return text.ToString().TrimEnd();

        static string Indent(string value, int width) =>
            value.ReplaceLineEndings("\n").Replace("\n", "\n" + new string(' ', width), StringComparison.Ordinal);
    }

    internal static string RenderJson(Report report, bool strict = false)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("tool", report.Tool);
            writer.WriteString("catalog", report.Catalog);
            if (report.Project is null) writer.WriteNull("project");
            else writer.WriteString("project", report.Project);
            writer.WriteBoolean("ok", !report.Failed && !(strict && report.Warned));
            writer.WriteStartArray("checks");
            foreach (Check check in report.Checks)
            {
                writer.WriteStartObject();
                writer.WriteString("name", check.Name);
                writer.WriteString("level", check.Level.ToString().ToLowerInvariant() switch
                {
                    "warning" => "warn",
                    _ => check.Level.ToString().ToLowerInvariant(),
                });
                writer.WriteString("message", check.Message);
                if (check.Fix is { Length: > 0 } fix) writer.WriteString("fix", fix);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
