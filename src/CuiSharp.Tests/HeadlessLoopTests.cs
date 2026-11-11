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

    private static string Wire(HeadlessApp app, string command)
    {
        var output = new StringWriter();
        HeadlessLoop.Run(app, new StringReader(command + "\nquit\n"), output);
        string wire = output.ToString();
        string[] lines = wire.ReplaceLineEndings("\n").Split('\n');
        int length = int.Parse(lines[1]["CUI-OK ".Length..], System.Globalization.CultureInfo.InvariantCulture);
        return string.Join('\n', lines[2..])[..length];
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
        Assert.Contains("needs three numbers", Wire(app, "wheel 200"), StringComparison.Ordinal);
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
        Assert.Contains("Unknown key", Wire(app, "key NoSuchKey"), StringComparison.Ordinal);
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
