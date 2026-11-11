using System.Globalization;
using System.Text;

namespace CuiSharp.Hosting;

/// <summary>
/// Runs an app with no window and answers commands on a stream, so a tool — <c>dotnet cui</c>,
/// an MCP server, a shell script — can look at a frame and act on it.
/// </summary>
/// <remarks>
/// <para>
/// One command per line in, one framed answer out:
/// </para>
/// <code>
/// in:  tree
/// out: CUI-OK 27
///      Frame
///        Button "Save"
/// </code>
/// <para>
/// The header carries the payload length in bytes, so a label that contains anything at all
/// still round-trips: the reader takes exactly that many bytes and never has to guess where an
/// answer ended. <c>CUI-ERR</c> carries the message of a command that could not be obeyed.
/// </para>
/// <para>
/// An app turns this on by running headless — <see cref="Requested"/> reads
/// <c>CUISHARP_HEADLESS=1</c> or a <c>--headless</c> argument — and
/// <see cref="CuiApplication"/> then serves these commands instead of opening a window.
/// </para>
/// </remarks>
public static class HeadlessLoop
{
    /// <summary>The environment variable an app checks to run without a window.</summary>
    public const string EnvironmentVariable = "CUISHARP_HEADLESS";

    /// <summary>
    /// The longest answer the loop will send. A whole-page tree of a large window is kilobytes; a
    /// megabyte means the caller asked for something that cannot travel down this pipe, and a
    /// client that pre-allocates the announced length should not be told to allocate it.
    /// </summary>
    internal const int MaxAnswerBytes = 16 * 1024 * 1024;

    /// <summary>
    /// The longest command line the loop will read. A command is a word and a label; anything past
    /// this is a stream with no newline in it, and reading it to the end would be the app's memory.
    /// </summary>
    internal const int MaxCommandChars = 64 * 1024;

    private static readonly string[] Commands =
    [
        "tree", "print", "find", "click", "type", "key", "focus", "move", "wheel", "frame", "pulse", "faults", "png", "quit",
    ];

    /// <summary>True when this process was asked to run without a window.</summary>
    public static bool Requested(string[]? args = null) =>
        Environment.GetEnvironmentVariable(EnvironmentVariable) is "1" or "true" or "yes" or "on"
        || (args ?? Environment.GetCommandLineArgs()).Contains("--headless", StringComparer.Ordinal);

    /// <summary>The commands the loop understands, for a client that wants to list them.</summary>
    public static IReadOnlyList<string> Known => Commands;

    /// <summary>
    /// Serves commands for an app that draws with a callback: a gallery, a demo, a single-file
    /// tool, or a hosted component — the host is the callback either way.
    /// </summary>
    /// <remarks>
    /// <paramref name="window"/> is the same description the window would have been built from,
    /// so the size, the scale, the theme and the accent of the frames here are the app's own. An
    /// app turns this on by running headless, so nothing in the app has to know the difference
    /// between a window and a tool.
    /// </remarks>
    /// <param name="draw">Drawn once per frame — for a hosted app, the host's frame.</param>
    /// <param name="window">The window the app asked for.</param>
    /// <param name="input">Commands; the process's standard input by default.</param>
    /// <param name="output">Answers; the process's standard output by default.</param>
    public static int Run(DrawFn draw, CuiAppOptions window, TextReader? input = null, TextWriter? output = null)
    {
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(window);
        if (input is null && output is null) ClaimStandardOutput();
        using HeadlessApp app = HeadlessApp.Start(
            draw,
            window.Width,
            window.Height,
            theme: window.Theme,
            accent: window.Accent);
        return Run(app, input, output);
    }

