using System.Diagnostics;

namespace CuiSharp.Tools;

/// <summary>
/// Finds the app: builds the project next to the caller (or the one it named) and answers the
/// assembly to run.
/// </summary>
/// <remarks>
/// A published executable is used as it is — an agent checking a release build must inspect the
/// release build. Otherwise the project is built in Release, because the frame of a Debug build
/// is not the frame a user sees.
/// </remarks>
internal static class AppLocator
{
    /// <summary>The assembly to run, or null when there is no project here.</summary>
    internal static string? Resolve(string? path, bool noBuild, TextWriter log)
    {
        string directory = Path.GetFullPath(path ?? Directory.GetCurrentDirectory());
        string? project = FindProject(directory);
        if (project is null) return null;

        // `--no-build` trusts the last build, for an agent iterating on frames: it saves the
        // half second MSBuild needs even when nothing changed.
        return noBuild ? Newest(project) ?? Build(project, log) : Build(project, log);
    }

    /// <summary>The newest assembly this project already built, or null when it never did.</summary>
    private static string? Newest(string project)
    {
        string bin = Path.Combine(Path.GetDirectoryName(project)!, "bin", "Release");
        if (!Directory.Exists(bin)) return null;

        // A library sits next to the app in the same folder, so "an assembly with a
        // runtimeconfig beside it" is the thing that can actually be run.
        return Directory.EnumerateFiles(bin, "*.dll", SearchOption.AllDirectories)
            .Where(file => File.Exists(Path.ChangeExtension(file, ".runtimeconfig.json")))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>A project file in <paramref name="directory"/>, preferring the only one there.</summary>
    internal static string? FindProject(string directory)
    {
        if (File.Exists(directory) && directory.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return directory;

        string[] projects = Directory.GetFiles(directory, "*.csproj");
        return projects.Length switch
        {
            0 => null,
            1 => projects[0],
            // A test project is never the app; a directory with several projects is ambiguous
            // only if none of them looks like the one next to it.
            _ => projects.FirstOrDefault(p => !p.Contains("Tests", StringComparison.OrdinalIgnoreCase))
                 ?? projects[0],
        };
    }

    /// <summary>
    /// Builds the project in Release and answers the assembly to run. Asking MSBuild for
    /// `TargetPath` is what makes a project whose assembly is named differently from its file
    /// work — the gallery's file is `CuiSharp.Gallery.csproj` and its assembly `CuiGallery`.
    /// </summary>
    private static string Build(string project, TextWriter log)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in (string[])
            ["build", project, "-c", "Release", "--nologo", "-v", "quiet", "--getProperty:TargetPath"])
        {
            info.ArgumentList.Add(argument);
        }

        using Process? process = Process.Start(info);
        if (process is null) throw new ToolException($"Could not run `dotnet build {project}`.");
        string report = process.StandardOutput.ReadToEnd();
        string complaints = process.StandardError.ReadToEnd();
        process.WaitForExit();

        // The property is the last line of a quiet build; anything else is the build's own
        // report, which only matters when the build failed.
        string target = report.Trim().Split('\n')[^1].Trim();
        if (process.ExitCode != 0 || !target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            log.Write(report);
            throw new ToolException($"`{Path.GetFileName(project)}` did not build:" +
                $"{Environment.NewLine}{complaints.Trim()}{Environment.NewLine}{report.Trim()}");
        }

        return target;
    }
}
