using System.CommandLine;
using System.Diagnostics;
using System.Text;
using CuiSharp.Tools;

using CliArgument = System.CommandLine.Argument;
using CliCommand = System.CommandLine.Command;
using CliHelpOption = System.CommandLine.Help.HelpOption;
using CliOption = System.CommandLine.Option;

namespace CuiSharp.Tests;

/// <summary>
/// How <c>dotnet cui</c> reads its own arguments, through the same <see cref="Grammar.Parse"/>
/// the tool itself dispatches on. The important distinctions: a command owns its arity, an option
/// owns its value, everything after <c>--</c> belongs to the app, and a typo is reported rather
/// than sent to the app as a command it will reject.
/// </summary>
public class ToolArgumentTests
{
    [Fact]
    public void AWordIsTheCommandAndTheRestIsItsArgument()
    {
        Grammar.Parsed parsed = Grammar.Parse(["click", "Save"]);

        Assert.Empty(parsed.Errors);
        Assert.Equal("click", parsed.Options!.Command);
        Assert.Equal("Save", parsed.Options.Argument);
        Assert.Equal("click Save", parsed.Options.Line);
    }

    [Fact]
    public void ALabelWithSpacesIsOneQuotedArgument()
    {
        // The shell hands a quoted label over as one token and it stays one label. Splitting it
        // back into words would make `click Save now` a two-argument command that no app knows.
        Grammar.Parsed parsed = Grammar.Parse(["type", "Hello world"]);

        Assert.Empty(parsed.Errors);
        Assert.Equal("type Hello world", parsed.Options!.Line);
    }

