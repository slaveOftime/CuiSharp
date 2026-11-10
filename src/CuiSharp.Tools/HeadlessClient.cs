using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CuiSharp.Tools;

/// <summary>
/// One running app, spoken to over the headless protocol: a ready banner, then one framed answer
/// per command.
/// </summary>
/// <remarks>
/// <para>
/// Everything is read as bytes off the child's stdout. A <see cref="StreamReader"/> would read
/// ahead and swallow the beginning of the next answer, and the payload length in the header is
/// in bytes, so bytes are the honest unit here.
/// </para>
/// <para>
/// The protocol is self-synchronizing: a line that does not look like a header is not an error,
/// it is the app talking. An app may print before it starts the loop — a startup banner, a log
/// line from a library — and holding that against it would mean a tool that works only for apps
/// polite enough not to log. Such lines are passed to <see cref="Noise"/> and skipped.
/// </para>
/// <para>
/// Every read is bounded. A child that never writes a newline, announces a gigabyte payload or
/// logs without end cannot make the tool allocate without limit: the line cap truncates what it
/// relays, the payload cap refuses an answer it cannot carry, and retained stderr keeps only its
/// tail.
/// </para>
/// </remarks>
internal sealed class HeadlessClient : IDisposable
{
    /// <summary>The most one answer may claim. Past this the app is not speaking this protocol.</summary>
    internal const int MaxPayloadBytes = 16 * 1024 * 1024;

    /// <summary>How much of one line the tool keeps. A header is 32 bytes; app output may be a log.</summary>
    internal const int MaxLineBytes = 4096;

    private readonly Process? _process;
    private readonly Stream _out;
    private readonly Stream _in;
    private readonly TailBuffer _errors = new();

