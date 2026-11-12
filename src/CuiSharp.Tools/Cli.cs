using System.Globalization;

namespace CuiSharp.Tools;

/// <summary>
/// <c>dotnet cui</c>: drives a CuiSharp app — either headless over stdio or attached to a live
/// window over a local named pipe — and answers offline API/icon catalog queries.
/// </summary>
internal static class Cli
{
    private const string Usage = """
        dotnet cui <command> [argument] [options]

        Commands (what the app understands):
          tree                the controls the last frame drew, nested
          layout              layout rectangles, observed union bounds and overflow diagnostics
          find <text>         one control: kind, label and rectangle
          click <text>        click a control, then print the frame it produced
          type <text>         type into the focused control, then print the frame
          key <name>          press a key, e.g. `key Tab`
          focus               report that the window has focus (needed before typing)
          move <x> <y>        put the pointer somewhere without clicking (hover, tooltips)
          wheel <x> <y> <dy> [dx]  scroll at a point, in wheel notches (negative dy goes down)
          frame [count]       draw more frames, then print the tree
          pulse [count]       draw frames without printing the tree (answers: count, heap bytes)
          wait [seconds]      keep drawing until a frame marks itself ready (ui.MarkReady)
          waittext <s> <text> keep drawing until a frame draws this text or query
          faults              what the app threw while drawing, if anything
          png <path>          save the last frame as a PNG
          quit                close a live attached window

        Commands the tool answers itself:
          ps [--json]         list running live CuiSharp windows (--live / CUISHARP_LIVE=1)
          dwell <seconds>     keep the app drawing for that long, then answer with one STATS
                              line: wall and cpu milliseconds, frames, fps, and the app's
                              heap, working-set and private bytes

        Commands answered offline, from the pages and the API catalog inside this tool:
          docs                the topics and documents bundled here, and what to run next.
                              Reads no project, builds nothing, starts nothing, needs no network.
          docs read <what>    one topic, one document or one section: `docs read testing`,
                              `docs read SKILL`, `docs read SKILL#pitfalls`.
          docs search <words> the sections that answer a question, ranked, each with the
                              `docs read` line that opens it.
          api <name>          one declaration: `api ChartOpts`, `api Ui.LineChart`, `api Button`.
                              An exact answer exits 0; an ambiguous or unknown one names the
                              qualified or nearest names and exits nonzero.
          icons [filter]      every icon constant: name, code point, and the literal to paste.
          All of these take --json for machine-readable output; none builds, launches or loads
          anything.

        Options:
          -a, --attach [id]     drive a running live window (--live) over its named pipe instead
                                of starting a new headless process; use --attach=<pid|title> when
                                more than one live window is open
          -p, --project <path>  the app to run: a directory, a .csproj, or a file-based .cs
                                app. Default: here
              --app <exe>       run a published build instead of building
              --framework <tfm> the target framework, when the project has more than one
              --frames <count>  draw this many frames before the command
          -s, --stdin           read commands from stdin and print each answer, one process
          -q, --quiet           only print the payload
              --wait ready      precondition: draw until a frame marks itself ready (ui.MarkReady)
              --wait-text <text> precondition: draw until a frame draws this text or query
              --wait-timeout <s> wait window for the two above, in seconds (default 10, max 3600)

        A wait is a precondition, not a command: `dotnet cui png out.png --wait ready` screenshots
        the frame that proved the app ready, and an unsatisfied wait ends the run with exit=1
        before the command is sent. `--wait ready` alone waits and then prints the tree.

        Anything after `--` goes to the app.
        """;

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
        try
        {
            Options options = Options.Parse(args);
            if (options.Help)
            {
                output.WriteLine(Usage);
                return args.Length == 0 ? 2 : 0;
            }

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

        internal bool Help { get; init; }

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

        internal static Options Parse(string[] args)
        {
            if (args.Length == 0) return new Options { Help = true };

            string? project = null, host = null, framework = null, attachTarget = null;
            string? wait = null, waitText = null;
            double? waitTimeout = null;
            var appArgs = new List<string>();
            var words = new List<string>();
            int frames = 0;
            bool quiet = false, help = false, streaming = false, noBuild = false, json = false, attach = false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "-h" or "--help":
                        help = true;
                        break;
                    case "help" when words.Count == 0:
                        help = true;
                        break;
                    case "--json":
                        json = true;
                        break;
                    case "-a" or "--attach":
                        attach = true;
                        break;
                    case "-t" or "--target":
                        attach = true;
                        attachTarget = Value(args, ref i);
                        break;
                    case "-p" or "--project":
                        project = Value(args, ref i);
                        break;
                    case "--app":
                        host = Value(args, ref i);
                        break;
                    case "--framework":
                        framework = Value(args, ref i);
                        break;
                    case "--frames":
                        frames = ParseFrames(Value(args, ref i));
                        break;
                    case "--wait":
                        wait = Value(args, ref i);
                        break;
                    case "--wait-text":
                        waitText = Value(args, ref i);
                        break;
                    case "--wait-timeout":
                        waitTimeout = ParseWaitTimeout(Value(args, ref i));
                        break;
                    case "-q" or "--quiet":
                        quiet = true;
                        break;
                    case "-s" or "--stdin":
                        streaming = true;
                        break;
                    case "--no-build":
                        noBuild = true;
                        break;
                    case "--":
                        appArgs.AddRange(args[(i + 1)..]);
                        i = args.Length;
                        break;
                    default:
                        if (arg.StartsWith("--attach=", StringComparison.Ordinal))
                        {
                            string value = arg["--attach=".Length..].Trim();
                            if (value.Length == 0) throw new ToolException("--attach needs a target.");
                            attach = true;
                            attachTarget = value;
                            break;
                        }

                        if (arg.StartsWith("--", StringComparison.Ordinal) || arg.StartsWith('-'))
                            throw new ToolException($"Unknown option \"{arg}\". Use `--` to pass it to the app.");
                        words.Add(arg);
                        break;
                }
            }

