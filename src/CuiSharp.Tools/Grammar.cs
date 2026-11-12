using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using System.Globalization;

namespace CuiSharp.Tools;

/// <summary>
/// The <c>dotnet cui</c> grammar, declared once with System.CommandLine so that the help text,
/// the arity, the value types and the error messages cannot drift apart.
/// <para>
/// The verbs are the app's wire protocol, not this tool's invention: each is a
/// <see cref="Command"/> whose typed arguments <see cref="Resolve"/> re-serialises into exactly
/// one protocol line. Declaring them — rather than splitting a token list by hand — is what gives
/// <c>click</c> an arity, <c>frame</c> an <see cref="int"/>, a mistyped verb a suggestion, and
/// <c>-a 15036</c> its value.
/// </para>
/// </summary>
internal static class Grammar
{
    /// <summary>What to say after a usage error, so a reader (or an agent) has the next step.</summary>
    internal const string UsageHint = "Run `dotnet cui --help`, or `dotnet cui <command> --help`.";

    /// <summary>The verbs the app answers: name, description, and one argument per protocol slot.</summary>
    private static readonly (string Name, string Description, Argument[] Arguments)[] Verbs =
    [
        ("tree", "the controls the last frame drew, nested (`print` is an alias)", []),
        ("layout", "layout rectangles, observed union bounds and overflow diagnostics", []),
        ("focus", "report that the window has focus (needed before typing)", []),
        ("faults", "what the app threw while drawing, if anything", []),
        ("quit", "close a live attached window", []),

        ("find", "one control: kind, label and rectangle", [Text("text", "the label, text or id to find")]),
        ("click", "click a control, then print the frame it produced", [Text("text", "the label, text or id to click")]),
        ("type", "type into the focused control, then print the frame", [Text("text", "the text to type")]),
        ("key", "press a key, e.g. `key Tab`", [Text("name", "the key: Tab, Enter, F1, Ctrl+S")]),

        ("move", "put the pointer somewhere without clicking (hover, tooltips)",
            [Number("x", required: true), Number("y", required: true)]),
        ("wheel", "scroll at a point, in wheel notches (negative dy scrolls down)",
            [Number("x", required: true), Number("y", required: true), Number("dy", required: true), Number("dx", required: false)]),

        ("frame", "draw more frames, then print the tree", [Count("how many frames (default 1)")]),
        ("pulse", "draw frames without printing the tree (answers: count, heap bytes)", [Count("how many frames (default 1)")]),
        ("wait", "keep drawing until a frame marks itself ready (ui.MarkReady)", [Seconds("how long to keep waiting (default: the app's own budget)")]),
        ("waittext", "keep drawing until a frame draws this text or query",
            [Seconds("how long to keep waiting"), Text("query", "the text or query to wait for")]),

        ("png", "save the last frame as a PNG", [OptionalText("path", "where to write it (default: a temporary file)")]),
        ("dwell", "keep the app drawing, then answer with one STATS line", [Seconds("how long to draw for")]),
    ];

    /// <summary>Every symbol the resolver needs, in one place.</summary>
    private sealed class Symbols
    {
        internal Option<string?> Project = new("--project", "-p")
        {
            Description = "the app to drive: a directory, a .csproj, or a file-based .cs app (default: here)",
            HelpName = "path",
            Recursive = true,
        };

        internal Option<string?> App = new("--app")
        {
            Description = "drive a published build instead of building",
            HelpName = "exe",
            Recursive = true,
        };

        internal Option<string?> Framework = new("--framework", "-f")
        {
            Description = "the target framework, when the project has more than one",
            HelpName = "tfm",
            Recursive = true,
        };

        internal Option<int?> Frames = new("--frames")
        {
            Description = "draw this many frames before the command runs",
            HelpName = "count",
            Arity = ArgumentArity.ExactlyOne,
            Recursive = true,
        };

        internal Option<string?> Attach = new("--attach", "-a")
        {
            Description = "drive a running live window (started with --live) over its named pipe, "
                + "instead of starting a new headless process; the value is its pid or title",
            HelpName = "pid|title",
            Arity = ArgumentArity.ZeroOrOne,
            Recursive = true,
        };

