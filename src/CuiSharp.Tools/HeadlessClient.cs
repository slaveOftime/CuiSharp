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
/// </remarks>
internal sealed class HeadlessClient : IDisposable
{
    private readonly Process _process;
    private readonly Stream _out;
    private readonly Stream _in;
    private readonly StringBuilder _errors = new();

    private HeadlessClient(Process process, Action<string>? echo)
    {
        Noise = echo;
        _process = process;
        _out = process.StandardOutput.BaseStream;
        _in = process.StandardInput.BaseStream;
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            _errors.AppendLine(e.Data);
            // The app's stderr is the app's diagnostics: a person running the tool sees them the
            // way they would see them from a shell, and the protocol on stdout stays clean.
            Noise?.Invoke(e.Data);
        };
        _process.BeginErrorReadLine();
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
    internal static HeadlessClient Start(string host, IReadOnlyList<string> arguments, Action<string>? echo = null)
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
        var client = new HeadlessClient(process, echo);
        try
        {
            client.WaitReady();
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private const string CuiSharpEnvironmentVariable = "CUISHARP_HEADLESS";

    private void WaitReady()
    {
        using var cancel = new CancellationTokenSource(ReadyTimeout);
        var skipped = new StringBuilder();
        for (string line = ReadLine(cancel.Token); ; line = ReadLine(cancel.Token))
        {
            if (line.StartsWith("CUI-READY ", StringComparison.Ordinal))
            {
                string[] parts = line.Split(' ');
                Size = parts.Length >= 3 ? $"{parts[1]}x{parts[2]}" : "?";
                return;
            }

            // The app had not reached the loop yet, so this is its own output.
            skipped.AppendLine(line);
            Relay(line);
            if (skipped.Length > 4096)
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
    internal Answer Send(string command)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(command + "\n");
        _in.Write(bytes);
        _in.Flush();

        using var cancel = new CancellationTokenSource(AnswerTimeout);
        string header = ReadHeader(cancel.Token);
        int space = header.IndexOf(' ', StringComparison.Ordinal);
        string status = header[..space];
        int length = int.Parse(header[(space + 1)..], CultureInfo.InvariantCulture);
        string payload = ReadExactly(length, cancel.Token);
        // The answer ends with a newline so a person reading the raw stream sees one answer per
        // block; consume it or the next header would look empty.
        ReadLine(cancel.Token);
        return new Answer(status == "CUI-OK", payload);
    }

    /// <summary>
    /// Reads until a line looks like a header, skipping anything else the app printed.
    /// </summary>
    private string ReadHeader(CancellationToken token)
    {
        for (string line = ReadLine(token); ; line = ReadLine(token))
        {
            if (IsHeader(line)) return line;
            Relay(line);
        }
    }

    internal static bool IsHeader(string line)
    {
        int space = line.IndexOf(' ', StringComparison.Ordinal);
        if (space < 0) return false;
        if (!line[..space].Equals("CUI-OK", StringComparison.Ordinal) &&
            !line[..space].Equals("CUI-ERR", StringComparison.Ordinal))
        {
            return false;
        }

        return int.TryParse(line.AsSpan(space + 1), NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    private string ReadLine(CancellationToken token)
    {
        var line = new List<byte>(128);
        var one = new byte[1];
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
                throw new ToolException(
                    $"The app was not ready after {ReadyTimeout.TotalSeconds:0}s.{Tail()}");
            }

            if (read == 0) throw new ToolException($"The app closed its output.{$"{Tail()}"}");
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r');
            line.Add(one[0]);
        }
    }

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
                throw new ToolException(
                    $"The app stopped answering after {AnswerTimeout.TotalSeconds:0}s.{Tail()}");
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
        try
        {
            if (!_process.HasExited)
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

                if (!_process.WaitForExit(TimeSpan.FromSeconds(5))) _process.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            _process.Dispose();
        }
    }
}

/// <summary>One answer: whether the command was obeyed, and its payload.</summary>
internal readonly record struct Answer(bool Ok, string Payload);

/// <summary>Something the tool reports instead of a stack trace.</summary>
internal sealed class ToolException(string message) : Exception(message);
