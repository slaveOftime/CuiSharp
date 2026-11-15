using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Globalization;

namespace CuiSharp.Tools;

/// <summary>
/// <c>dotnet cui</c>: drives a CuiSharp app — either headless over stdio or attached to a live
/// window over a local named pipe — and answers offline docs/API/icon queries. The grammar itself
/// is declared in <see cref="Grammar"/>; this file is the dispatcher.
/// </summary>
internal static class Cli
{

    private static readonly Lazy<ApiCatalog> EmbeddedCatalog = new(() =>
    {
        try
        {
            return ApiCatalog.LoadEmbedded();
        }
        catch (InvalidDataException broken)
        {
            throw new ToolException(broken.Message);
        }
    });

    private static readonly Lazy<DocLibrary> BundledDocs = new(DocLibrary.LoadEmbedded);

    /// <summary>The catalog this tool ships, shared with <c>doctor</c> so one bad resource is one error.</summary>
    internal static ApiCatalog Catalog => EmbeddedCatalog.Value;

    /// <summary>Runs the tool; the return value is the process exit code.</summary>
    internal static int Run(string[] args, TextWriter output, TextWriter error, TextReader? input = null)
    {
        Grammar.Parsed parsed = Grammar.Parse(args);

        if (parsed.Errors.Count > 0)
        {
            foreach (string message in parsed.Errors) error.WriteLine(message);
            error.WriteLine(Grammar.UsageHint);
            return 2;
        }

        // `--help` and `--version` are System.CommandLine's own actions: let it render them, but
        // through this tool's writers so a captured stream and a redirected one agree.
        if (parsed.NothingAsked)
        {
            // A bare `dotnet cui` asked for nothing, so the help is a usage error: it belongs on
            // stderr with a nonzero exit, not in a pipeline as if it were the answer.
            Grammar.Root.Parse(["--help"]).Invoke(new InvocationConfiguration { Output = error, Error = error });
            return 2;
        }

        if (parsed.Help || parsed.Version)
        {
            parsed.Result.Invoke(new InvocationConfiguration { Output = output, Error = error });
            return 0;
        }

        try
        {
            Options options = parsed.Options!;

            // The offline answers return before any project is resolved: they read resources in
            // this assembly, so they work in an empty directory with nothing built or running.
            if (options.Command is "docs" or "docs.read" or "docs.search")
                return AnswerDocs(options, output);

            if (options.Command is "api" or "icons")
                return AnswerCatalog(options, output);

            if (options.Command == "ps")
            {
                output.WriteLine(LiveClient.FormatList(LiveClient.List(), options.Json));
                return 0;
            }

            if (options.Command == "doctor") return Doctor.Run(options, output);

            if (options.Command == "session" && options.Script is { Length: > 0 } script)
                input = new StringReader(ReadScript(script));

            if (options.Viewports.Length > 1)
                return RunViewports(options, output, error);

            using HeadlessClient client = options.Attach
                ? LiveClient.Connect(options, error)
                : StartHeadless(options, error);

            if (!CheckViewport(client, options, error)) return 1;
            if (!PassPreconditions(client, options, error)) return 1;

            if (options.Command == "session") return Relay(client, input, output, error);

            // `dwell` is the one command the tool answers itself: it keeps the app drawing and
            // reports what that cost.
            if (options.Command == "dwell") return Dwell(client, output, error, options.Seconds) ? 0 : 1;

            Answer answer = Ask(client, options.Line);
            if (!answer.Ok)
            {
                error.WriteLine(answer.Payload);
                return 1;
            }

            output.WriteLine(answer.Payload);
            return 0;
        }
        catch (ToolException failure)
        {
            error.WriteLine(failure.Message);
            return 1;
        }
    }

    /// <summary>
    /// System.CommandLine reports a conversion failure with the CLR type name in it —
    /// "expected type 'System.Nullable`1[System.Int32]'" — which is a fact about the language, not
    /// about the number the reader meant to type. Translate the three types this grammar uses.
    /// </summary>
    internal static string Humanize(string message) => message
        .Replace("System.Nullable`1[System.Int32]", "a whole number", StringComparison.Ordinal)
        .Replace("System.Nullable`1[System.Single]", "a number", StringComparison.Ordinal)
        .Replace("System.Nullable`1[System.Double]", "a number", StringComparison.Ordinal)
        .Replace("System.Int32", "a whole number", StringComparison.Ordinal)
        .Replace("System.Single", "a number", StringComparison.Ordinal)
        .Replace("System.Double", "a number", StringComparison.Ordinal)
        .Replace("as expected type 'a whole number'", "as a whole number", StringComparison.Ordinal)
        .Replace("as expected type 'a number'", "as a number", StringComparison.Ordinal);