        internal Option<string?> Wait = new("--wait")
        {
            Description = "precondition: draw until a frame marks itself ready (ui.MarkReady)",
            HelpName = "ready",
            Arity = ArgumentArity.ExactlyOne,
            Recursive = true,
        };

        internal Option<string?> WaitText = new("--wait-text")
        {
            Description = "precondition: draw until a frame draws this text or query",
            HelpName = "text",
            Recursive = true,
        };

        internal Option<double?> WaitTimeout = new("--wait-timeout")
        {
            Description = "the window for --wait and --wait-text, in seconds (default 10, max 3600)",
            HelpName = "seconds",
            Recursive = true,
        };

        internal Option<bool> Quiet = new("--quiet", "-q")
        {
            Description = "print only the payload, never the tool's own progress",
            Recursive = true,
        };

        internal Option<bool> Stdin = new("--stdin", "-s")
        {
            Description = "read one protocol command per line from stdin, with no command word",
            Recursive = true,
        };

        internal Option<bool> NoBuild = new("--no-build")
        {
            Description = "reuse the last build instead of rebuilding",
            Recursive = true,
        };

        /// <summary>
        /// A fresh <c>--json</c> per command. Sharing one instance across commands makes the
        /// symbol resolve ambiguously, and a flag nobody typed reads back as present.
        /// </summary>
        internal Option<bool> Json() => new("--json") { Description = "answer as JSON instead of text" };

        internal Argument<string> ApiName = new("name") { Description = "a type or member: ChartOpts, Ui.LineChart, Button" };

        internal Argument<string?> IconsFilter = OptionalText("filter", "only the icons whose name contains this");

        /// <summary>
        /// The shorthand `docs testing`. Hidden from the usage lines because it belongs to the
        /// `docs` command, and rendering it would suggest `docs testing search <words>`.
        /// </summary>
        internal Argument<string?> DocsWhat = OptionalText("what", "a topic, a document, or a document#section");


        internal Argument<string> DocsReadWhat = new("what") { Description = "a topic, a document, or a document#section" };

        internal Argument<string[]> DocsWords = new("words")
        {
            Description = "the words to look for",
            Arity = ArgumentArity.OneOrMore,
        };

        internal void Validate()
        {
            Wait.AcceptOnlyFromAmong("ready");

            Frames.Validators.Add(result =>
            {
                if (Peek<int>(result) is { } frames && frames < 0)
                    result.AddError("--frames needs a count of at least 0");
            });

            WaitTimeout.Validators.Add(result =>
            {
                if (Peek<double>(result) is { } seconds && !InRange(seconds))
                    result.AddError($"--wait-timeout needs seconds above 0 and at most {Cli.Options.MaxWaitSeconds:0}");
            });
        }