    /// <summary>Serves commands against an app the caller already started.</summary>
    public static int Run(HeadlessApp app, TextReader? input = null, TextWriter? output = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        // UTF-8 on the standard streams, explicitly: Console.Out follows the console code page
        // on Windows, so a label with a chevron in it would arrive as fewer bytes than the
        // header announced and the client would wait for bytes that never come.
        if (input is null && output is null) ClaimStandardOutput();

        using TextReader ownedSource = input is null
            ? new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false))
            : TextReader.Null;
        using TextWriter ownedSink = output is null
            ? new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false }
            : TextWriter.Null;
        TextReader source = input ?? ownedSource;
        TextWriter sink = output ?? ownedSink;

        // The client waits for this before it sends anything, so a slow startup is never read
        // as a failure.
        sink.Write($"CUI-READY {app.Width} {app.Height}\n");
        sink.Flush();

        while (true)
        {
            string? read;
            try
            {
                read = ReadCommand(source);
            }
            catch (ArgumentException error)
            {
                // The stream is still at an unknown place in an over-long line, so the session
                // ends with the reason instead of answering commands parsed from its middle.
                Answer(sink, ok: false, error.Message);
                return 1;
            }

            if (read is null) break;
            string line = read.Trim();
            if (line.Length == 0) continue;

            int space = line.IndexOf(' ', StringComparison.Ordinal);
            string command = (space < 0 ? line : line[..space]).ToLowerInvariant();
            string argument = space < 0 ? "" : line[(space + 1)..];

            if (command is "quit" or "exit")
            {
                Answer(sink, ok: true, "");
                return 0;
            }

            try
            {
                Answer(sink, ok: true, Execute(app, command, argument));
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or FormatException or OverflowException)
            {
                Answer(sink, ok: false, error.Message);
            }
        }

        return 0;
    }

    /// <summary>
    /// One command, with a bound: a client that never sends a newline is answered with why its
    /// command was too long instead of growing the loop's buffer until the process dies.
    /// </summary>
    internal static string? ReadCommand(TextReader source)
    {
        var line = new StringBuilder(128);
        while (true)
        {
            int next = source.Read();
            if (next < 0) return line.Length == 0 ? null : line.ToString();
            if (next == (char)10) return line.ToString().TrimEnd((char)13);
            if (line.Length >= MaxCommandChars)
            {
                throw new ArgumentException(
                    $"A command line longer than {MaxCommandChars} characters was not read. Commands end with a newline.");
            }

            line.Append((char)next);
        }
    }

    /// <summary>
    /// Anything the app prints belongs on stderr: stdout is the protocol, and a stray
    /// <c>Console.WriteLine</c> — in the app, in a service it resolved, in a logger — would
    /// otherwise land in the middle of a frame and corrupt it.
    /// </summary>
    internal static void ClaimStandardOutput() => Console.SetOut(Console.Error);

    private static string Execute(HeadlessApp app, string command, string argument) => command switch
    {
        "tree" or "print" => app.Print(),
        "find" => Find(app, argument),
        "click" => Click(app, argument),
        "type" => app.Type(argument).Print(),
        "key" => Key(app, argument),
        "focus" => app.Focus().Print(),
        "move" => Move(app, argument),
        "wheel" => Wheel(app, argument),
        "frame" => Frame(app, argument),
        "pulse" => Pulse(app, argument),
        "faults" => app.Faults.Count == 0 ? "" : string.Join("\n", app.Faults),
        "png" => SavePng(app, argument),
        _ => throw new InvalidOperationException($"Unknown command \"{command}\". Known: {string.Join(", ", Commands)}."),
    };

    private static string Find(HeadlessApp app, string argument)
    {
        WidgetTarget target = app.Find(argument);
        return $"{target.Kind} \"{target.Text}\" at {target.Rect}";
    }

    /// <summary>
    /// Draws frames without printing the tree: a client that wants to load the app should not
    /// have its measurement disturbed by the measuring — printing a two-thousand-node tree is
    /// work no real frame does. The answer carries the frame count and the managed heap size,
    /// so a sampler can watch allocation without touching the app.
    /// </summary>
    private static string Pulse(HeadlessApp app, string argument)
    {
        int frames = argument.Length == 0 ? 1 : int.Parse(argument, CultureInfo.InvariantCulture);
        if (frames < 1) throw new ArgumentException($"pulse needs a count of at least 1, not {frames}.");
        app.Frame(frames);
        return $"{frames} heap {GC.GetTotalMemory(false)}";
    }

    private static string Click(HeadlessApp app, string argument)
    {
        // Clicking answers with the frame the click produced: that is what the caller wants to
        // read next, and it saves a round trip. CUI reports the click on the release frame, and
        // a widget drawn earlier in that frame still shows the state from before it, so one
        // more frame is what proves the handler ran.
        app.Find(argument).Click();
        return app.Frame().Print();
    }

    /// <summary>
    /// The keys, once: a command line names a key the way a person would (<c>Tab</c>,
    /// <c>PageDown</c>, <c>F5</c>), and CUI's own spelling of the same key answers the same
    /// question, so both are tried. A scan of the values in a type named <c>Key</c> is not
    /// reflection, and it keeps the binary smaller than a dictionary would.
    /// </summary>
    private static readonly Key[] Keys = Enum.GetValues<Key>();

    private static Key ParseKey(string argument)
    {
        string wanted = argument.Trim();
        foreach (Key key in Keys)
        {
            if (key.ToString().Equals(wanted, StringComparison.OrdinalIgnoreCase) ||
                Ui.KeyName(key).Equals(wanted, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        throw new ArgumentException($"Unknown key \"{argument}\".", nameof(argument));
    }

    private static string Key(HeadlessApp app, string argument) => app.Key(ParseKey(argument)).Print();

    /// <summary>
    /// Puts the pointer somewhere without clicking: hover states, tooltips, and getting it off a
    /// control before a screenshot so the picture is not of a hover.
    /// </summary>
    private static string Move(HeadlessApp app, string argument)
    {
        string[] parts = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            throw new ArgumentException("move needs two numbers: `move 10 20`.", nameof(argument));

        return app.MoveTo(
            float.Parse(parts[0], CultureInfo.InvariantCulture),
            float.Parse(parts[1], CultureInfo.InvariantCulture)).Print();
    }

    /// <summary>
    /// Draws <paramref name="argument"/> frames and answers with the tree. One is the default;
    /// a count of zero or less is a client bug, and saying so beats drawing nothing quietly.
    /// </summary>
    private static string Frame(HeadlessApp app, string argument)
    {
        int frames = argument.Length == 0 ? 1 : int.Parse(argument, CultureInfo.InvariantCulture);
        if (frames < 1) throw new ArgumentException($"frame needs a count of at least 1, not {frames}.");
        return app.Frame(frames).Print();
    }

    /// <summary>
    /// Scrolls at a point: <c>wheel 120 400 -8</c> hovers the sidebar and turns the wheel eight
    /// notches down. That is how a control below a scroll area's fold is reached before it can
    /// be clicked; give the spring a <c>frame</c> or two to settle.
    /// </summary>
    private static string Wheel(HeadlessApp app, string argument)
    {
        string[] parts = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 3 or > 4)
            throw new ArgumentException("wheel needs three numbers and an optional fourth: `wheel 120 400 -8 [0]`.", nameof(argument));

        return app.ScrollAt(
            float.Parse(parts[0], CultureInfo.InvariantCulture),
            float.Parse(parts[1], CultureInfo.InvariantCulture),
            float.Parse(parts[2], CultureInfo.InvariantCulture),
            parts.Length == 4 ? float.Parse(parts[3], CultureInfo.InvariantCulture) : 0f).Print();
    }

    private static string SavePng(HeadlessApp app, string argument)
    {
        string path = app.SavePng(argument.Length == 0 ? null : argument);
        return path;
    }

    internal static void Answer(TextWriter sink, bool ok, string payload, int limit = MaxAnswerBytes)
    {
        int bytes = Encoding.UTF8.GetByteCount(payload);
        if (bytes > limit)
        {
            // Bounded, and honest about it: a client that asked for too much learns the size and
            // the limit rather than reading a tree that quietly stops halfway.
            payload =
                $"The answer is {bytes} bytes, past this protocol's {limit}-byte limit. " +
                "Ask for less: one control, one page of the tree, or a smaller window.";
            ok = false;
            bytes = Encoding.UTF8.GetByteCount(payload);
        }

        sink.Write($"{(ok ? "CUI-OK" : "CUI-ERR")} {bytes}\n");
        if (bytes > 0) sink.Write(payload);
        sink.Write('\n');
        sink.Flush();
    }
}
