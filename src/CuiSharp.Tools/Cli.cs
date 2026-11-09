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
/// <code>
/// dotnet cui tree                     # what the first frame drew, nested
/// dotnet cui click Save               # click a control, print the frame that reacted
/// dotnet cui type Milk                # type into the focused field
/// dotnet cui png shot.png             # the frame as a PNG
/// dotnet cui tree --project src/App   # another app
/// dotnet cui tree --app bin/App.exe   # a published build, not a rebuild
/// dotnet cui tree -- --page=2         # arguments for the app itself
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
          frame [count]       draw more frames, then print the tree
          faults              what the app threw while drawing, if anything
          png <path>          save the last frame as a PNG

        Options:
          -p, --project <path>  the app to run (a project file or its directory). Default: here
              --app <exe>       run a published build instead of building
              --framework <tfm> the target framework, when the project has more than one
              --frames <count>  draw this many frames before the command
          -s, --stdin           read commands from stdin and print each answer, one process
          -q, --quiet           only print the payload

        Anything after `--` goes to the app.
        """;

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


            (string host, string[] hostArgs) = options.Host is { } exe
                ? (exe, options.AppArgs)
                : AppLocator.Resolve(options.Project, options.NoBuild, error, new(Framework: options.Framework)) is { } assembly
                    ? ("dotnet", [assembly, .. options.AppArgs])
                    : throw new ToolException($"No .NET app found at \"{options.Project ?? Directory.GetCurrentDirectory()}\".");

            // The app's own output is the app's own business: it goes where a shell would send
            // the app's diagnostics, and the protocol on stdout stays clean.
            using HeadlessClient client = HeadlessClient.Start(
                host, hostArgs, echo: options.Quiet ? null : error.WriteLine);
            if (options.Frames > 0) client.Send($"frame {options.Frames}");

            if (options.Streaming) return Relay(client, input, output, error);

            Answer answer = client.Send(options.Line);
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
    /// Feeds commands from stdin to one app process, so a session of ten commands pays for one
    /// startup instead of ten. Answers go to stdout in order.
    /// </summary>
    private static int Relay(HeadlessClient client, TextReader? input, TextWriter output, TextWriter error)
    {
        TextReader source = input ?? Console.In;
        while (source.ReadLine() is { } line)
        {
            if (line.Trim().Length == 0) continue;
            Answer answer = client.Send(line.Trim());
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

        internal static Options Parse(string[] args)
        {
            if (args.Length == 0) return new Options { Help = true };

            string? project = null, host = null, framework = null;
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
                        frames = int.Parse(Value(args, ref i), CultureInfo.InvariantCulture);
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

            return new Options
            {
                Command = words.Count > 0 ? words[0] : "",
                Argument = words.Count > 1 ? string.Join(' ', words.Skip(1)) : "",
                Frames = frames,
                Quiet = quiet,
                Streaming = streaming,
                NoBuild = noBuild,
                Help = help || (words.Count == 0 && !streaming),
                Project = project,
                Host = host,
                Framework = framework,
                AppArgs = [.. appArgs],
            };
        }

        private static string Value(string[] args, ref int index)
        {
            if (index + 1 >= args.Length) throw new ToolException($"{args[index]} needs a value.");
            return args[++index];
        }
    }
}