        /// <summary>
        /// A validator-safe read. System.CommandLine has already recorded a conversion failure
        /// (and reports it), but asking for the value would throw instead of returning.
        /// </summary>
        internal static T? Peek<T>(SymbolResult result) where T : struct
        {
            try
            {
                return result switch
                {
                    OptionResult option => option.GetValueOrDefault<T?>(),
                    ArgumentResult argument => argument.GetValueOrDefault<T?>(),
                    _ => null,
                };
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        internal static bool InRange(double seconds) =>
            double.IsFinite(seconds) && seconds > 0 && seconds <= Cli.Options.MaxWaitSeconds;
    }

    // One initializer, because field initializers run in textual order: two separate ones could
    // leave Root assigned before the symbols that give it its meaning.
    private static void NoOp(ParseResult _)
    {
        // Dispatch happens in Cli.Run; an action exists only so a parent command can be terminal.
    }

    private static readonly (Symbols Symbols, RootCommand Root) Built = Build();

    private static Symbols S => Built.Symbols;

    internal static RootCommand Root => Built.Root;

    private static (Symbols Symbols, RootCommand Root) Build()
    {
        var symbols = new Symbols();
        symbols.Validate();

        var root = new RootCommand("drive a CuiSharp app from a shell or an agent, and read its rules offline.");
        // Without an action a parent command is only a namespace; `dotnet cui --stdin` and the
        // help path need the root itself to be a valid terminal.
        root.SetAction(NoOp);

        foreach ((string name, string description, Argument[] arguments) in Verbs)
        {
            var command = new Command(name, description);
            foreach (Argument argument in arguments) command.Arguments.Add(argument);
            root.Subcommands.Add(command);
        }

        var ps = new Command("ps", "list running live CuiSharp windows (started with --live)");
        ps.Options.Add(symbols.Json());
        ps.SetAction(NoOp);
        root.Subcommands.Add(ps);

        root.Subcommands.Add(Api(symbols));
        root.Subcommands.Add(Icons(symbols));
        root.Subcommands.Add(Docs(symbols));

        foreach (Option option in new Option[]
                 {
                     symbols.Project, symbols.App, symbols.Framework, symbols.Frames, symbols.Attach,
                     symbols.Wait, symbols.WaitText, symbols.WaitTimeout, symbols.Quiet, symbols.Stdin,
                     symbols.NoBuild,
                 })
        {
            root.Options.Add(option);
        }

        return (symbols, root);
    }

    private static Command Api(Symbols symbols)
    {
        var api = new Command("api", "one declaration from the API catalog inside this tool, offline");
        api.Arguments.Add(symbols.ApiName);
        api.Options.Add(symbols.Json());
        return api;
    }

    private static Command Icons(Symbols symbols)
    {
        var icons = new Command("icons", "every icon constant: name, code point, and the literal to paste");
        icons.Arguments.Add(symbols.IconsFilter);
        icons.Options.Add(symbols.Json());
        return icons;
    }

    private static Command Docs(Symbols symbols)
    {
        symbols.DocsWhat.Hidden = true;
        var docs = new Command(
            "docs",
            "the guide and the reference bundled with this tool, read offline; "
                + "`docs <topic>` is shorthand for `docs read <topic>`")
        {
            symbols.DocsWhat,
        };
        docs.Options.Add(symbols.Json());
        // A command with subcommands still has to be usable on its own: `dotnet cui docs` is the
        // index, and `dotnet cui docs SKILL#pitfalls` is one section.
        docs.SetAction(NoOp);

        var read = new Command("read", "print one topic, one document, or one #section of one");
        read.Arguments.Add(symbols.DocsReadWhat);
        read.Options.Add(symbols.Json());
        read.SetAction(NoOp);
        docs.Subcommands.Add(read);

        var search = new Command("search", "the sections that answer a question, ranked");
        search.Arguments.Add(symbols.DocsWords);
        search.Options.Add(symbols.Json());
        search.SetAction(NoOp);
        docs.Subcommands.Add(search);

        return docs;
    }

    // ---- argument builders ------------------------------------------------------------------

    private static Argument<string> Text(string name, string description) =>
        new(name) { Description = description };

    private static Argument<string?> OptionalText(string name, string description) =>
        new(name) { Description = description, Arity = ArgumentArity.ZeroOrOne };

    private static Argument<float?> Number(string name, bool required) =>
        new(name) { Description = name, Arity = required ? ArgumentArity.ExactlyOne : ArgumentArity.ZeroOrOne };

    private static Argument<int?> Count(string description)
    {
        var count = new Argument<int?>("count") { Description = description, Arity = ArgumentArity.ZeroOrOne };
        count.Validators.Add(result =>
        {
            if (Symbols.Peek<int>(result) is { } value && value < 1)
                result.AddError("needs a count of at least 1");
        });
        return count;
    }

    private static Argument<double?> Seconds(string description)
    {
        var seconds = new Argument<double?>("seconds") { Description = description, Arity = ArgumentArity.ZeroOrOne };
        seconds.Validators.Add(result =>
        {
            if (Symbols.Peek<double>(result) is { } value && !Symbols.InRange(value))
                result.AddError($"needs seconds above 0 and at most {Cli.Options.MaxWaitSeconds:0}");
        });
        return seconds;
    }

    // ---- parse result → Options -------------------------------------------------------------

    /// <summary>
    /// One parsed command line: the help, the version, the diagnostics, or the options to
    /// dispatch. <paramref name="NothingAsked"/> is the bare invocation, which is a usage error
    /// rather than a request for the help.
    /// </summary>
    internal sealed record Parsed(
        bool Help,
        bool Version,
        bool NothingAsked,
        Cli.Options? Options,
        IReadOnlyList<string> Errors,
        ParseResult Result);

    /// <summary>
    /// Reads a command line. This is the only path: the dispatcher and the tests both come
    /// through here, so what a test asserts is what a user gets.
    /// </summary>
    internal static Parsed Parse(string[] args)
    {
        string[] grammarArgs = SplitAppArguments(args, out string[] appArguments);
        ParseResult result = Root.Parse(grammarArgs);

        var errors = new List<string>();
        foreach (ParseError parseError in result.Errors) errors.Add(Cli.Humanize(parseError.Message));
        if (errors.Count == 0 && !TryCheckCombinations(result, out string conflict)) errors.Add(conflict);
        if (errors.Count > 0 && NearestCommand(grammarArgs) is { } meant)
        {
            // One unknown verb leaves every following token unrecognised too. Only the verb is
            // the mistake, so say that once and name the verb that was meant.
            errors = [errors[0], $"Did you mean `dotnet cui {meant}`? Run `dotnet cui --help` for the rest."];
        }
        if (errors.Count > 0) return new Parsed(false, false, false, null, errors, result);

        // `--version` is System.CommandLine's own action, and it is root-only: answering it
        // here keeps the help page from advertising a switch that would otherwise fall through
        // to the help screen.
        if (Root.Options.Any(option => option is VersionOption && result.GetResult(option) is { Implicit: false }))
            return new Parsed(false, true, false, null, [], result);

        // The help does not need resolved options, and resolving would demand the arguments it
        // is about to explain: `dotnet cui click --help` must not require a label.
        if (result.Action is HelpAction) return new Parsed(true, false, false, null, [], result);

        Cli.Options options = Resolve(result, appArguments);
        if (options.NoCommand && !options.Streaming)
        {
            // A bare `--wait ready` still means "wait, then show me the tree"; anything else has
            // no command to run, and guessing would hide a typo.
            if (options.WaitLine is null) return new Parsed(true, false, true, null, [], result);
            options = options with { Command = "tree", NoCommand = false };
        }

        return new Parsed(false, false, false, options, [], result);
    }

    /// <summary>
    /// The verb a mistyped first word was probably meant to be: same opening letters, or a
    /// near miss. A suggestion is worth this much arithmetic because the alternative is a reader
    /// (or an agent) spending a turn re-reading the help to find out it typed one letter wrong.
    /// </summary>
    private static string? NearestCommand(string[] args)
    {
        string? token = args.FirstOrDefault(word => word.Length > 0 && word[0] != '-');
        if (token is null || Root.Subcommands.Any(command => command.Name == token)) return null;

        string? best = null;
        int bestDistance = int.MaxValue;
        foreach (Command command in Root.Subcommands)
        {
            int distance = Distance(token, command.Name);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = command.Name;
            }
        }

        // One edit in five letters is a typo; more than that is a different word.
        int budget = Math.Max(1, Math.Min(3, token.Length / 2));
        return bestDistance <= budget ? best : null;
    }

