using System.Globalization;
using System.Text;
using CuiSharp.Hosting;
using CuiSharp.Testing;

namespace CuiSharp.Tests;

/// <summary>
/// The protocol a tool drives an app with. These tests speak it the way a client does — write a
/// line, read a framed answer — so the wire format is what is under test, not an internal call.
/// </summary>
public class HeadlessLoopTests
{
    private sealed class Counter : Component
    {
        public int Clicks;
        public string Name = "";

        protected override void Draw(Ui ui)
        {
            using (ui.Column(new() { Width = Size.Fill, Pad = 16, Gap = 8 }))
            {
                ui.Label($"clicks {Clicks}");
                if (ui.Button("Add")) Clicks++;
                ui.Input("name", ref Name, new InputOpts { AutoFocus = true });
            }
        }
    }

    /// <summary>
    /// Speaks the wire the way the client does: the banner and the header are lines, but the
    /// payload is exactly the announced number of <em>bytes</em>, sliced as bytes and decoded
    /// after — never characters sliced by a byte count, which would cut a unicode answer in
    /// half and eat the next answer's header. An answer that is not CUI-OK fails the premise
    /// here, so a positive test can never be satisfied by an error message that happens to
    /// repeat the words it is looking for.
    /// </summary>
    private static string Wire(HeadlessApp app, string command) => Framed(app, command, "CUI-OK");

    /// <summary>
    /// The same framing read against a command that must fail: the header has to say CUI-ERR,
    /// the announced byte count has to survive decoding, and the block has to end on its
    /// newline. Diagnostic prose is an answer only when it is framed as one.
    /// </summary>
    private static string ErrorWire(HeadlessApp app, string command) => Framed(app, command, "CUI-ERR");

    private static string Framed(HeadlessApp app, string command, string expectedStatus)
    {
        var buffer = new MemoryStream();
        using (var sink = new StreamWriter(buffer, new UTF8Encoding(false), leaveOpen: true))
            HeadlessLoop.Run(app, new StringReader(command + "\nquit\n"), sink);
        byte[] wire = buffer.ToArray();

        int offset = ReadLine(wire, 0, out string banner);
        if (!banner.StartsWith("CUI-READY ", StringComparison.Ordinal))
            throw new InvalidOperationException($"the loop never announced itself: {banner}");

        offset = ReadLine(wire, offset, out string header);
        int space = header.IndexOf(' ', StringComparison.Ordinal);
        if (space < 0) throw new InvalidOperationException($"not an answer header: {header}");
        int length = int.Parse(header[(space + 1)..], CultureInfo.InvariantCulture);
        string payload = Read(wire, offset, length);

        if (!header.StartsWith(expectedStatus + " ", StringComparison.Ordinal))
            throw new InvalidOperationException($"\"{command}\" answered {header}: {payload}");
        if (Encoding.UTF8.GetByteCount(payload) != length)
            throw new InvalidOperationException($"the {length}-byte answer did not survive decoding whole");
        if (wire[offset + length] != (byte)'\n')
            throw new InvalidOperationException($"the answer did not end its block: {header}");

        return payload;
    }

    private static string Read(byte[] wire, int offset, int length) => Encoding.UTF8.GetString(wire, offset, length);

    private static int ReadLine(byte[] wire, int start, out string line)
    {
        int end = Array.IndexOf(wire, (byte)'\n', start);
        if (end < 0) throw new InvalidOperationException("the wire ended in the middle of a line");
        line = Read(wire, start, end - start);
        return end + 1;
    }

    [Fact]
    public void TheLoopAnnouncesItselfBeforeItReadsAnything()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = TestApp.Start(new Counter(), 400, 300);
        var output = new StringWriter();

        // A client may not send until it has heard this, so a slow startup is never a timeout.
        HeadlessLoop.Run(app, new StringReader("quit\n"), output);