    [Fact]
    public void ASecondWordWhereOneArgumentIsDeclaredIsRefusedNotSilentlyJoined()
    {
        // `type Hello world` used to be joined back into "Hello world"; the arity now says one
        // argument, so an unquoted label is a usage error rather than a different label.
        Grammar.Parsed parsed = Grammar.Parse(["type", "Hello", "world"]);

        Assert.NotEmpty(parsed.Errors);
        Assert.Contains("world", string.Join("; ", parsed.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void FlagsBeforeTheSeparatorAreTheToolsAndAfterItAreTheApps()
    {
        Grammar.Parsed parsed = Grammar.Parse(["tree", "--frames", "3", "--no-build", "--", "--page=2", "--dark"]);

        Assert.Empty(parsed.Errors);
        Assert.Equal("tree", parsed.Options!.Command);
        Assert.Equal(3, parsed.Options.Frames);
        Assert.True(parsed.Options.NoBuild);
        Assert.Equal(["--page=2", "--dark"], parsed.Options.AppArgs);
    }

    [Fact]
    public void AFlagTheToolDoesNotKnowIsReportedRatherThanForwarded()
    {
        Grammar.Parsed parsed = Grammar.Parse(["tree", "--pgae=2"]);

        Assert.NotEmpty(parsed.Errors);
        Assert.Contains("--pgae=2", string.Join("; ", parsed.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void NoCommandIsHelp()
    {
        Assert.True(Grammar.Parse([]).Help);
        Assert.True(Grammar.Parse([]).NothingAsked);

        // A session supplies its own commands on stdin, so it needs no verb of its own.
        Grammar.Parsed session = Grammar.Parse(["session", "--app", "x.exe"]);
        Assert.Empty(session.Errors);
        Assert.Equal("session", session.Options!.Command);
        Assert.False(session.Help);
    }

    [Fact]
    public void TheRootTakesNoOptionsSoABareLineCannotMeanSomethingElse()
    {
        // `dotnet cui --wait ready` used to invent a `tree`. With the options on the commands
        // that honour them, the line is a usage error with the ordering to fix it — which is
        // cheaper to read than an answer for a command nobody typed.
        Grammar.Parsed parsed = Grammar.Parse(["--wait", "ready"]);

        Assert.NotEmpty(parsed.Errors);
        Assert.Contains("belongs after the command", string.Join(" ", parsed.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void RunningTheToolWithoutACommandExplainsItselfOnTheErrorStream()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        // Exit 2 is a usage error, so the help goes where a usage error goes: a pipeline reading
        // stdout must not receive a help page in place of an answer.
        Assert.Equal(2, Cli.Run([], output, error));
        Assert.Equal("", output.ToString());
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("tree", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnAttachTargetIsAnOptionValueAndNeverPartOfTheLabel()
    {
        // The bug this pins: `click "中文" -a 15036` left `15036` a positional word, so the tool
        // looked for a widget whose label was "中文 15036" and reported that none existed.
        Grammar.Parsed parsed = Grammar.Parse(["click", "中文", "-a", "15036"]);

        Assert.Empty(parsed.Errors);
        Assert.True(parsed.Options!.Attach);
        Assert.Equal("15036", parsed.Options.AttachTarget);
        Assert.Equal("click 中文", parsed.Options.Line);
    }

    [Fact]
    public void AnAttachWithNoTargetPicksTheOnlyLiveWindow()
    {
        Grammar.Parsed parsed = Grammar.Parse(["tree", "-a"]);

        Assert.Empty(parsed.Errors);
        Assert.True(parsed.Options!.Attach);
        Assert.Null(parsed.Options.AttachTarget);
    }

    [Fact]
    public void ANegativeNumberIsAValueAndNotAnOption()
    {
        // `wheel` scrolls up with a negative dy; a parser that reads every leading dash as an
        // option cannot express the one thing the command exists for.
        Grammar.Parsed parsed = Grammar.Parse(["wheel", "120", "400", "-8"]);

        Assert.Empty(parsed.Errors);
        Assert.Equal("wheel 120 400 -8", parsed.Options!.Line);
    }

    [Fact]
    public void EveryCommandAndOptionTheHelpListsCanBeTyped()
    {
        // The help is generated from this grammar, so this walks exactly what a reader is shown:
        // every command with its required arguments, and every option in both its spellings. An
        // option the help advertises as taking a value but the parser ignores fails here.
        // Every command, by its own name and by each alias: the help names both, so both work.
        foreach (string name in Grammar.Root.Subcommands.SelectMany(c => c.Aliases.Prepend(c.Name)))
        {
            CliCommand command = Grammar.Root.Subcommands.First(c => c.Name == name || c.Aliases.Contains(name));
            var argv = new List<string> { name };
            // Every declared slot, optional ones included: the point is the position, and a
            // value in an optional slot is still a legal command line.
            foreach (CliArgument argument in command.Arguments) argv.Add(SampleArgument(argument));

            Grammar.Parsed bare = Grammar.Parse([.. argv]);
            Assert.True(bare.Errors.Count == 0, $"`{string.Join(' ', argv)}` did not parse: {string.Join("; ", bare.Errors)}");
        }

        // Every option every command declares, in both spellings, on the command that declares
        // it: an option the help advertises as taking a value but the parser ignores fails here,
        // and so does an option the help lists on a command that cannot honour it.
        foreach (CliCommand command in Grammar.Root.Subcommands)
        {
            foreach (CliOption option in command.Options.Where(o => o is not CliHelpOption and not VersionOption))
            {
                foreach (string spelling in new[] { option.Name }.Concat(option.Aliases))
                {
                    var argv = new List<string> { command.Name };
                    foreach (CliArgument argument in command.Arguments) argv.Add(SampleArgument(argument));
                    argv.Add(spelling);
                    if (option.ValueType != typeof(bool)) argv.Add(SampleOption(option));
                    // A timeout bounds a wait, so it is only legal beside one.
                    if (spelling is "--wait-timeout") argv.InsertRange(1, ["--wait", "ready"]);

                    Grammar.Parsed parsed = Grammar.Parse([.. argv]);
                    Assert.True(parsed.Errors.Count == 0, $"`{spelling}` on `{command.Name}` did not parse: {string.Join("; ", parsed.Errors)}");
                }
            }
        }
    }

    /// <summary>A value the argument's declared type can hold, so the test walks the grammar's
    /// shape without hard-coding which slot is a number today.</summary>
    private static string SampleArgument(CliArgument argument) => argument.ValueType switch
    {
        Type t when t == typeof(int) || t == typeof(int?)
            || t == typeof(float) || t == typeof(float?)
            || t == typeof(double) || t == typeof(double?) => "2",
        _ => "x",
    };

    private static string SampleOption(CliOption option) => option.Name switch
    {
        "--wait" => "ready",
        _ => option.ValueType == typeof(string) ? "x" : "2",
    };

    [Fact]
    public void TheVersionSwitchAnswersWithAVersionAndNotTheHelp()
    {
        // The generated help lists `--version`, so it has to do something: a switch that falls
        // through to the help screen is a switch that lies about itself.
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.Equal(0, Cli.Run(["--version"], output, error));
        Assert.Matches(@"\d+\.\d+", output.ToString());
        Assert.DoesNotContain("Usage:", output.ToString(), StringComparison.Ordinal);
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void AMistypedVerbNamesTheVerbMeant()
    {
        // One letter wrong is the mistake an agent (and a person) makes most, so the answer says
        // which verb was meant instead of only which one was rejected.
        Grammar.Parsed parsed = Grammar.Parse(["clik", "Save"]);

        Assert.NotEmpty(parsed.Errors);
        Assert.Contains("did you mean `dotnet cui click`", string.Join(" ", parsed.Errors), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AWordThatIsNoVerbAtAllGetsNoInventedSuggestion()
    {
        Grammar.Parsed parsed = Grammar.Parse(["migrate-history"]);

        Assert.NotEmpty(parsed.Errors);
        Assert.DoesNotContain("Did you mean", string.Join(" ", parsed.Errors), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HelpIsPerCommandAndNamesItsArguments()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.Equal(0, Cli.Run(["click", "--help"], output, error));

        string help = output.ToString();
        Assert.Contains("click <text>", help, StringComparison.Ordinal);
        Assert.Contains("--attach", help, StringComparison.Ordinal);
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void AWaitIsAPreconditionAndNotTheCommand()
    {
        // `png out.png --wait ready`: png is the command and keeps the payload; the wait is a
        // gate the way --frames is, so the line the command travels on stays untouched.
        Grammar.Parsed parsed = Grammar.Parse(["png", "out.png", "--wait", "ready"]);

        Assert.Empty(parsed.Errors);
        Assert.Equal("png out.png", parsed.Options!.Line);
        Assert.Equal("wait 10", parsed.Options.WaitLine);
        Assert.Equal(Cli.Options.DefaultWaitSeconds, parsed.Options.WaitSeconds);
    }

    [Fact]
    public void AWaitIsWrittenAfterTheCommandItGates()
    {
        Grammar.Parsed parsed = Grammar.Parse(["tree", "--wait", "ready", "--wait-timeout", "30"]);

        Assert.Empty(parsed.Errors);
        Assert.Equal("tree", parsed.Options!.Line);
        Assert.Equal("wait 30", parsed.Options.WaitLine);
        Assert.Equal(30, parsed.Options.WaitSeconds);
    }

    [Fact]
    public void AWaitTextCarriesItsTimeoutAndItsTextUnchanged()
    {
        Grammar.Parsed parsed = Grammar.Parse(
            ["click", "Save", "--wait-text", "添加 \"Save now\" 3", "--wait-timeout", "2.5"]);

        Assert.Empty(parsed.Errors);
        Assert.Equal("click Save", parsed.Options!.Line);
        Assert.Equal(2.5, parsed.Options.WaitSeconds);
        // The query is verbatim after the timeout: spaces, quotes and a numeric tail all stay
        // user text, and the number in front is the only one the app parses.
        Assert.Equal("waittext 2.5 添加 \"Save now\" 3", parsed.Options.WaitLine);
    }

    [Fact]
    public void AWaitCoexistsWithFramesInASession()
    {
        Grammar.Parsed parsed = Grammar.Parse(["session", "--frames", "3", "--wait", "ready"]);

        Assert.Empty(parsed.Errors);
        Assert.Equal("session", parsed.Options!.Command);
        Assert.Equal(3, parsed.Options.Frames);
        // The gate passes before any stdin line is relayed, and stdin carries the commands: no
        // protocol line is invented for the session itself.
        Assert.Equal("wait 10", parsed.Options.WaitLine);
    }

    [Fact]
    public void ASessionCannotBeGivenACommandWordToIgnore()
    {
        // `tree --stdin` used to read stdin and drop the `tree`. The session is a command now,
        // so the only way to reach it is to ask for it.
        Grammar.Parsed parsed = Grammar.Parse(["tree", "--stdin"]);

        Assert.NotEmpty(parsed.Errors);
        Assert.Contains("'--stdin'", string.Join(" ", parsed.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void AWaitOptionThatContradictsItselfIsRefused()
    {
        Assert.NotEmpty(Grammar.Parse(["tree", "--wait", "busy"]).Errors);
        Assert.NotEmpty(Grammar.Parse(["tree", "--wait", "ready", "--wait-text", "Save"]).Errors);
        Assert.NotEmpty(Grammar.Parse(["tree", "--wait-timeout", "3"]).Errors);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("soon")]
    [InlineData("3601")]
    public void AWaitTimeoutThatCannotBoundAWaitIsRefusedNotThrown(string value)
    {
        // TryParse accepts NaN and Infinity, and an unbounded timeout overflows the answer
        // deadline downstream — all of them are typos, and a typo is a sentence with exit=2.
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.Equal(2, Cli.Run(["tree", "--wait", "ready", "--wait-timeout", value], output, error));
        Assert.Contains("wait-timeout", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("-1")]
    [InlineData("99999999999")]
    public void AFramesCountThatCannotDrawIsRefusedNotThrown(string value)
    {
        // int.Parse used to throw FormatException and OverflowException out of the parser for
        // these; a negative count drew nothing while claiming it had drawn.
        Grammar.Parsed parsed = Grammar.Parse(["tree", "--frames", value]);

        Assert.NotEmpty(parsed.Errors);
        Assert.Contains("frames", string.Join("; ", parsed.Errors), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("wait 61", 91)]
    [InlineData("waittext 61 添加 \"Save now\" 3", 91)]
    [InlineData("Wait 61", 91)]
    [InlineData("wait 0.1", 30.1)]
    [InlineData("waittext 3600 data", 3630)]
    [InlineData("tree", 0)]
    [InlineData("png out.png", 0)]
    [InlineData("dwell 600", 0)]
    [InlineData("wait", 0)]
    [InlineData("waittext", 0)]
    [InlineData("wait soon", 0)]
    [InlineData("wait 3601", 0)]
    [InlineData("waittext 0.1", 0)]
    [InlineData("waittext 0.1 x 3", 30.1)]
    public void AWaitCommandBuysItsOwnAnswerDeadline(string line, double expectedSeconds)
    {
        // The app answers a wait no later than its own timeout; a client fixed at a minute
        // would give up on a 61-second wait the app is still honouring, and refuse a 3600. So
        // the two wait commands buy timeout plus slack — from the first token only, because
        // everything after it is query text and never a number. Everything else, including a
        // wait the app will reject immediately, keeps the default: the answer is instant.
        TimeSpan? timeout = Cli.AnswerTimeoutFor(line);
        if (expectedSeconds == 0)
        {
            Assert.Null(timeout);
            return;
        }

        Assert.NotNull(timeout);
        Assert.Equal(expectedSeconds, timeout!.Value.TotalSeconds, 6);
    }

    [Fact]
    public void TheToolWaitsForReadinessBeforeItLetsTheCommandThrough()
    {
        if (!OperatingSystem.IsWindows()) return;

        // The whole chain, not the parser: build a real app, start it headless, pass the gate,
        // then run the command. The probe draws "loading" until its third frame, so a png that
        // exists afterwards is a png that was taken after readiness, not merely one that parsed.
        string project = Path.Combine(TestPaths.RepositoryRoot, "src", "CuiSharp.Tests", "TestApps", "ReadyProbe");
        string png = Path.Combine(Path.GetTempPath(), $"cui-ready-{Guid.NewGuid():N}.png");
        string late = Path.Combine(Path.GetTempPath(), $"cui-late-{Guid.NewGuid():N}.png");

        try
        {
            var output = new StringWriter();
            var error = new StringWriter();
            int exit = Cli.Run(
                ["png", png, "--project", project, "--wait", "ready", "--wait-timeout", "20", "--", "--ready-after", "3"],
                output,
                error);

            Assert.True(exit == 0, $"the run should have passed its gate: {error}");
            Assert.Contains(png, output.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(png), "the wait's payload is discarded and the command's own answer is the payload");

            // The order proof: against a probe that never marks ready, the same command must
            // not run — a tool that screenshotted first would exit 0 and lie about the frame.
            // The probe was just built above, so --no-build keeps this check on the readiness
            // gate rather than re-entering MSBuild on the shared project reference.
            var missError = new StringWriter();
            int miss = Cli.Run(
                ["png", late, "--project", project, "--no-build", "--wait", "ready", "--wait-timeout", "1", "--", "--ready-after", "0"],
                new StringWriter(),
                missError);

            Assert.Equal(1, miss);
            Assert.False(File.Exists(late), "an unsatisfied wait must stop the command before it draws anything");
            Assert.Contains("ready", missError.ToString(), StringComparison.Ordinal);

            // A direct `wait` as the command, not the precondition: the answer that comes back
            // is the app's own bounded verdict — server CUI-ERR, not the client giving up — and
            // the miss still exits 1 with the reason.
            var directError = new StringWriter();
            int direct = Cli.Run(
                ["wait", "0.1", "--project", project, "--no-build", "--", "--ready-after", "0"],
                new StringWriter(),
                directError);
            Assert.Equal(1, direct);
            Assert.Contains("No frame marked itself ready within 0.1s", directError.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(png)) File.Delete(png);
            if (File.Exists(late)) File.Delete(late);
        }
    }

    [Fact]
    public void ACommandThatOutlivesTheDefaultDeadlineIsAnsweredWhenItBoughtItsOwn()
    {
        // The server answers a tenth of a second later — past a default deadline narrowed to a
        // thirtieth for the test — and the only difference between an answer and a give-up is
        // the per-command override the wait's grammar buys. The verdict that comes back is the
        // server's own miss, not the client's timeout dressed up as one.
        using var client = HeadlessClient.Attach(
            new DelayedAnswer("CUI-READY 800 600\n", "CUI-ERR 12\nnot within 3\n"),
            new MemoryStream(),
            answerTimeout: TimeSpan.FromMilliseconds(30));

        Answer answer = client.Send("wait 3", answerTimeout: TimeSpan.FromSeconds(3));

        Assert.False(answer.Ok);
        Assert.Equal("not within 3", answer.Payload);
    }

    [Fact]
    public void ATimeoutNamesTheDeadlineThatActuallyRanOut()
    {
        // The silent app: the message has to say which deadline expired. The old text quoted
        // the default minute even when the command had bought three quarters of a second, so a
        // reader went looking for a minute-long hang that never existed.
        using var client = HeadlessClient.Attach(
            new DelayedAnswer("CUI-READY 800 600\n"),
            new MemoryStream(),
            answerTimeout: TimeSpan.FromMilliseconds(30));

        var error = Assert.Throws<ToolException>(
            () => client.Send("wait 3", answerTimeout: TimeSpan.FromMilliseconds(450)));
        Assert.Contains("did not answer within 0.45s", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("60s", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARelayedWaitBuysItsOwnDeadlineToo()
    {
        // The per-command deadline is not a one-shot-only rule. The same slow server sits
        // behind a `session`: the relayed `wait 3` has to get the override its grammar
        // asks for, or the session dies of the client's default give-up while the app is
        // still answering honestly.
        using var client = HeadlessClient.Attach(
            new DelayedAnswer("CUI-READY 800 600\n", "CUI-ERR 12\nnot within 3\n"),
            new MemoryStream(),
            answerTimeout: TimeSpan.FromMilliseconds(30));
        var output = new StringWriter();
        var error = new StringWriter();

        int exit = Cli.Relay(client, new StringReader("wait 3\n"), output, error);

        // The server's miss ends the session with the server's words; a relay stuck on the
        // default deadline would have said the app did not answer instead.
        Assert.Equal(1, exit);
        Assert.Contains("not within 3", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("did not answer", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AFramePreconditionTheAppRefusesStopsTheRunBeforeAnythingElse()
    {
        // The app refuses the --frames draw with a real framed error. That verdict ends the
        // run: the gate, the command, and a whole relayed session would act on frames the app
        // never drew, and reading the error and carrying on can finish a lie with exit 0. The
        // bytes the client wrote are the proof — after the refusal, nothing else was offered.
        var sent = new MemoryStream();
        using var client = HeadlessClient.Attach(
            new DelayedAnswer("CUI-READY 800 600\n", "CUI-ERR 13\nthe app threw\n"),
            sent);
        var error = new StringWriter();
        Cli.Options options = Grammar.Parse(["tree", "--frames", "2", "--wait", "ready"]).Options!;

        Assert.False(Cli.PassPreconditions(client, options, error));

        Assert.Contains("the app threw", error.ToString(), StringComparison.Ordinal);
        Assert.Equal("frame 2\n", Encoding.UTF8.GetString(sent.ToArray()));
    }

    /// <summary>
    /// A server stream that answers on a delay: the banner arrives after the same tenth of a
    /// second as every later block — long enough for a default deadline narrowed for the test
    /// to run out, short enough that no test waits on it.
    /// </summary>
    private sealed class DelayedAnswer(params string[] blocks) : Stream
    {
        private readonly Queue<byte[]> _blocks = new(blocks.Select(Encoding.UTF8.GetBytes));

        private byte[] _current = [];

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        // The client reads only through ReadAsync, and that is the one path whose waits the
        // caller's deadline can end. A synchronous read past the last block would park on a
        // delay no token can cancel — so here it is a loud failure instead of a silent hang.
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_current.Length == 0)
            {
                if (_blocks.Count == 0)
                {
                    // A live app with nothing more to say: only the caller's deadline ends this.
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                    return 0;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                _current = _blocks.Dequeue();
            }

            int take = Math.Min(buffer.Length, _current.Length);
            _current.AsSpan(0, take).CopyTo(buffer.Span);
            _current = _current[take..];
            return take;
        }
    }

    [Fact]
    public void TheFixtureCarriesItsNativePayloadIntoAnyWorkingDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;

        // The probe must run wherever the tool happens to stand. A cui.dll inherited from the
        // launcher's current directory lets an in-process run pass while the same app fails on
        // a user's machine, so the tool runs here as a real process from a scratch directory
        // that has no DLL of any kind to borrow — the process's own cwd is never touched, which
        // parallel tests would share.
        // The tool that runs is the one for the configuration under test: the test assembly
        // lives in bin\<Configuration>\net10.0, and the tool the same build produced for that
        // configuration answers for the code under review — a stale Release build can no
        // longer review Debug changes, and a clean Debug run finds its own binary.
        string tool = ToolUnderTest(ConfigurationUnderTest());
        string project = Path.Combine(TestPaths.RepositoryRoot, "src", "CuiSharp.Tests", "TestApps", "ReadyProbe");
        string scratch = Directory.CreateTempSubdirectory("cui-scratch-").FullName;

        try
        {
            Assert.Empty(Directory.GetFiles(scratch, "*.dll"));

            string png = Path.Combine(scratch, "ready.png");
            (int exit, _, string errors) = RunFrom(
                scratch, tool,
                ["png", png, "--project", project, "--wait", "ready", "--wait-timeout", "20", "--", "--ready-after", "3"]);
            Assert.True(exit == 0, $"a ready-marking probe should pass its gate from an empty cwd: {errors}");
            Assert.True(File.Exists(png), "the PNG is the proof the app ran on its own native payload");

            string late = Path.Combine(scratch, "late.png");
            (int miss, _, string missErrors) = RunFrom(
                scratch, tool,
                ["png", late, "--project", project, "--no-build", "--wait", "ready", "--wait-timeout", "1", "--", "--ready-after", "0"]);
            Assert.Equal(1, miss);
            Assert.Contains("ready", missErrors, StringComparison.Ordinal);
            Assert.False(File.Exists(late), "an unsatisfied gate must still stop the command from a foreign cwd");
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// The configuration the tests are running from: the directory segment after <c>bin</c> in
    /// this assembly's own path is the only answer that cannot review a stale build.
    /// </summary>
    private static string ConfigurationUnderTest()
    {
        string? directory = Path.GetDirectoryName(typeof(ToolArgumentTests).Assembly.Location);
        ArgumentException.ThrowIfNullOrEmpty(directory);
        string[] parts = directory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].Equals("bin", StringComparison.OrdinalIgnoreCase)) return parts[i + 1];
        }

        throw new InvalidOperationException($"{directory} does not live under a bin{Path.DirectorySeparatorChar}<configuration> directory");
    }

    /// <summary>
    /// The tool for the configuration under test. <c>dotnet test</c> has already built the
    /// ProjectReference in this configuration, so the test only selects and asserts that
    /// output — a nested <c>dotnet build</c> from inside the testhost re-enters MSBuild on
    /// outputs the running build owns, which is how a fixture turns into a hang.
    /// </summary>
    private static string ToolUnderTest(string configuration)
    {
        string tool = Path.Combine(
            TestPaths.RepositoryRoot, "src", "CuiSharp.Tools", "bin", configuration, "net10.0", "CuiSharp.Tools.dll");
        Assert.True(
            File.Exists(tool),
            $"{tool} is missing: build the solution in {configuration} (dotnet test does, through the ProjectReference)");
        return tool;
    }

    /// <summary>
    /// Runs the real tool as a child with an explicit working directory, so the fixture is
    /// judged where it will actually be launched rather than where the test host happens to
    /// be. Both outputs are drained at once: a tool that fails loudly fills the pipe nobody is
    /// reading and deadlocks the reader that waits on the other one first.
    /// </summary>
    private static (int Exit, string Out, string Error) RunFrom(string workingDirectory, string toolDll, string[] arguments) =>
        Run(workingDirectory, "dotnet", [toolDll, .. arguments]);

    private static (int Exit, string Out, string Error) Run(string workingDirectory, string host, string[] arguments)
    {
        var info = new ProcessStartInfo(host)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);

        using Process process = Process.Start(info) ?? throw new InvalidOperationException("could not start the tool");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(3)))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("the tool did not answer within three minutes");
        }

        return (process.ExitCode, output.GetAwaiter().GetResult(), errors.GetAwaiter().GetResult());
    }

    [Fact]
    public void OnlyAFramedAnswerLooksLikeOne()
    {
        // The app may print whatever it likes, so the reader has to recognize its own framing
        // and skip everything else: a line is a header only when it says OK/ERR and a length.
        Assert.True(HeadlessClient.IsHeader("CUI-OK 12"));
        Assert.True(HeadlessClient.IsHeader("CUI-ERR 0"));
        Assert.False(HeadlessClient.IsHeader("CUI-OK"));
        Assert.False(HeadlessClient.IsHeader("CUI-OK twelve"));
        Assert.False(HeadlessClient.IsHeader("frame at click 0"));
        Assert.False(HeadlessClient.IsHeader(""));
    }
}
