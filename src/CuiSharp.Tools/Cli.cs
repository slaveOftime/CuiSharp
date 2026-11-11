using System.Globalization;

namespace CuiSharp.Tools;

/// <summary>
/// <c>dotnet cui</c>: runs a CuiSharp app with no window and does one thing to it — print the
/// controls of its last frame, screenshot it, click a control, type into it.
/// </summary>
/// <remarks>
/// <para>
/// The app is not simulated: the tool builds it, runs it the way a person would, and speaks the
/// headless protocol the framework serves. What you see is the frame the app really draws, with
/// its own services, its own data and its own bugs.
/// </para>
/// <para>
/// Two commands answer from the API catalog embedded in this assembly — <c>api</c> and
/// <c>icons</c>. They discover no project, build nothing, start no app and load no native
/// library, so they work in any directory, before any app exists.
/// </para>
/// <code>
/// dotnet cui tree                     # what the first frame drew, nested
/// dotnet cui click Save               # click a control, print the frame that reacted
/// dotnet cui type Milk                # type into the focused field
/// dotnet cui png shot.png             # the frame as a PNG
/// dotnet cui tree --project src/App   # another app
/// dotnet cui tree --project app.cs    # a file-based app
/// dotnet cui tree --app bin/App.exe   # a published build, not a rebuild
/// dotnet cui tree -- --page=2         # arguments for the app itself
/// dotnet cui api ChartOpts            # one declaration, offline, from the embedded catalog
/// dotnet cui icons chart              # every icon whose name contains "chart"
/// </code>
/// </remarks>
internal static class Cli
{
    private const string Usage = """
        dotnet cui <command> [argument] [options]

        Commands (what the app understands):
          tree                the controls the last frame drew, nested
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

        Commands the tool answers itself:
          dwell <seconds>     keep the app drawing for that long, then answer with one STATS
                              line: wall and cpu milliseconds, frames, fps, and the app's
                              heap, working-set and private bytes

        Commands answered offline, from the API catalog inside this tool:
          api <name>          one declaration: `api ChartOpts`, `api Ui.LineChart`, `api Button`.
                              An exact answer exits 0; an ambiguous or unknown one names the
                              qualified or nearest names and exits nonzero.
          icons [filter]      every icon constant: name, code point, and the literal to paste.
          Both take --json for machine-readable output; neither builds, launches or loads anything.

        Options:
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

    /// <summary>Runs the tool; the return value is the process exit code.</summary>
    internal static int Run(string[] args, TextWriter output, TextWriter error, TextReader? input = null)
    {
        try
        {
            // `api` and `icons` come first, before anything touches a project: the question
            // they answer ("what is ChartOpts?") is legal in a directory with no app, and the
            // answer is the catalog embedded in this assembly — no discovery, no build, no
            // child process, no native library. The detection itself reads only argv.
            if (TryReadCatalogCommand(args, out CatalogRequest catalog))
                return AnswerCatalog(catalog, output, error);

            Options options = Options.Parse(args);
            if (options.Help)
            {
                output.WriteLine(Usage);
                return args.Length == 0 ? 2 : 0;
            }


            (string host, string[] hostArgs) = options.Host is { } exe
                ? (exe, options.AppArgs)
                : AppLocator.Resolve(options.Project, options.NoBuild, error, new(Framework: options.Framework)) is { } assembly
                    ? ("dotnet", [assembly, .. options.AppArgs])
                    : throw new ToolException(
                        $"No app found at \"{options.Project ?? Directory.GetCurrentDirectory()}\": " +
                        "no .csproj and no file-based .cs app there.");

            // The app's own output is the app's own business: it goes where a shell would send
            // the app's diagnostics, and the protocol on stdout stays clean.
            using HeadlessClient client = HeadlessClient.Start(
                host, hostArgs, echo: options.Quiet ? null : error.WriteLine);

            if (!PassPreconditions(client, options, error)) return 1;

            if (options.Streaming) return Relay(client, input, output, error);

            // dwell is the one command the tool answers itself, and it reads the same in a
            // session as it does on the command line: `dotnet cui dwell 3`.
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
    /// The catalog the CLI answers from, read once from this assembly's own resources.
    /// A broken or missing resource is a packaging fault, so it says so once and stays said:
    /// <see cref="ToolException"/> reaches <see cref="Run"/>'s handler, which prints the
    /// sentence and exits 1 rather than crashing on a raw <see cref="InvalidDataException"/>.
    /// </summary>
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

    /// <summary>An offline catalog request: which command, what was asked about, and how to print it.</summary>
    internal sealed record CatalogRequest(string Command, string Term, bool Json, bool Help);

    /// <summary>
    /// Recognises <c>api &lt;name&gt;</c> and <c>icons [filter]</c> among the raw arguments.
    /// The first positional word decides everything: if it is neither command the call falls
    /// through to the normal app-driven flow untouched — every flag complaint this scan
    /// noticed stays unvoiced, because the ordinary parser owns those arguments and a
    /// dash-prefixed <c>--wait-text</c> value is legal there. Only once the command word
    /// proves this is a catalog call are a held-back unknown flag or missing value named.
    /// App-scoped flags are accepted and ignored so one wrapper script can pass the same
    /// flags to every command. <c>-h</c>/<c>--help</c> ask for usage anywhere; a bare
    /// <c>help</c> does so only before the command, because after one it is data — the icon
    /// filter of `icons help`, the lookup term of `api help`. A value-taking flag consumes
    /// exactly the word <see cref="Options.Parse"/> would consume, dash or not — the scan's
    /// position may never diverge from the parser's, or the command word itself lands in the
    /// wrong slot — and complains, catalog-only, when that word is really another flag. The one
    /// option whose value the parser judges only at the end — <c>--wait</c>, last occurrence
    /// wins — is stored rather than checked where it is read, so a value the caller overwrote
    /// is not a mistake here either.
    /// </summary>
    internal static bool TryReadCatalogCommand(string[] args, out CatalogRequest request)
    {
        request = null!;
        string? command = null;
        var words = new List<string>();
        bool json = false, help = false;
        // The first mistake the scan cannot ignore, held in escrow until the command word
        // says whether this is even the scan's business: `tree --wait-text -loading` names a
        // legal value in the ordinary flow, so the scan may note the oddity and keep walking,
        // but it may only speak for `api` and `icons`.
        string? complaint = null;
        // The wait the parser ends up judging: it takes every occurrence but checks only the
        // surviving value, after the whole line, so the scan keeps that same value instead of
        // condemning one the line already replaced.
        string? wait = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            // Everything after `--` belongs to an app this command never starts.
            if (arg == "--") break;
            switch (arg)
            {
                case "-h" or "--help":
                    help = true;
                    break;
                // A bare `help` means help only until the command is named; after `api` or
                // `icons` it is a word like any other, so `icons help` can find CircleHelp.
                case "help" when command is null:
                    help = true;
                    break;
                case "--json":
                    json = true;
                    break;
                // Flags that mean nothing to a catalog answer but must not derail one:
                // a script that appends `-q` or `--no-build` to every cui call stays one script.
                case "-q" or "--quiet" or "-s" or "--stdin" or "--no-build":
                    break;
                case "-p" or "--project" or "--app" or "--framework" or "--frames"
                    or "--wait" or "--wait-text" or "--wait-timeout":
                    // Consume exactly what the ordinary parser consumes: its Value() takes
                    // the word after the flag, dash or not, so `--wait-text --app tree` has
                    // command `tree` there and must have command `tree` here — a scan whose
                    // position diverges misclassifies the line and answers a catalog error
                    // for an ordinary app run. When that consumed word is really the next
                    // flag (`--frames --json` lost its number), note the mistake; the note is
                    // voiced only for a catalog command.
                    if (i + 1 >= args.Length)
                    {
                        complaint ??= $"{arg} needs a value.";
                    }
                    else
                    {
                        // A consumed word that is not a legal value — the next flag, or a
                        // `--wait-timeout abc` the parser would refuse — is the same class
                        // of catalog-only complaint: checked with the parser's own validators
                        // so the sentence matches what it would say for an ordinary command.
                        string value = args[i + 1];
                        // `--wait` is the one option the parser does not judge where it reads
                        // it: each occurrence overwrites the last and only the surviving value
                        // is checked, after the line. Keep that value and check it below, so a
                        // word the caller already replaced is not a mistake here either.
                        if (arg == "--wait") wait = value;
                        else complaint ??= value.StartsWith('-')
                            ? $"{arg} needs a value."
                            : ValueComplaint(arg, value);
                        i++;
                    }
                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        // Likewise escrowed: for a non-catalog command the ordinary parser
                        // is the one that calls this unknown, in its own words.
                        complaint ??= $"Unknown option \"{arg}\". Use `--` to pass it to the app.";
                        break;
                    }

                    if (command is null) command = arg;
                    else words.Add(arg);
                    break;
            }
        }

        if (command is not ("api" or "icons")) return false;
        // Judged where the parser judges it — after the line, on the surviving value — and
        // after whatever the loop itself already found, which is the parser's own precedence.
        if (wait is { } kind && !kind.Equals("ready", StringComparison.OrdinalIgnoreCase))
            complaint ??= $"--wait needs \"ready\", not \"{kind}\".";
        if (complaint is { } mistake) throw new ToolException(mistake);
        request = new CatalogRequest(command, string.Join(' ', words), json, help);
        return true;
    }

    /// <summary>
    /// Why the ordinary parser would refuse this value, in its own words, by calling its own
    /// validators — or null when it would take it. The catalog scan owns no second grammar of
    /// numbers; it borrows this one. <c>--wait</c> is absent on purpose: the parser checks its
    /// kind after the whole line, on the last value, which is where the scan checks it too.
    /// </summary>
    private static string? ValueComplaint(string option, string value) => option switch
    {
        "--frames" => Check(() => Options.ParseFrames(value)),
        "--wait-timeout" => Check(() => Options.ParseWaitTimeout(value)),
        _ => null,
    };

    /// <summary>
    /// Runs one of the parser's validators, turning its refusal into the complaint text —
    /// the scan borrows the parser's grammar wholesale, wording included.
    /// </summary>
    private static string? Check(Func<object> validator)
    {
        try
        {
            validator();
            return null;
        }
        catch (ToolException problem)
        {
            return problem.Message;
        }
    }

    /// <summary>
    /// Answers a catalog request from the embedded JSON. The answer is the payload and always
    /// goes to stdout — including an unknown or ambiguous one, whose lines are the explanation;
    /// the exit code is the signal, so a script never has to merge streams to read a failure.
    /// Exact answers exit 0; a filter that hit nothing and a name that is not there exit 1;
    /// an <c>api</c> with no term is a usage error and exits 2, the code this CLI already
    /// spends on "you asked for nothing".
    /// </summary>
    private static int AnswerCatalog(CatalogRequest request, TextWriter output, TextWriter error)
    {
        if (request.Help)
        {
            output.WriteLine(Usage);
            return 0;
        }

        if (request.Command == "api" && request.Term.Length == 0)
        {
            error.WriteLine(
                "`api` needs a name to look up: `dotnet cui api ChartOpts`, `dotnet cui api Ui.LineChart`, "
                + "or `dotnet cui icons` for the glyph list.");
            return 2;
        }

        ApiCatalog catalog = EmbeddedCatalog.Value;
        CatalogResult answer = request.Command == "icons"
            ? catalog.IconsResult(request.Term.Length == 0 ? null : request.Term)
            : catalog.Query(request.Term);

        output.WriteLine(request.Json ? answer.RenderJson() : answer.Render());
        return answer.IsExact ? 0 : 1;
    }

    /// <summary>
    /// Draws and waits through everything that must be true before the command runs: the
    /// <c>--frames</c> draw, then the wait gate. A refused precondition is a real answer with a
    /// real reason, and it ends the run — the command, the gate behind it, and a whole relayed
    /// session would otherwise act on frames the app never drew, and a tool that reads an error
    /// and carries on can finish a lie with exit 0. Internal so a fake server can prove the
    /// stop without an app to refuse.
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

        // A wait is a precondition, like --frames, not the command: the app must reach the
        // state before the command acts on it. A miss ends the run with exit=1 and the command
        // is never sent; a satisfied wait stays out of the way — its payload is discarded
        // because the command's own answer is the payload. In a --stdin session the gate passes
        // first, before any line is relayed.
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
    /// Feeds commands from stdin to one app process, so a session of ten commands pays for one
    /// startup instead of ten. Answers go to stdout in order. Internal so a fake server can
    /// stand behind the session and show a relayed wait gets the deadline its grammar asks for.
    /// </summary>
    internal static int Relay(HeadlessClient client, TextReader? input, TextWriter output, TextWriter error)
    {
        TextReader source = input ?? Console.In;
        while (source.ReadLine() is { } line)
        {
            if (line.Trim().Length == 0) continue;
            if (TryReadDwell(line.Trim(), out double seconds))
            {
                if (!Dwell(client, output, error, seconds)) return 1;
                continue;
            }

            Answer answer = Ask(client, line.Trim());
            if (!answer.Ok)
            {
                error.WriteLine(answer.Payload);
                return 1;
            }

            output.WriteLine(answer.Payload);
        }

        return 0;
    }

    /// <summary>
    /// Recognises <c>dwell 3</c> — the one command the tool answers itself. Parsing is a
    /// separate method so the grammar is testable without an app to dwell in.
    /// </summary>
    internal static bool TryReadDwell(string line, out double seconds)
    {
        seconds = 0;
        // The char array is written out because StringSplitOptions is an enum with an int
        // base: `Split(' ', '\t', options)` would bind to Split(char, int, options) and turn
        // the options into a maximum count.
        string[] parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[0] != "dwell") return false;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
            throw new ToolException($"dwell needs a number of seconds, not \"{parts[1]}\".");

        // A dwell of zero or less measures nothing, and Infinity or NaN — which TryParse accepts —
        // would either spin forever or end the walk's table with a row of zeros. Both are typos.
        if (!double.IsFinite(seconds) || seconds <= 0)
            throw new ToolException($"dwell needs a positive number of seconds, not \"{parts[1]}\".");

        return true;
    }

    /// <summary>
    /// Holds a page on screen for a while with the app really drawing: idle, the app only waits
    /// for the next command, so a dwell that sent nothing would measure a sleeping process. It
    /// ticks <c>pulse</c> until the window is through — pulse draws without printing the tree,
    /// so the measurement does not pay for serialising the very interface it is measuring — and
    /// answers with what the process burned between the first sample and the last.
    /// </summary>
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
            // The pulse carries the app's own view of its managed heap: "240 heap 458752".
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

    /// <summary>
    /// Room on top of a wait command's own timeout before the client stops listening: the wait
    /// answers within its timeout, and the slack covers the real frames drawn around it.
    /// </summary>
    private static readonly TimeSpan WaitAnswerSlack = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Sends one command with the answer deadline its own grammar asks for. A direct
    /// <c>wait 60</c> answers no later than its sixty seconds, and a client fixed at the
    /// default deadline would give up on an app that is answering exactly as promised.
    /// </summary>
    private static Answer Ask(HeadlessClient client, string line) =>
        client.Send(line, answerTimeout: AnswerTimeoutFor(line));

    /// <summary>
    /// The answer deadline a command line needs, or null for the default. Only <c>wait</c> and
    /// <c>waittext</c> wait on something, and only their first timeout token is read — the query
    /// after it is user text and never a number. A malformed or impossible wait gets the
    /// default, because the app answers one immediately, with the reason.
    /// </summary>
    internal static TimeSpan? AnswerTimeoutFor(string line)
    {
        string[] words = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? token = words.Length > 0
            ? words[0].ToLowerInvariant() switch
            {
                // `wait` alone takes the app's own default window, which the default covers; a
                // `waittext` without a query is answered with its usage immediately.
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

    /// <summary>
    /// What the tool was asked for. Parsing is separate from running so that flag handling is
    /// testable without spawning a process.
    /// </summary>
    internal sealed record Options
    {
        internal string Command { get; init; } = "";

        internal string Argument { get; init; } = "";

        internal int Frames { get; init; }

        internal bool Quiet { get; init; }

        internal bool Help { get; init; }

        /// <summary>Read commands from stdin instead of taking one from the arguments.</summary>
        internal bool Streaming { get; init; }

        /// <summary>Reuse the newest build instead of building.</summary>
        internal bool NoBuild { get; init; }

        /// <summary>The project to build, when the app was not given as an executable.</summary>
        internal string? Project { get; init; }

        /// <summary>The executable to run, instead of building.</summary>
        internal string? Host { get; init; }

        /// <summary>The target framework, when the project targets more than one.</summary>
        internal string? Framework { get; init; }

        /// <summary>Arguments for the app itself, from after <c>--</c>.</summary>
        internal string[] AppArgs { get; init; } = [];

        /// <summary>The line the app receives: its command and the argument.</summary>
        internal string Line => Argument.Length == 0 ? Command : $"{Command} {Argument}";

        /// <summary>The wait window the caller asked for, in seconds; 0 when nothing waits.</summary>
        internal double WaitSeconds { get; init; }

        /// <summary>
        /// The gate line sent to the app before the command, or null when nothing waits. A wait
        /// is a precondition like <see cref="Frames"/>, never the command itself: the command
        /// keeps <see cref="Line"/> and its answer keeps the payload.
        /// </summary>
        internal string? WaitLine { get; init; }

        /// <summary>The window a wait gets when no --wait-timeout narrows it, in seconds.</summary>
        internal const double DefaultWaitSeconds = 10;

        /// <summary>
        /// The longest wait the tool will set. A wait bounds the client's answer deadline
        /// downstream, and a number near <see cref="TimeSpan.MaxValue"/> would overflow it; a
        /// wait measured in hours is not a wait, it is a hung app with extra steps.
        /// </summary>
        internal const double MaxWaitSeconds = 3600;

        internal static Options Parse(string[] args)
        {
            if (args.Length == 0) return new Options { Help = true };

            string? project = null, host = null, framework = null;
            string? wait = null, waitText = null;
            double? waitTimeout = null;
            var appArgs = new List<string>();
            var words = new List<string>();
            int frames = 0;
            bool quiet = false, help = false, streaming = false, noBuild = false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "-h" or "--help" or "help":
                        help = true;
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
                        // An unknown long option is a mistake, not a command: say so rather than
                        // sending it to the app, which would answer "unknown command".
                        if (arg.StartsWith("--", StringComparison.Ordinal) || arg.StartsWith('-'))
                            throw new ToolException($"Unknown option \"{arg}\". Use `--` to pass it to the app.");
                        words.Add(arg);
                        break;
                }
            }

            // A wait is a precondition, so it composes with any command instead of replacing
            // one. The gate line carries an explicit timeout and the query verbatim after it,
            // so text with spaces, unicode or a numeric tail reaches the app exactly as typed.
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

            // A wait with no command still has to answer with something: the tree the wait
            // waited for is the smallest honest payload.
            string command = words.Count > 0
                ? words[0]
                : waiting && !streaming ? "tree" : "";
            string argument = words.Count > 1 ? string.Join(' ', words.Skip(1)) : "";

            return new Options
            {
                Command = command,
                Argument = argument,
                Frames = frames,
                Quiet = quiet,
                Streaming = streaming,
                NoBuild = noBuild,
                Help = help || (words.Count == 0 && !streaming && !waiting),
                Project = project,
                Host = host,
                Framework = framework,
                AppArgs = [.. appArgs],
                WaitSeconds = waiting ? waitSeconds : 0,
                WaitLine = waitLine,
            };
        }

        /// <summary>
        /// A frame count the app can draw. A bare int.Parse threw FormatException and
        /// OverflowException at the user for a typo that deserves a sentence, and a negative
        /// count drew nothing while claiming otherwise.
        /// </summary>
        internal static int ParseFrames(string value)
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int frames) || frames < 0)
                throw new ToolException($"--frames needs a count of at least 0, not \"{value}\".");

            return frames;
        }

        /// <summary>
        /// A wait window the tool can honour. TryParse accepts NaN and Infinity, and neither is
        /// a bound anyone could wait by; the ceiling keeps the deadline arithmetic downstream
        /// finite. Every typo here is an exit=1, not a crash.
        /// </summary>
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
            if (index + 1 >= args.Length) throw new ToolException($"{args[index]} needs a value.");
            return args[++index];
        }
    }
}