    private static int Distance(string left, string right)
    {
        ReadOnlySpan<char> a = left.ToLowerInvariant();
        ReadOnlySpan<char> b = right.ToLowerInvariant();
        Span<int> previous = stackalloc int[b.Length + 1];
        Span<int> current = stackalloc int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            previous.Clear();
            current.CopyTo(previous);
        }

        return previous[b.Length];
    }

    /// <summary>
    /// Splits the app's own arguments off at the first standalone <c>--</c>. Doing it here keeps
    /// one owner of that rule: everything to the right is handed to the app untouched, and the
    /// grammar never has to treat an app flag as a mistake.
    /// </summary>
    internal static string[] SplitAppArguments(string[] args, out string[] appArguments)
    {
        int separator = Array.IndexOf(args, "--");
        if (separator < 0)
        {
            appArguments = [];
            return args;
        }

        appArguments = args[(separator + 1)..];
        return args[..separator];
    }

    /// <summary>
    /// Rules that span two options. They live here rather than in a <c>Command.Validators</c> on
    /// the root because root validators do not run when a subcommand is the one being invoked.
    /// </summary>
    internal static bool TryCheckCombinations(ParseResult result, out string message)
    {
        bool wait = result.GetResult(S.Wait) is not null;
        bool waitText = result.GetResult(S.WaitText) is not null;
        if (wait && waitText)
        {
            message = "choose one wait: --wait ready or --wait-text <text>, not both";
            return false;
        }

        if (result.GetResult(S.WaitTimeout) is not null && !wait && !waitText)
        {
            message = "--wait-timeout needs --wait or --wait-text to bound";
            return false;
        }

        message = "";
        return true;
    }