        Assert.StartsWith("CUI-READY 400 300\n", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TreeDescribesTheFrameAndClickAnswersWithTheNextOne()
    {
        if (!OperatingSystem.IsWindows()) return;

        var page = new Counter();
        using var app = TestApp.Start(page, 400, 300);

        Assert.Equal("Frame\n  Label \"clicks 0\"\n  Button \"Add\"\n  Input \"name\"", Wire(app, "tree"));

        // A click on "Add" answers with the frame that reacted to it, so a client needs one
        // round trip per action rather than two.
        Assert.Contains("Label \"clicks 1\"", Wire(app, "click Add"), StringComparison.Ordinal);
        Assert.Equal(1, page.Clicks);
    }

    [Fact]
    public void TypingGoesToTheFocusedField()
    {
        if (!OperatingSystem.IsWindows()) return;

        var page = new Counter();
        using var app = TestApp.Start(page, 400, 300);

        Wire(app, "focus");
        Wire(app, "type Milk");

        Assert.Equal("Milk", page.Name);
        // The answer to a command is the frame it produced.
        Assert.Contains("Input \"name\"", Wire(app, "tree"), StringComparison.Ordinal);
    }

    [Fact]
    public void PngWritesTheFramesPixels()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = TestApp.Start(new Counter(), 400, 300);
        string path = Path.Combine(Path.GetTempPath(), $"cui-loop-{Guid.NewGuid():N}.png");

        Assert.Equal(path, Wire(app, "png " + path));
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], bytes[..4]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ACommandThatCannotBeObeyedAnswersWithWhy()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = TestApp.Start(new Counter(), 400, 300);
        var output = new StringWriter();

        HeadlessLoop.Run(app, new StringReader("click Nope\nquit\n"), output);
        string answer = output.ToString().ReplaceLineEndings("\n");