            bool waiting = wait is not null || waitText is not null;
            if (wait is not null && !wait.Equals("ready", StringComparison.OrdinalIgnoreCase))
                throw new ToolException($"--wait needs \"ready\", not \"{wait}\".");
            if (wait is not null && waitText is not null)
                throw new ToolException("Choose one wait: --wait ready or --wait-text <text>, not both.");
            if (waitTimeout is not null && !waiting)
                throw new ToolException("--wait-timeout needs --wait or --wait-text to bound.");

            double waitSeconds = waitTimeout ?? DefaultWaitSeconds;
            string? waitLine = null;
            if (waiting)
            {
                string seconds = waitSeconds.ToString("R", CultureInfo.InvariantCulture);
                waitLine = waitText is not null ? $"waittext {seconds} {waitText}" : $"wait {seconds}";
            }

            string command = words.Count > 0
                ? words[0]
                : waiting && !streaming ? "tree" : "";
            string argument = words.Count > 1 ? string.Join(' ', words.Skip(1)) : "";

            bool showHelp = help || (words.Count == 0 && !streaming && !waiting);
            if (json && !showHelp && command is not ("api" or "icons" or "ps" or "docs"))
                throw new ToolException("Unknown option \"--json\". Only `docs`, `api`, `icons`, and `ps` support --json.");

            return new Options
            {
                Command = command,
                Argument = argument,
                Frames = frames,
                Quiet = quiet,
                Json = json,
                Attach = attach,
                AttachTarget = attachTarget,
                Streaming = streaming,
                NoBuild = noBuild,
                Help = showHelp,
                Project = project,
                Host = host,
                Framework = framework,
                AppArgs = [.. appArgs],
                WaitSeconds = waiting ? waitSeconds : 0,
                WaitLine = waitLine,
            };
        }

        internal static int ParseFrames(string value)
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int frames) || frames < 0)
                throw new ToolException($"--frames needs a count of at least 0, not \"{value}\".");

            return frames;
        }

        internal static double ParseWaitTimeout(string value)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                || !double.IsFinite(seconds) || seconds <= 0 || seconds > MaxWaitSeconds)
            {
                throw new ToolException(
                    $"--wait-timeout needs a finite number of seconds between 0 and {MaxWaitSeconds:0}, not \"{value}\".");
            }

            return seconds;
        }

        private static string Value(string[] args, ref int index)
        {
            if (index + 1 >= args.Length || IsFlagToken(args[index + 1]))
                throw new ToolException($"{args[index]} needs a value.");
            return args[++index];
        }

        private static bool IsFlagToken(string token) =>
            token.StartsWith("--", StringComparison.Ordinal)
            || token is "-h" or "-q" or "-s" or "-p" or "-a" or "-t";
    }
}