    /// <summary>
    /// Answers a <c>docs</c> call: the index, one topic or section, or a ranked search. Like the
    /// catalog commands this returns before any project is resolved, so it works in an empty
    /// directory with nothing built.
    /// </summary>
    private static int AnswerDocs(Options options, TextWriter output)
    {
        DocLibrary docs = BundledDocs.Value;
        CatalogResult answer = options.Command switch
        {
            "docs.read" => docs.Read(options.Argument),
            "docs.search" => docs.Search(options.Argument),
            _ => docs.List(),
        };

        output.WriteLine(options.Json ? answer.RenderJson() : answer.Render());
        return answer.IsExact ? 0 : 1;
    }

    private static HeadlessClient StartHeadless(Options options, TextWriter error)
    {
        (string host, string[] hostArgs) = options.Host is { } exe
            ? (exe, options.AppArgs)
            : AppLocator.Resolve(options.Project, options.NoBuild, error, new(Framework: options.Framework)) is { } assembly
                ? ("dotnet", [assembly, .. options.AppArgs])
                : throw new ToolException(
                    $"No app found at \"{options.Project ?? Directory.GetCurrentDirectory()}\": " +
                    "no .csproj and no file-based .cs app there.");

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (options.State is { Length: > 0 } state)
        {
            string full;
            try
            {
                full = Path.GetFullPath(state);
                Directory.CreateDirectory(full);
            }
            catch (Exception failure) when (failure is ArgumentException or IOException or UnauthorizedAccessException)
            {
                throw new ToolException($"`{state}` is not a state directory this tool can use: {failure.Message}");
            }

            environment["CUISHARP_STATE_HOME"] = full;
        }

        foreach ((string key, string value) in options.Environment) environment[key] = value;
        if (options.Viewport is { } viewport) environment["CUISHARP_VIEWPORT"] = viewport.ToString();

        return HeadlessClient.Start(
            host, hostArgs, echo: options.Quiet ? null : error.WriteLine, environment: environment);
    }

    /// <summary>
    /// The app's banner is the truth about the frame size. A package that predates
    /// <c>CUISHARP_VIEWPORT</c> ignores it, so a silent 1080x720 screenshot would be worse than
    /// a refusal: say which size arrived and how to get the requested one.
    /// </summary>
    private static bool CheckViewport(HeadlessClient client, Options options, TextWriter error)
    {
        if (options.Viewport is not { } viewport) return true;
        if (string.Equals(client.Size, viewport.ToString(), StringComparison.Ordinal)) return true;

        error.WriteLine(
            $"The app started at {client.Size}, not {viewport}. This CuiSharp build does not apply " +
            "CUISHARP_VIEWPORT; rebuild it with a package or project reference that does, then retry.");
        return false;
    }

    /// <summary>
    /// Runs <c>png</c> once per <c>--sizes</c> viewport. Each app instance gets its own frame
    /// size, and each output name carries the size so the files cannot overwrite one another.
    /// </summary>
    private static int RunViewports(Options options, TextWriter output, TextWriter error)
    {
        if (options.Command != "png")
            throw new ToolException(
                "--sizes with more than one viewport is for `png`; run another command once per --size.");

        foreach (Viewport viewport in options.Viewports)
        {
            Options single = options with { Viewport = viewport, Viewports = [viewport] };
            using HeadlessClient client = StartHeadless(single, error);
            if (!CheckViewport(client, single, error)) return 1;
            if (!PassPreconditions(client, single, error)) return 1;

            string path = PngPath(single.Argument, viewport);
            Answer answer = Ask(client, $"png {path}");
            if (!answer.Ok)
            {
                error.WriteLine(answer.Payload);
                return 1;
            }

            output.WriteLine(answer.Payload);
        }

        return 0;
    }

    private static string PngPath(string argument, Viewport viewport)
    {
        string path = argument.Length == 0
            ? Path.Combine(Path.GetTempPath(), $"cui-{viewport}.png")
            : Path.GetFullPath(argument);
        string directory = Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory();
        string extension = Path.GetExtension(path);
        if (extension.Length == 0) extension = ".png";
        string stem = Path.GetFileNameWithoutExtension(path);
        return Path.Combine(directory, $"{stem}-{viewport}{extension}");
    }

    private static int AnswerCatalog(Options options, TextWriter output)
    {
        ApiCatalog catalog = EmbeddedCatalog.Value;
        CatalogResult answer = options.Command == "icons"
            ? catalog.IconsResult(options.Argument.Length == 0 ? null : options.Argument)
            : catalog.Query(options.Argument);

        output.WriteLine(options.Json ? answer.RenderJson() : answer.Render());
        return answer.IsExact ? 0 : 1;
    }