        Assert.StartsWith("CUI-READY 400 300\nCUI-ERR ", answer, StringComparison.Ordinal);
        // The message says what the frame did draw, so the client can retry without guessing.
        Assert.Contains("Button", answer, StringComparison.Ordinal);
        Assert.Contains("Add", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void NonsenseIsRejectedWithTheCommandList()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = TestApp.Start(new Counter(), 400, 300);
        var output = new StringWriter();

        HeadlessLoop.Run(app, new StringReader("dance\nquit\n"), output);
        string answer = output.ToString().ReplaceLineEndings("\n");

        Assert.Contains("Unknown command", answer, StringComparison.Ordinal);
        Assert.Contains("tree", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void WheelIsACommandAndAnswersWithTheFrameItProduced()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = TestApp.Start(new Counter(), 400, 300);

        // Nothing here scrolls, but the command must still dispatch and answer with a frame;
        // the scrolling itself is proven in TestingHarnessTests.
        string answer = Wire(app, "wheel 200 150 -4");

        Assert.DoesNotContain("Unknown command", answer, StringComparison.Ordinal);
        Assert.Contains("Button \"Add\"", answer, StringComparison.Ordinal);
        Assert.Contains("needs three numbers", ErrorWire(app, "wheel 200"), StringComparison.Ordinal);
    }

    [Fact]
    public void PulseDrawsFramesAndCarriesTheHeapInsteadOfTheTree()
    {
        if (!OperatingSystem.IsWindows()) return;

        var page = new Counter();
        using var app = TestApp.Start(page, 400, 300);

        // The whole point of pulse: the frames are drawn, but no tree is serialised.
        string answer = Wire(app, "pulse 7");

        Assert.StartsWith("7 heap ", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("Button", answer, StringComparison.Ordinal);
        Assert.True(long.TryParse(answer["7 heap ".Length..], out long heap) && heap > 0);

        // The frames were real frames: the app drew them and its clock moved.
        Assert.Contains("clicks 0", Wire(app, "tree"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pulse 0")]
    [InlineData("pulse -3")]
    [InlineData("frame 0")]
    [InlineData("frame 99999999999")]
    public void AFrameCountThatCannotMeanAnythingIsAnErrorNotASilentNop(string command)
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = TestApp.Start(new Counter(), 400, 300);
        var output = new StringWriter();

        HeadlessLoop.Run(app, new StringReader(command + "\nquit\n"), output);
        string answer = output.ToString().ReplaceLineEndings("\n");

        // A negative count used to answer "-3 heap ..." and a number too large for an int took
        // the whole session down with an unhandled OverflowException.
        Assert.Contains("CUI-ERR", answer, StringComparison.Ordinal);
        Assert.Contains("CUI-OK 0\n", answer, StringComparison.Ordinal);   // the quit still answered
    }

    /// <summary>A page whose heading and button both say "Save", so a click has to choose.</summary>
    private sealed class Ambiguous : Component
    {
        public int Saves;

        protected override void Draw(Ui ui)
        {
            ui.Heading("Save");
            if (ui.Button("Save")) Saves++;
        }
    }

    [Fact]
    public void ClickOnAWordLandsOnTheControlNotTheHeading()
    {
        if (!OperatingSystem.IsWindows()) return;

        var page = new Ambiguous();
        using var app = TestApp.Start(page, 400, 300);

        // `click Save` has two candidates and only one of them can be clicked.
        Wire(app, "click Save");
        Assert.Equal(1, page.Saves);

        // Naming the kind is how a caller is specific when the ranking is not enough.
        Assert.StartsWith("Heading", Wire(app, "find Heading \"Save\""), StringComparison.Ordinal);
    }

    /// <summary>A page whose frames mark ready and draw a label only once a flag flips.</summary>
    private sealed class ReadyPage : Component
    {
        public bool Loaded;

        protected override void Draw(Ui ui)
        {
            if (Loaded)
            {
                ui.Label("data 7");
                ui.Label("添加 数据 3");
                ui.MarkReady();
            }
            else
            {
                ui.Label("loading");
            }
        }
    }

    /// <summary>Runs commands and returns the whole wire, headers included.</summary>
    private static string RawWire(HeadlessApp app, string commands)
    {
        var output = new StringWriter();
        HeadlessLoop.Run(app, new StringReader(commands + "\nquit\n"), output);
        return output.ToString().ReplaceLineEndings("\n");
    }

    [Fact]
    public void WaitAnswersWithTheFrameThatBecameReady()
    {
        if (!OperatingSystem.IsWindows()) return;

        var page = new ReadyPage();
        using var app = TestApp.Start(page, 400, 300);

        // The banner says the channel is open; `wait` says the app's data is there, and an app
        // that never marks ready is answered by the clock, not left hanging the session. The
        // miss is a framed CUI-ERR naming its window, and the session keeps serving after it.
        Assert.Contains("within 0.2s", ErrorWire(app, "wait 0.2"), StringComparison.Ordinal);
        Assert.Contains("CUI-OK 0\n", RawWire(app, "wait 0.2"), StringComparison.Ordinal); // quit answered

        page.Loaded = true;
        Assert.Contains("data 7", Wire(app, "wait 2"), StringComparison.Ordinal);
    }

    [Fact]
    public void WaitTextDrawsWithWhateverTheUserTypedAndTimesOutWithWhatWasDrawn()
    {
        if (!OperatingSystem.IsWindows()) return;

        var page = new ReadyPage();
        using var app = TestApp.Start(page, 400, 300);

        // A miss is a framed CUI-ERR saying what was waited for and what the frame really drew,
        // so the client can retry without guessing — the payload sliced by its announced byte
        // count, not prose that merely contains the words.
        string miss = ErrorWire(app, "waittext 0.2 data 8");
        Assert.Contains("data 8", miss, StringComparison.Ordinal);
        Assert.Contains("loading", miss, StringComparison.Ordinal);

        // The miss is a framed answer, not prose that merely contains the words: CUI-ERR, an
        // announced byte count that survives decoding a quoted unicode query, and its delimiter.
        Assert.Contains("不存在的文本", ErrorWire(app, "waittext 0.1 不存在的文本"), StringComparison.Ordinal);

        page.Loaded = true;

        // Spaces, a numeric tail and unicode all stay query text: only the first token after
        // the command is the timeout, and a number inside the text is never parsed as one.
        Assert.Contains("data 7", Wire(app, "waittext 2 data 7"), StringComparison.Ordinal);
        Assert.Contains("data 7", Wire(app, "waittext 2 7"), StringComparison.Ordinal);

        // The exact payload, byte-framed and decoded: the tree with its unicode label is what
        // the answered frame printed, whole — proof the slice was in bytes, not in characters.
        Assert.Equal(
            "Frame\n  Label \"data 7\"\n  Label \"添加 数据 3\"",
            Wire(app, "waittext 2 添加 数据 3"));
    }

    [Theory]
    [InlineData("wait -1", "wait needs")]
    [InlineData("wait 0", "wait needs")]
    [InlineData("wait NaN", "wait needs")]
    [InlineData("wait Infinity", "wait needs")]
    [InlineData("wait 3601", "wait needs")]
    [InlineData("wait soon", "wait needs")]
    [InlineData("waittext", "waittext needs a timeout")]
    [InlineData("waittext 5", "waittext needs a timeout")]
    [InlineData("waittext 99999 data 7", "wait needs")]
    [InlineData("waittext five data 7", "wait needs")]
    public void AWaitWindowThatCannotBoundAWaitIsAnAnswerNotACrash(string command, string refusal)
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = TestApp.Start(new ReadyPage(), 400, 300);

        // The refusal is a framed CUI-ERR carrying its reason as the announced bytes — an error
        // that only looks like prose in the stream is not an answer — and the session is still
        // serving after it, which the quit's own answer in the same wire says out loud.
        Assert.Contains(refusal, ErrorWire(app, command), StringComparison.Ordinal);
        Assert.Contains("CUI-OK 0\n", RawWire(app, command), StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyIsNamedTheWayAPersonWould()
    {
        if (!OperatingSystem.IsWindows()) return;

        var page = new Counter();
        using var app = TestApp.Start(page, 400, 300);
        Wire(app, "focus");

        // Both CUI's spelling and the enum's are accepted, in any case.
        Wire(app, "key Tab");
        Wire(app, "key tab");
        Wire(app, "key PageDown");

        // A key the loop does not know is an answer, not a crash: the client reads why.
        Assert.Contains("Unknown key", ErrorWire(app, "key NoSuchKey"), StringComparison.Ordinal);
    }

    [Fact]
    public void WhatTheAppPrintsCannotLandInTheMiddleOfAFrame()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = TestApp.Start(new Counter(), 400, 300);

        // A loop the caller gave streams to leaves this process's console alone: a test's own
        // output is not the tool's business.
        TextWriter before = Console.Out;
        HeadlessLoop.Run(app, new StringReader("quit\n"), new StringWriter());
        Assert.Same(before, Console.Out);

        // The process path claims stdout for the protocol, so a stray Console.WriteLine in the
        // app — or in a service it resolved — goes to stderr instead of into a frame.
        try
        {
            HeadlessLoop.ClaimStandardOutput();
            Assert.Same(Console.Error, Console.Out);
        }
        finally
        {
            Console.SetOut(before);
        }
    }

    [Fact]
    public void TheLoopIsOffUnlessTheProcessAsksForIt()
    {
        // A windowed run must never quietly become a headless one.
        Environment.SetEnvironmentVariable(HeadlessLoop.EnvironmentVariable, null);
        Assert.False(HeadlessLoop.Requested(["--page=2"]));
        Assert.True(HeadlessLoop.Requested(["--headless"]));

        Environment.SetEnvironmentVariable(HeadlessLoop.EnvironmentVariable, "1");
        try
        {
            Assert.True(HeadlessLoop.Requested([]));
        }
        finally
        {
            Environment.SetEnvironmentVariable(HeadlessLoop.EnvironmentVariable, null);
        }
    }
}