    /// <summary>Turns a successfully parsed command line into the options the tool dispatches on.</summary>
    internal static Cli.Options Resolve(ParseResult result, string[] appArguments)
    {
        CommandResult matched = result.CommandResult;
        // A RootCommand takes its name from the running assembly (the test host, the shim...),
        // so "nothing was asked for" is decided by identity, never by an empty string.
        string name = ReferenceEquals(matched.Command, Root) ? "" : matched.Command.Name;

        (string command, string argument) = name switch
        {
            // `docs read X` and `docs search a b` are subcommands so their help is per-verb; the
            // docs reader still takes one line, so the verb goes back on the front here.
            "read" or "search" => ("docs", $"{name} {Join(result, matched)}"),
            "api" => ("api", Join(result, matched)),
            "icons" => ("icons", Join(result, matched)),
            "docs" => ("docs", Join(result, matched)),
            "ps" => ("ps", ""),
            _ => (name, Join(result, matched)),
        };

        bool waiting = result.GetResult(S.Wait) is not null || result.GetResult(S.WaitText) is not null;
        double waitSeconds = result.GetValue(S.WaitTimeout) ?? Cli.Options.DefaultWaitSeconds;
        string? waitLine = null;
        if (waiting)
        {
            string seconds = waitSeconds.ToString("R", CultureInfo.InvariantCulture);
            waitLine = result.GetResult(S.WaitText) is not null
                ? $"waittext {seconds} {result.GetValue(S.WaitText)}"
                : $"wait {seconds}";
        }

        OptionResult? attach = result.GetResult(S.Attach);
        bool streaming = result.GetValue(S.Stdin);
        return new Cli.Options
        {
            Command = command,
            Argument = argument,
            // `--wait ready` with no command means "wait, then show me the tree".
            NoCommand = command.Length == 0,
            Streaming = streaming,
            Frames = result.GetValue(S.Frames) ?? 0,
            Quiet = result.GetValue(S.Quiet),
            Json = JsonRequested(result, matched),
            Attach = attach is not null,
            AttachTarget = attach?.GetValueOrDefault<string?>() is { Length: > 0 } target ? target : null,
            NoBuild = result.GetValue(S.NoBuild),
            Project = result.GetValue(S.Project),
            Host = result.GetValue(S.App),
            Framework = result.GetValue(S.Framework),
            AppArgs = appArguments,
            WaitSeconds = waiting ? waitSeconds : 0,
            WaitLine = waitLine,
        };
    }

    /// <summary>
    /// Whether the command that ran was asked for JSON. Each command owns its own <c>--json</c>,
    /// so the answer is looked up on the command that was actually matched, never globally.
    /// </summary>
    private static bool JsonRequested(ParseResult result, CommandResult matched) =>
        matched.Command.Options.Any(option =>
            // A bool option gets a result even when nobody typed it, and carries no tokens even
            // when somebody did: only Implicit tells the two apart.
            option.Name == "--json" && result.GetResult(option) is { Implicit: false });

    /// <summary>
    /// One protocol line from a command's arguments, in declaration order: the wire protocol is a
    /// line, so the join is by the value that was actually supplied, formatted invariantly.
    /// </summary>
    private static string Join(ParseResult result, CommandResult matched)
    {
        var parts = new List<string>();
        foreach (Argument argument in matched.Command.Arguments)
        {
            // The argument's declared type is the protocol slot's type, so the formatting is
            // decided once, here, and stays invariant whatever the machine's culture is.
            string? text = argument switch
            {
                Argument<string[]> many => Join(result.GetValue(many)),
                Argument<int?> number => result.GetValue(number)?.ToString(CultureInfo.InvariantCulture),
                Argument<float?> number => result.GetValue(number)?.ToString("R", CultureInfo.InvariantCulture),
                Argument<double?> number => result.GetValue(number)?.ToString("R", CultureInfo.InvariantCulture),
                Argument<string> one => result.GetValue(one),
                _ => null,
            };

            if (!string.IsNullOrEmpty(text)) parts.Add(text);
        }

        return string.Join(' ', parts);

        static string Join(string[]? words) => words is null ? "" : string.Join(' ', words);
    }
}
