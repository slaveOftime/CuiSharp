using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace CuiSharp.Tools;

/// <summary>
/// Discovers and connects to running CuiSharp windows that enabled live attach
/// (<c>--live</c>, <c>CUISHARP_LIVE=1</c>, or <c>CuiAppOptions.LiveAttach</c>).
/// </summary>
internal static class LiveClient
{
    internal const string DirectoryEnvironmentVariable = "CUISHARP_LIVE_DIR";

    /// <summary>One running CuiSharp window advertising a live named-pipe session.</summary>
    internal sealed record LiveTarget(
        int Pid,
        string Pipe,
        string Title,
        string ProcessPath,
        string WorkingDirectory,
        int Width,
        int Height);

    /// <summary>Where live sessions write their discovery records on this machine.</summary>
    internal static string GetDiscoveryDirectory()
    {
        string? custom = Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable);
        return !string.IsNullOrWhiteSpace(custom)
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cuisharp", "live");
    }

    /// <summary>Lists all live CuiSharp windows currently running, pruning stale entries whose processes exited.</summary>
    internal static IReadOnlyList<LiveTarget> List()
    {
        string directory = GetDiscoveryDirectory();
        if (!Directory.Exists(directory)) return [];

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "*.json");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var targets = new List<LiveTarget>(files.Length);
        foreach (string file in files)
        {
            LiveTarget? target = TryReadTarget(file);
            if (target is null) continue;
            if (!IsProcessAlive(target.Pid))
            {
                TryDelete(file);
                continue;
            }

            targets.Add(target);
        }

        targets.Sort(static (a, b) => a.Pid.CompareTo(b.Pid));
        return targets;
    }

    /// <summary>Resolves which running live window to attach to, refusing to guess when more than one matches.</summary>
    internal static LiveTarget Resolve(string? target, string? project)
    {
        IReadOnlyList<LiveTarget> running = List();
        if (running.Count == 0)
        {
            throw new ToolException(
                "No live CuiSharp window is running. Start the app with `--live` or `CUISHARP_LIVE=1` first.");
        }

        if (!string.IsNullOrWhiteSpace(target))
        {
            string wanted = target.Trim();
            if (int.TryParse(wanted, NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
            {
                LiveTarget? byPid = running.FirstOrDefault(t => t.Pid == pid);
                if (byPid is not null) return byPid;
            }

            LiveTarget[] matches =
            [
                .. running.Where(t =>
                    t.Pipe.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                    || t.Title.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                    || t.ProcessPath.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                    || t.WorkingDirectory.Contains(wanted, StringComparison.OrdinalIgnoreCase)),
            ];

            if (matches.Length == 1) return matches[0];
            if (matches.Length == 0)
            {
                throw new ToolException(
                    $"No live window matches \"{wanted}\". Running:{Environment.NewLine}{FormatList(running, json: false)}");
            }

            throw new ToolException(
                $"More than one live window matches \"{wanted}\":{Environment.NewLine}{FormatList(matches, json: false)}{Environment.NewLine}" +
                "Choose one with `--attach=<pid>`.");
        }

        if (project is not null)
        {
            string fullProject = Path.GetFullPath(project);
            string folder = File.Exists(fullProject)
                ? Path.GetDirectoryName(fullProject) ?? fullProject
                : fullProject;
            string name = Path.GetFileNameWithoutExtension(fullProject);
            LiveTarget[] byProject =
            [
                .. running.Where(t =>
                    PathEqualsOrUnder(t.WorkingDirectory, folder)
                    || PathEqualsOrUnder(t.ProcessPath, folder)
                    || (name.Length > 0 && Path.GetFileNameWithoutExtension(t.ProcessPath).Equals(name, StringComparison.OrdinalIgnoreCase))),
            ];

            if (byProject.Length == 1) return byProject[0];
            if (byProject.Length == 0)
            {
                throw new ToolException(
                    $"No live window matches `{project}`. Running:{Environment.NewLine}{FormatList(running, json: false)}");
            }

            throw new ToolException(
                $"More than one live window matches `{project}`:{Environment.NewLine}{FormatList(byProject, json: false)}{Environment.NewLine}" +
                "Choose one with `--attach=<pid>`.");
        }

        if (running.Count == 1) return running[0];

        string cwd = Directory.GetCurrentDirectory();
        LiveTarget[] inCwd = [.. running.Where(t => PathEqualsOrUnder(t.WorkingDirectory, cwd))];
        if (inCwd.Length == 1) return inCwd[0];

        throw new ToolException(
            $"More than one live CuiSharp window is running:{Environment.NewLine}{FormatList(running, json: false)}{Environment.NewLine}" +
            "Choose one with `--attach=<pid>` or `--attach=<title>`.");
    }

    /// <summary>Connects a <see cref="HeadlessClient"/> to the resolved live window's named pipe.</summary>
    internal static HeadlessClient Connect(Cli.Options options, TextWriter error)
    {
        LiveTarget target = Resolve(options.AttachTarget, options.Project);
        var pipe = new NamedPipeClientStream(".", target.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            pipe.Connect(timeout: 5000);
            Process? process = TryGetProcess(target.Pid);
            return HeadlessClient.Attach(
                pipe,
                pipe,
                echo: options.Quiet ? null : error.WriteLine,
                process: process);
        }
        catch (TimeoutException)
        {
            pipe.Dispose();
            throw new ToolException(
                $"Could not connect to live window {target.Pid} (\"{target.Title}\") on pipe `{target.Pipe}` within 5s.");
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    /// <summary>Renders the running live windows as text or machine-readable JSON.</summary>
    internal static string FormatList(IReadOnlyList<LiveTarget> targets, bool json)
    {
        if (json)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartArray();
                foreach (LiveTarget target in targets)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("pid", target.Pid);
                    writer.WriteString("pipe", target.Pipe);
                    writer.WriteString("title", target.Title);
                    writer.WriteString("processPath", target.ProcessPath);
                    writer.WriteString("workingDirectory", target.WorkingDirectory);
                    writer.WriteNumber("width", target.Width);
                    writer.WriteNumber("height", target.Height);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        if (targets.Count == 0) return "No live CuiSharp windows.";

        var sb = new StringBuilder();
        for (int i = 0; i < targets.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            LiveTarget t = targets[i];
            sb.Append(CultureInfo.InvariantCulture, $"{t.Pid}  \"{t.Title}\"  {t.Width}x{t.Height}  {t.WorkingDirectory}");
        }

        return sb.ToString();
    }

    private static bool PathEqualsOrUnder(string candidate, string directory)
    {
        if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(directory)) return false;
        try
        {
            string fullCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullDir = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            StringComparison cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return fullCandidate.Equals(fullDir, cmp)
                || fullCandidate.StartsWith(fullDir + Path.DirectorySeparatorChar, cmp);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static LiveTarget? TryReadTarget(string file)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(file);
            using JsonDocument doc = JsonDocument.Parse(bytes);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("pid", out JsonElement pidEl) || !pidEl.TryGetInt32(out int pid)) return null;
            if (!root.TryGetProperty("pipe", out JsonElement pipeEl) || pipeEl.GetString() is not { Length: > 0 } pipe) return null;

            string title = root.TryGetProperty("title", out JsonElement titleEl) ? titleEl.GetString() ?? "" : "";
            string processPath = root.TryGetProperty("processPath", out JsonElement pathEl) ? pathEl.GetString() ?? "" : "";
            string cwd = root.TryGetProperty("workingDirectory", out JsonElement cwdEl) ? cwdEl.GetString() ?? "" : "";
            int width = root.TryGetProperty("width", out JsonElement wEl) && wEl.TryGetInt32(out int w) ? w : 0;
            int height = root.TryGetProperty("height", out JsonElement hEl) && hEl.TryGetInt32(out int h) ? h : 0;
            return new LiveTarget(pid, pipe, title, processPath, cwd, width, height);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Process? TryGetProcess(int pid)
    {
        try
        {
            Process process = Process.GetProcessById(pid);
            if (!process.HasExited) return process;
            process.Dispose();
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
    }
}