    private HeadlessClient(Process? process, Stream output, Stream input, Action<string>? echo)
    {
        Noise = echo;
        _process = process;
        _out = output;
        _in = input;
        if (process is null) return;

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            _errors.Append(e.Data);
            // The app's stderr is the app's diagnostics: a person running the tool sees them the
            // way they would see them from a shell, and the protocol on stdout stays clean.
            Noise?.Invoke(e.Data);
        };
        process.BeginErrorReadLine();
    }

    /// <summary>Anything the app wrote to stderr: its exception, or its own logging.</summary>
    internal string Errors => _errors.ToString();

    /// <summary>
    /// A line the app wrote to stdout that was not protocol, relayed here so it is not lost — it
    /// is the app's own output, and the only place it can go is where stderr already goes.
    /// </summary>
    internal Action<string>? Noise { get; }

    /// <summary>How long to wait for the app to say it is ready.</summary>
    internal TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long one command may take. A frame is milliseconds, so a minute means the app is
    /// waiting for something that will never come — a nested message loop, a dialog, a deadlock —
    /// and a tool that hangs is worse than a tool that says so.
    /// </summary>
    internal TimeSpan AnswerTimeout { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Starts an app in headless mode and waits for it to report its size.</summary>
    /// <param name="host">The executable or <c>dotnet</c>.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <param name="echo">Where the app's own output goes, or null to discard it.</param>
    /// <param name="cancellation">Cancels the wait for the app's banner.</param>
    internal static HeadlessClient Start(
        string host,
        IReadOnlyList<string> arguments,
        Action<string>? echo = null,
        CancellationToken cancellation = default)
    {
        var info = new ProcessStartInfo(host)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        info.Environment[CuiSharpEnvironmentVariable] = "1";

        Process process = Process.Start(info) ?? throw new ToolException($"Could not start {host}.");
        var client = new HeadlessClient(
            process,
            process.StandardOutput.BaseStream,
            process.StandardInput.BaseStream,
            echo);
        try
        {
            client.WaitReady(cancellation);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A client over streams instead of a child process: what the protocol's own tests speak, and
    /// what an embedder driving a channel of its own can use.
    /// </summary>
    internal static HeadlessClient Attach(
        Stream output,
        Stream input,
        Action<string>? echo = null,
        TimeSpan? readyTimeout = null,
        TimeSpan? answerTimeout = null,
        CancellationToken cancellation = default)
    {
        var client = new HeadlessClient(null, output, input, echo)
        {
            ReadyTimeout = readyTimeout ?? TimeSpan.FromSeconds(60),
            AnswerTimeout = answerTimeout ?? TimeSpan.FromMinutes(1),
        };
        client.WaitReady(cancellation);
        return client;
    }

    private const string CuiSharpEnvironmentVariable = "CUISHARP_HEADLESS";

    private void WaitReady(CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(ReadyTimeout);
        var skipped = new StringBuilder();
        while (true)
        {
            string line = ReadLine(timeout.Token, Waiting.Ready);
            if (line.StartsWith("CUI-READY ", StringComparison.Ordinal))
            {
                string[] parts = line.Split(' ');
                Size = parts.Length >= 3 ? $"{parts[1]}x{parts[2]}" : "?";
                return;
            }

            // The app had not reached the loop yet, so this is its own output.
            skipped.AppendLine(line);
            Relay(line);
            if (skipped.Length > MaxLineBytes)
            {
                throw new ToolException(
                    $"The app never started in headless mode, and this is what it said:{Environment.NewLine}{skipped}");
            }
        }
    }

    private void Relay(string line) => Noise?.Invoke(line);

    /// <summary>The frame size the app reported, e.g. <c>1080x720</c>.</summary>
    internal string Size { get; private set; } = "?";

    /// <summary>Sends one command and reads its answer.</summary>
    internal Answer Send(string command, CancellationToken cancellation = default)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(command + "\n");
        _in.Write(bytes);
        _in.Flush();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(AnswerTimeout);
        string header = ReadHeader(timeout.Token);
        bool ok = TryParseHeader(header, out int length);
        string payload = ReadExactly(length, timeout.Token);
        // The answer ends with a newline so a person reading the raw stream sees one answer per
        // block; consume it or the next header would look empty.
        ReadLine(timeout.Token, Waiting.Answer);
        return new Answer(ok, payload);
    }

    /// <summary>
    /// Reads until a line looks like a header, skipping anything else the app printed.
    /// </summary>
    private string ReadHeader(CancellationToken token)
    {
        for (string line = ReadLine(token, Waiting.Answer); ; line = ReadLine(token, Waiting.Answer))
        {
            if (IsHeader(line)) return line;
            Relay(line);
        }
    }

    internal static bool IsHeader(string line) => TryParseHeader(line, out _);

    /// <summary>
    /// Whether <paramref name="line"/> is a header, and what it promises. A header that names more
    /// bytes than the protocol can carry is an error rather than a header: the alternative is
    /// allocating whatever a broken child asked for.
    /// </summary>
    internal static bool TryParseHeader(string line, out int length) => TryParseHeader(line, out _, out length);

    internal static bool TryParseHeader(string line, out bool ok, out int length)
    {
        ok = false;
        length = 0;
        int space = line.IndexOf(' ', StringComparison.Ordinal);
        if (space < 0) return false;

        ReadOnlySpan<char> status = line.AsSpan(0, space);
        bool success = status.SequenceEqual("CUI-OK");
        if (!success && !status.SequenceEqual("CUI-ERR")) return false;

        // Not a number at all: not protocol. A number too large to carry: a broken app.
        if (!long.TryParse(line.AsSpan(space + 1), NumberStyles.None, CultureInfo.InvariantCulture, out long claimed))
            return false;
        if (claimed > MaxPayloadBytes)
        {
            throw new ToolException(
                $"The app announced a {claimed}-byte answer; this tool carries at most {MaxPayloadBytes} bytes.");
        }

        ok = success;
        length = (int)claimed;
        return true;
    }

    /// <summary>Why the tool is reading, so a timeout says what actually did not happen.</summary>
    private enum Waiting
    {
        Ready,
        Answer,
    }

    private string ReadLine(CancellationToken token, Waiting waiting)
    {
        var line = new List<byte>(128);
        var one = new byte[1];
        bool overflowed = false;
        while (true)
        {
            // ReadAsync, not Read: the timeout has to interrupt a child that never writes, and a
            // synchronous read of a pipe blocks in the kernel, where no check can happen.
            int read;
            try
            {
                read = _out.ReadAsync(one.AsMemory(0, 1), token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                throw Timeout(waiting);
            }

            if (read == 0) throw new ToolException($"The app closed its output.{Tail()}");
            if (one[0] == (byte)'\n')
            {
                string text = Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r');

                // A header cut short would be relayed as noise and the payload behind it read as
                // headers: say so instead of desynchronizing.
                if (overflowed && (text.StartsWith("CUI-OK", StringComparison.Ordinal) ||
                                   text.StartsWith("CUI-ERR", StringComparison.Ordinal)))
                {
                    throw new ToolException($"The app sent a header longer than {MaxLineBytes} bytes.{Tail()}");
                }

                return text;
            }

            // A line the tool will only relay is cut, not accumulated: an app that logs without a
            // newline must not be able to grow the tool's memory.
            if (line.Count < MaxLineBytes) line.Add(one[0]);
            else overflowed = true;
        }
    }

    private ToolException Timeout(Waiting waiting) => waiting switch
    {
        Waiting.Ready => new ToolException(
            $"The app was not ready after {ReadyTimeout.TotalSeconds:0}s: it never printed CUI-READY.{Tail()}"),
        _ => new ToolException(
            $"The app did not answer within {AnswerTimeout.TotalSeconds:0}s.{Tail()}"),
    };

    private string ReadExactly(int length, CancellationToken token)
    {
        var payload = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int read;
            try
            {
                read = _out.ReadAsync(payload.AsMemory(offset, length - offset), token)
                    .AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                throw Timeout(Waiting.Answer);
            }

            if (read == 0) throw new ToolException($"The app closed its output mid-answer.{Tail()}");
            offset += read;
        }

        return Encoding.UTF8.GetString(payload);
    }

    /// <summary>Whatever the app said on stderr, indented, for a failure message.</summary>
    private string Tail()
    {
        string errors = Errors.Trim();
        return errors.Length == 0 ? "" : "\n" + errors;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_process is not { } process)
        {
            _out.Dispose();
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                try
                {
                    byte[] quit = Encoding.UTF8.GetBytes("quit\n");
                    _in.Write(quit);
                    _in.Flush();
                }
                catch (IOException)
                {
                    // It is already gone; nothing to ask it nicely.
                }

                if (!process.WaitForExit(TimeSpan.FromSeconds(5))) process.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            process.Dispose();
        }
    }
}

