using System.Text;
using CuiSharp.Hosting;
using CuiSharp.Tools;

namespace CuiSharp.Tests;

/// <summary>
/// The headless protocol's edges: a child that misbehaves, a line that never ends, a timeout that
/// has to say which wait it gave up on. Each of these used to be an unbounded read, a crash or a
/// message pointing at the wrong phase.
/// </summary>
public class ProtocolBoundsTests
{
    [Fact]
    public void AHeaderThatClaimsMoreBytesThanTheProtocolCarriesIsRefused()
    {
        // The old parser took the announced length on trust and allocated it.
        var tooBig = $"CUI-OK {HeadlessClient.MaxPayloadBytes + 1}";

        var error = Assert.Throws<ToolException>(() => HeadlessClient.TryParseHeader(tooBig, out _, out _));
        Assert.Contains("bytes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLongestCarryableAnswerIsStillAHeader()
    {
        Assert.True(HeadlessClient.TryParseHeader($"CUI-OK {HeadlessClient.MaxPayloadBytes}", out bool ok, out int length));
        Assert.True(ok);
        Assert.Equal(HeadlessClient.MaxPayloadBytes, length);

        // Anything that is not a number is the app talking, not protocol, which is not an error.
        Assert.False(HeadlessClient.TryParseHeader("CUI-OK later", out _, out _));
        Assert.False(HeadlessClient.TryParseHeader("Starting up…", out _, out _));

        // A refusal is a header too, and it says so: false, not merely "parsed".
        Assert.True(HeadlessClient.TryParseHeader("CUI-ERR 0", out bool refused, out _));
        Assert.False(refused);
    }

    [Fact]
    public void ALineWithNoNewlineIsCutInsteadOfAccumulated()
    {
        // A logger that forgets its newline must not become the tool's memory.
        var stream = new Feed(
            Encoding.UTF8.GetBytes("CUI-READY 800 600\n"),
            new byte[HeadlessClient.MaxLineBytes * 4],
            Encoding.UTF8.GetBytes("\nCUI-OK 2\nok\n"));

        var relayed = new List<string>();
        using var client = HeadlessClient.Attach(stream, new MemoryStream(), relayed.Add);

        Answer answer = client.Send("tree");

        Assert.True(answer.Ok);
        Assert.Equal("ok", answer.Payload);
        string line = Assert.Single(relayed);
        Assert.Equal(HeadlessClient.MaxLineBytes, line.Length);
    }

    [Fact]
    public void ARefusedCommandIsAnAnswerThatSaysSoNotASuccess()
    {
        // The regression: Send took the "is this a header" answer as the status, so every CUI-ERR
        // became Ok with the error text as its payload — the tool printed it to stdout and exited
        // 0, which a script cannot tell from a good run.
        var stream = new Feed(
            Encoding.UTF8.GetBytes("CUI-READY 800 600" + (char)10),
            Encoding.UTF8.GetBytes("CUI-ERR 21" + (char)10 + "Unknown command \"dance\"" + (char)10));

        using var client = HeadlessClient.Attach(stream, new MemoryStream());
        Answer answer = client.Send("dance");

        Assert.False(answer.Ok);
        Assert.Contains("Unknown command", answer.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAppThatIsAllNoiseBeforeItsBannerIsGivenUpOn()
    {
        var stream = new Feed(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("noise, no banner " + (char)10, 1000))));

        var error = Assert.Throws<ToolException>(() => HeadlessClient.Attach(stream, new MemoryStream()));
        Assert.Contains("never started in headless mode", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACommandTimeoutSaysTheAnswerIsWhatDidNotArrive()
    {
        // The regression: every read reported the readiness message, so a command that hung after
        // the app was ready looked like an app that never started.
        var stream = new Feed(Encoding.UTF8.GetBytes("CUI-READY 800 600" + (char)10));
        using var client = HeadlessClient.Attach(
            stream, new MemoryStream(), answerTimeout: TimeSpan.FromMilliseconds(150));

        var error = Assert.Throws<ToolException>(() => client.Send("tree"));
        Assert.Contains("did not answer", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not ready", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CancellingACommandStopsTheWait()
    {
        var stream = new Feed(Encoding.UTF8.GetBytes("CUI-READY 800 600\n"));
        using var client = HeadlessClient.Attach(stream, new MemoryStream());
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var error = Assert.Throws<ToolException>(() => client.Send("tree", cancel.Token));
        Assert.Contains("did not answer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RetainedStderrKeepsOnlyItsTail()
    {
        var tail = new TailBuffer(capacity: 4096);
        for (var i = 0; i < 100; i++) tail.Append(new string('x', 100) + i);

        Assert.True(tail.Length <= 4096);
        Assert.StartsWith("[earlier output dropped]", tail.ToString(), StringComparison.Ordinal);
        Assert.Contains("99", tail.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ACommandWithoutANewlineIsRefusedRatherThanRead()
    {
        var source = new StringReader(new string('t', HeadlessLoop.MaxCommandChars + 10));

        var error = Assert.Throws<ArgumentException>(() => HeadlessLoop.ReadCommand(source));
        Assert.Contains("newline", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAnswerPastTheProtocolLimitIsAnErrorThatSaysSo()
    {
        var sink = new StringWriter();

        HeadlessLoop.Answer(sink, ok: true, "a tree that is far too large for this pipe", limit: 16);

        string wire = sink.ToString().ReplaceLineEndings("\n");
        Assert.StartsWith("CUI-ERR ", wire, StringComparison.Ordinal);
        Assert.Contains("past this protocol's 16-byte limit", wire, StringComparison.Ordinal);
    }

    /// <summary>One stream the app would have written: some bytes, then nothing at all.</summary>
    private sealed class Feed(params byte[][] chunks) : Stream
    {
        private readonly Queue<byte[]> _chunks = new(chunks);
        private byte[] _current = [];

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_current.Length == 0)
            {
                if (_chunks.Count == 0)
                {
                    // A live pipe with nothing to say: the wait has to be interruptible, and the
                    // only thing that ends it is the caller's timeout.
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                    return 0;
                }

                _current = _chunks.Dequeue();
            }

            int take = Math.Min(buffer.Length, _current.Length);
            _current.AsSpan(0, take).CopyTo(buffer.Span);
            _current = _current[take..];
            return take;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