    /// <summary>
    /// Draws and waits through everything that must be true before the command runs: the
    /// <c>--frames</c> draw, then the wait gate.
    /// </summary>
    internal static bool PassPreconditions(HeadlessClient client, Options options, TextWriter error)
    {
        if (options.Frames > 0)
        {
            Answer drawn = Ask(client, $"frame {options.Frames}");
            if (!drawn.Ok)
            {
                error.WriteLine(drawn.Payload);
                return false;
            }
        }

        if (options.WaitLine is { } gate)
        {
            Answer waited = Ask(client, gate);
            if (!waited.Ok)
            {
                error.WriteLine(waited.Payload);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A session script, read before the app starts so a typo in the path is one sentence rather
    /// than a process that starts, waits for its banner, and then fails to read its first line.
    /// </summary>
    internal static string ReadScript(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception failure)
            when (failure is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ToolException($"Could not read the session script `{path}`: {failure.Message}");
        }
    }

    /// <summary>Feeds commands from stdin or a script to one app session and prints each answer in order.</summary>
    internal static int Relay(HeadlessClient client, TextReader? input, TextWriter output, TextWriter error)
    {
        TextReader source = input ?? Console.In;
        while (source.ReadLine() is { } line)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (TryReadDwell(trimmed, out double seconds))
            {
                if (!Dwell(client, output, error, seconds)) return 1;
                continue;
            }

            Answer answer = Ask(client, trimmed);
            if (!answer.Ok)
            {
                error.WriteLine(answer.Payload);
                return 1;
            }

            output.WriteLine(answer.Payload);
        }

        return 0;
    }

    /// <summary>Recognises <c>dwell 3</c> — the one command the tool answers itself.</summary>
    internal static bool TryReadDwell(string line, out double seconds)
    {
        seconds = 0;
        string[] parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[0] != "dwell") return false;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
            throw new ToolException($"dwell needs a number of seconds, not \"{parts[1]}\".");

        if (!double.IsFinite(seconds) || seconds <= 0)
            throw new ToolException($"dwell needs a positive number of seconds, not \"{parts[1]}\".");

        return true;
    }

    private static bool Dwell(HeadlessClient client, TextWriter output, TextWriter error, double seconds)
    {
        const int perPulse = 240;
        var before = client.Sample();
        long heap = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        long frames = 0;
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            Answer answer = client.Send($"pulse {perPulse}");
            if (!answer.Ok)
            {
                error.WriteLine(answer.Payload);
                return false;
            }

            frames += perPulse;
            int at = answer.Payload.IndexOf("heap ", StringComparison.Ordinal);
            if (at >= 0 && long.TryParse(answer.Payload[(at + 5)..].Trim(), out long bytes)) heap = bytes;
        }

        watch.Stop();
        (TimeSpan Cpu, long WorkingSetBytes, long PrivateBytes) after = client.Sample();
        output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "STATS wall {0} cpu {1} frames {2} fps {3:0.0} heap {4} ws {5} private {6}",
            watch.ElapsedMilliseconds,
            Math.Round((after.Cpu - before.Cpu).TotalMilliseconds),
            frames,
            frames / Math.Max(watch.Elapsed.TotalSeconds, 0.001),
            heap,
            after.WorkingSetBytes,
            after.PrivateBytes));
        return true;
    }

    private static readonly TimeSpan WaitAnswerSlack = TimeSpan.FromSeconds(30);

    private static Answer Ask(HeadlessClient client, string line) =>
        client.Send(line, answerTimeout: AnswerTimeoutFor(line));

    /// <summary>The answer deadline a command line needs, or null for the default.</summary>
    internal static TimeSpan? AnswerTimeoutFor(string line)
    {
        string[] words = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? token = words.Length > 0
            ? words[0].ToLowerInvariant() switch
            {
                "wait" => words.Length > 1 ? words[1] : "",
                "waittext" => words.Length > 2 ? words[1] : null,
                _ => null,
            }
            : null;

        if (token is null
            || !double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            || !double.IsFinite(seconds) || seconds <= 0 || seconds > Options.MaxWaitSeconds)
        {
            return null;
        }

        return TimeSpan.FromSeconds(seconds) + WaitAnswerSlack;
    }

    /// <summary>Parsed command-line options for <c>dotnet cui</c>.</summary>
    internal sealed record Options
    {
        internal string Command { get; init; } = "";

        internal string Argument { get; init; } = "";

        internal int Frames { get; init; }

        internal bool Quiet { get; init; }

        internal bool Json { get; init; }

        internal bool Attach { get; init; }

        internal string? AttachTarget { get; init; }

        internal bool NoBuild { get; init; }

        internal string? Project { get; init; }

        internal string? Host { get; init; }

        internal string? Framework { get; init; }

        internal string[] AppArgs { get; init; } = [];

        internal Viewport? Viewport { get; init; }

        internal Viewport[] Viewports { get; init; } = [];

        internal IReadOnlyDictionary<string, string> Environment { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal string? State { get; init; }

        internal string? Script { get; init; }

        internal bool Aot { get; init; }

        internal bool Strict { get; init; }

        internal string Line => Argument.Length == 0 ? Command : $"{Command} {Argument}";

        /// <summary>How long <c>dwell</c> should keep drawing.</summary>
        internal double Seconds { get; init; }

        internal double WaitSeconds { get; init; }

        internal string? WaitLine { get; init; }

        internal const double DefaultWaitSeconds = 10;

        internal const double MaxWaitSeconds = 3600;

    }
}