/// <summary>
/// The last few kilobytes of what an app printed. A tool that keeps every line a chatty app ever
/// wrote holds a session's memory hostage to its logging.
/// </summary>
internal sealed class TailBuffer
{
    private readonly int _capacity;
    private readonly Queue<string> _lines = new();
    private readonly Lock _gate = new();
    private int _bytes;
    private bool _dropped;

    internal TailBuffer(int capacity = 64 * 1024) => _capacity = capacity;

    internal void Append(string line)
    {
        lock (_gate)
        {
            if (line.Length > _capacity)
            {
                line = line[.._capacity];
                _dropped = true;
            }

            while (_bytes + line.Length > _capacity && _lines.Count > 0)
            {
                _bytes -= _lines.Dequeue().Length;
                _dropped = true;
            }

            _lines.Enqueue(line);
            _bytes += line.Length;
        }
    }

    internal int Length
    {
        get { lock (_gate) return _bytes; }
    }

    public override string ToString()
    {
        lock (_gate)
        {
            return _dropped
                ? "[earlier output dropped]\n" + string.Join('\n', _lines)
                : string.Join('\n', _lines);
        }
    }
}

/// <summary>One answer: whether the command was obeyed, and its payload.</summary>
internal readonly record struct Answer(bool Ok, string Payload);

/// <summary>Something the tool reports instead of a stack trace.</summary>
internal sealed class ToolException(string message) : Exception(message);
