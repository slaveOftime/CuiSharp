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

            if (options.Command is "api" or "icons")
                return AnswerCatalog(options, output, error);

            if (options.Command == "docs")
                return AnswerDocs(options, output, error);

            if (options.Command == "ps")
            {
                output.WriteLine(LiveClient.FormatList(LiveClient.List(), options.Json));
                return 0;
            }

            using HeadlessClient client = options.Attach
                ? LiveClient.Connect(options, error)
                : StartHeadless(options, error);

            if (!PassPreconditions(client, options, error)) return 1;

            if (options.Streaming) return Relay(client, input, output, error);

            if (TryReadDwell(options.Line, out double seconds))
                return Dwell(client, output, error, seconds) ? 0 : 1;

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
    /// Answers a <c>docs</c> call: a topic, a document, a section, or a search. Like the catalog
    /// commands this returns before any project is resolved, so it works in an empty directory.
    /// </summary>
    private static int AnswerDocs(Options options, TextWriter output, TextWriter error)
    {
        (CatalogResult? answer, string? usage) = DocsAnswer(BundledDocs.Value, options.Argument);
        if (usage is not null)
        {
            error.WriteLine(usage);
            return 2;
        }

        output.WriteLine(options.Json ? answer!.RenderJson() : answer!.Render());
        return answer!.IsExact ? 0 : 1;
    }

    /// <summary>
    /// The <c>docs</c> grammar, separated from the writers so the grammar is testable on its own:
    /// no argument is the index, <c>search</c> and <c>read</c> are the two verbs, and anything else
    /// is the thing to read — an assistant that drops <c>read</c> gets the page, not a correction.
    /// </summary>
    internal static (CatalogResult? Answer, string? Usage) DocsAnswer(DocLibrary docs, string argument)
    {
        string line = argument.Trim();
        if (line.Length == 0) return (docs.List(), null);

        int space = line.IndexOfAny([' ', '\t']);
        string verb = space < 0 ? line : line[..space];
        string rest = space < 0 ? "" : line[(space + 1)..].Trim();

        if (verb.Equals("search", StringComparison.OrdinalIgnoreCase))
        {
            return rest.Length > 0
                ? (docs.Search(rest), null)
                : (null, "`docs search` needs words to look for: `dotnet cui docs search dialog keyboard`.");
        }

        if (verb.Equals("read", StringComparison.OrdinalIgnoreCase))
        {
            return rest.Length > 0
                ? (docs.Read(rest), null)
                : (null, "`docs read` needs a topic, a document or a section: `dotnet cui docs read debugging`.");
        }

        return (docs.Read(line), null);
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

        return HeadlessClient.Start(
            host, hostArgs, echo: options.Quiet ? null : error.WriteLine);
    }

    private static int AnswerCatalog(Options options, TextWriter output, TextWriter error)
    {
        if (options.Command == "api" && options.Argument.Length == 0)
        {
            error.WriteLine(
                "`api` needs a name to look up: `dotnet cui api ChartOpts`, `dotnet cui api Ui.LineChart`, "
                + "or `dotnet cui icons` for the glyph list.");
            return 2;
        }

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

    /// <summary>Feeds commands from stdin to one app session and prints each answer in order.</summary>
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

        /// <summary>True when no verb was given: either the help, or `--wait ready` alone.</summary>
        internal bool NoCommand { get; init; }

        internal bool Json { get; init; }

        internal bool Attach { get; init; }

        internal string? AttachTarget { get; init; }

        internal bool Streaming { get; init; }

        internal bool NoBuild { get; init; }

        internal string? Project { get; init; }

        internal string? Host { get; init; }

        internal string? Framework { get; init; }

        internal string[] AppArgs { get; init; } = [];

        internal string Line => Argument.Length == 0 ? Command : $"{Command} {Argument}";

        internal double WaitSeconds { get; init; }

        internal string? WaitLine { get; init; }

        internal const double DefaultWaitSeconds = 10;

        internal const double MaxWaitSeconds = 3600;

    }
}
