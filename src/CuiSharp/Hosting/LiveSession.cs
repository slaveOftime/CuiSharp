// Hand-written, not generated. Live attach: a tool on the same machine drives the window a human
// is looking at, without taking the loop from CUI. The whole design hangs on one fact: on Windows
// CUI pumps messages inside cui__plat_wait, between presenting a completed frame and beginning the
// next one (native/CUI/src/platform/cui_app_win32.c). A message-only window on that thread gives
// managed code a safe point per frame — after cui_end_frame, before cui__app_begin — and every
// input injection, capture end and boundary screenshot runs there. CUI keeps its frame boundaries;
// CuiSharp never calls cui_begin_frame or cui_end_frame on this path.
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CuiSharp.Interop;

namespace CuiSharp.Hosting;

/// <summary>
/// A driven window: commands arrive from another thread or over a local named pipe, run as
/// multi-frame state machines on the window thread at CUI's own frame boundaries, and answer with
/// the frame that reacted.
/// </summary>
internal sealed class LiveSession : IDisposable
{
    /// <summary>Active session and generation as one published value, so readers never pair one with the other's stale half.</summary>
    private sealed record Published(long Generation, LiveSession? Session);

    private static readonly Lock StaticGate = new();
    private static Published _published = new(0, null);

    /// <summary>The session of the window this process is showing, or null when nothing is driven.</summary>
    internal static LiveSession? Active => Volatile.Read(ref _published).Session;

    /// <summary>Counts publishes (bind and dispose), so a watcher can tell a fresh session from the one it closed.</summary>
    internal static long Generation => Volatile.Read(ref _published).Generation;

    internal const string EnvironmentVariable = "CUISHARP_LIVE";
    internal const string DirectoryEnvironmentVariable = "CUISHARP_LIVE_DIR";
    internal const string Argument = "--live";

    private const uint WmBoundary = Win32.WmApp + 1;
    private const nint TimerId = 0x2C1;
    private const uint TimerMs = 10;
    private const string ClassName = "CuiSharpLiveMessage";
    private const int MaxQueue = 64;
    private const long MaxPixelPayloadBytes = 64L * 1024 * 1024;

    private readonly CuiAppOptions _options;
    private readonly UiFrameState _frameState;
    private readonly FrameRecorder _recorder = new();
    private readonly Lock _gate = new();
    private readonly Queue<Command> _queue = [];
    private readonly List<string> _faults = [];
    private readonly CancellationTokenSource _pipeCts = new();
    private readonly Task _pipeTask;

    private nint _messageHwnd;
    private nint _app;
    private nint _ctx;
    private nint _cuiWindow;
    private bool _bound;
    private bool _reserved;

    private Command? _active;
    private Command? _completing;
    private Command? _cancelCleanup;
    private ulong _armedFrame;
    private ulong _cancelFrame;
    private ulong _steppedFrame;
    private ulong _lastInjectedFrame;
    private bool _armed;
    private int _callbackDepth;
    private bool _injectedPress;
    private bool _timerArmed;
    private volatile bool _closing;
    private bool _disposed;
    private LiveSnapshot? _lastSnapshot;

    /// <summary>The local named pipe this session listens on.</summary>
    internal string PipeName { get; }

    /// <summary>The discovery file written for <c>dotnet cui --attach</c> and <c>dotnet cui ps</c>.</summary>
    internal string DiscoveryFilePath { get; }

    /// <summary>
    /// Runs on the window thread right after a press step is injected. A test uses it to cancel at
    /// the one moment no polling can hit deterministically: after Down, before any later step.
    /// </summary>
    internal Action? AfterPressHookForTests;

    /// <summary>
    /// Runs on the window thread right after a release step is injected, for deterministic
    /// cancel-after-Up tests.
    /// </summary>
    internal Action? AfterReleaseHookForTests;

    internal LiveSession(CuiAppOptions? options = null, UiFrameState? frameState = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Live attach is Windows-only for now.");

        _options = options ?? new CuiAppOptions();
        _frameState = frameState ?? new UiFrameState();

        lock (StaticGate)
        {
            if (_published.Session is not null)
                throw new InvalidOperationException("A live session is already active in this process.");
        }

        if (!CreateMessageWindow())
        {
            throw new IOException(
                $"Live attach was requested and the message window could not be created (Win32 error {Marshal.GetLastPInvokeError()}).");
        }

        string nonce = Guid.NewGuid().ToString("N")[..12];
        PipeName = $"cui-live-{Environment.ProcessId}-{nonce}";
        DiscoveryFilePath = Path.Combine(GetDiscoveryDirectory(), $"{PipeName}.json");
        _reserved = true;
        _pipeTask = Task.Run(() => RunPipeServerAsync(_pipeCts.Token));
    }

    /// <summary>Where live sessions publish their discovery records on this machine.</summary>
    internal static string GetDiscoveryDirectory()
    {
        string? custom = Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable);
        return !string.IsNullOrWhiteSpace(custom)
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cuisharp", "live");
    }

    /// <summary>True when this process was asked to let a tool drive its window.</summary>
    internal static bool Requested(string[]? args = null) =>
        Environment.GetEnvironmentVariable(EnvironmentVariable) is "1" or "true" or "yes" or "on"
        || (args ?? Environment.GetCommandLineArgs()).Contains(Argument, StringComparer.Ordinal);

    /// <summary>
    /// Completes the session with the window that is now drawing. Called from the first draw
    /// callback; the session is only published — only waitable and submittable — once bound.
    /// </summary>
    internal void Bind(nint app, nint ctx, nint windowHandle = 0)
    {
        if (_bound) return;
        _app = app;
        _ctx = ctx;
        _cuiWindow = windowHandle != 0 ? windowHandle : Native.AppNativeWindow(app);
        WidgetRecorder.Attach(_recorder, enabled: true);
        _bound = true;
        WriteDiscoveryFile();
        lock (StaticGate)
        {
            _published = new Published(_published.Generation + 1, this);
        }
    }

    /// <summary>
    /// Waits for the session published after <paramref name="previousGeneration"/> — read before
    /// the window started, so a session left over from the last test is never mistaken for the
    /// fresh one.
    /// </summary>
    internal static LiveSession WaitForActive(long previousGeneration, TimeSpan timeout)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (true)
        {
            Published state = Volatile.Read(ref _published);
            if (state.Generation != previousGeneration && state.Session is { } session) return session;
            if (clock.Elapsed > timeout)
            {
                throw new InvalidOperationException(
                    "No live session started: the app never drew a first frame with live attach within the timeout.");
            }

            Thread.Sleep(5);
        }
    }

    /// <summary>Looks at the current UI: the tree the next recorded frame draws, and its pixels.</summary>
    internal Task<LiveSnapshot> SnapshotAsync(bool pixels = true, CancellationToken cancellation = default) =>
        Submit([new(StepKind.Capture)], pixels, cancellation);

    /// <summary>Moves the pointer and answers with the frame that shows the hover.</summary>
    internal Task<LiveSnapshot> MoveAsync(float x, float y, bool pixels = true, CancellationToken cancellation = default) =>
        Submit([new(StepKind.Move, x, y), new(StepKind.Capture)], pixels, cancellation);

    /// <summary>
    /// Clicks a point: move, press, release, then the frame that reacted — CUI reports the click
    /// on the release frame and a handler's effect is drawn by the one after it.
    /// </summary>
    internal Task<LiveSnapshot> ClickAsync(float x, float y, bool pixels = true, CancellationToken cancellation = default) =>
        Submit(
        [
            new(StepKind.Move, x, y),
            new(StepKind.Down),
            new(StepKind.Up),
            new(StepKind.Capture),
        ], pixels, cancellation);

    /// <summary>Scrolls at a point and answers with the frame that reacted.</summary>
    internal Task<LiveSnapshot> WheelAsync(float x, float y, float dx, float dy, CancellationToken cancellation = default) =>
        Submit([new(StepKind.Move, x, y), new(StepKind.Wheel, dx, dy), new(StepKind.Capture)], pixels: false, cancellation);

    /// <summary>Reports window focus and answers with the resulting frame.</summary>
    internal Task<LiveSnapshot> FocusAsync(CancellationToken cancellation = default) =>
        Submit([new(StepKind.Focus), new(StepKind.Capture)], pixels: false, cancellation);

    /// <summary>Presses and releases a key across two frames, then captures the reacted frame.</summary>
    internal Task<LiveSnapshot> KeyAsync(Key key, CancellationToken cancellation = default) =>
        Submit([new(StepKind.KeyDown, Key: key), new(StepKind.KeyUp, Key: key), new(StepKind.Capture)], pixels: false, cancellation);

    /// <summary>Focuses and types UTF-8 text into the active input, then captures the reacted frame.</summary>
    internal Task<LiveSnapshot> TypeAsync(string text, CancellationToken cancellation = default) =>
        Submit([new(StepKind.Focus), new(StepKind.Text, Text: text), new(StepKind.Capture)], pixels: false, cancellation);

    /// <summary>Advances <paramref name="count"/> frames and optionally captures the final frame.</summary>
    internal Task<LiveSnapshot> FrameAsync(int count, bool capture = true, CancellationToken cancellation = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        int extra = capture ? count : count;
        var steps = new Step[extra];
        for (int i = 0; i < count - (capture ? 1 : 0); i++) steps[i] = new Step(StepKind.Frame);
        if (capture) steps[^1] = new Step(StepKind.Capture);
        return Submit(steps, pixels: false, cancellation);
    }

    /// <summary>Closes the window, exactly as its close button does.</summary>
    internal void RequestClose()
    {
        if (_disposed || _closing) return;
        if (_cuiWindow != 0 && OperatingSystem.IsWindows()) Win32.PostMessage(_cuiWindow, Win32.WmClose, 0, 0);
        else if (_app != 0) Native.AppQuit(_app);
    }

    /// <summary>Wakes the window thread to look at the queue.</summary>
    internal void Kick()
    {
        if (!_disposed && !_closing && _messageHwnd != 0)
            Win32.PostMessage(_messageHwnd, WmBoundary, 0, 0);
    }

    /// <summary>Called when the visible CUI window receives WM_DESTROY or WM_NCDESTROY, before cui_app_destroy runs.</summary>
    internal void OnWindowDestroying(nint hwnd)
    {
        if (_cuiWindow != 0 && hwnd != _cuiWindow) return;
        _closing = true;
        FailAllPending("The live window closed before it could answer.");
    }

    private Task<LiveSnapshot> Submit(Step[] steps, bool pixels, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested)
            return Task.FromCanceled<LiveSnapshot>(cancellation);

        var command = new Command(steps, pixels, _messageHwnd);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_closing)
                throw new IOException("The live window is closing.");
            if (_queue.Count >= MaxQueue)
            {
                throw new InvalidOperationException(
                    $"The live session already has {MaxQueue} commands waiting; send the next one after those answer.");
            }

            if (cancellation.CanBeCanceled)
            {
                command.Registration = cancellation.Register(
                    static state => ((Command)state!).Cancel(), command);
            }

            _queue.Enqueue(command);
        }

        if (Win32.PostMessage(_messageHwnd, WmBoundary, 0, 0) == 0)
        {
            RemoveOrCancel(command);
            throw new IOException("The live session's message window is gone; the app is closing.");
        }

        return command.Tcs.Task;
    }

    private void RemoveOrCancel(Command target)
    {
        bool removed = false;
        lock (_gate)
        {
            int count = _queue.Count;
            for (int i = 0; i < count; i++)
            {
                Command other = _queue.Dequeue();
                if (ReferenceEquals(other, target)) removed = true;
                else _queue.Enqueue(other);
            }
        }

        if (removed) target.Discard();
        else target.Cancel();
    }

    /// <summary>
    /// The frame boundary: runs on the window thread inside CUI's wait, after the last frame was
    /// presented and before the next one begins. At most one step per frame, whatever else the
    /// same drain delivered.
    /// </summary>
    private void OnBoundary()
    {
        if (_disposed || _closing)
        {
            StopWake();
            return;
        }

        if (_callbackDepth > 0 || LiveResize.IsResizing(_cuiWindow))
        {
            // Somebody pumped messages from inside a draw callback, a deferred canvas painter, a
            // state disposer, or an OS modal resize loop. Nothing here is safe mid-frame: defer
            // and make sure a later boundary comes.
            if (HasWork()) WakeAgainSoon();
            return;
        }

        try
        {
            if (!_bound)
            {
                if (HasWork()) WakeAgainSoon();
                return;
            }

            ulong frame = Native.FrameCount(_ctx);

            if (_cancelCleanup is not null)
            {
                // Wait until a frame has actually drawn and consumed the cleanup / pending input.
                if (frame == _cancelFrame)
                {
                    WakeAgainSoon();
                    return;
                }

                _cancelCleanup.Tcs.TrySetCanceled();
                _cancelCleanup = null;
            }

            if (_armed)
            {
                if (frame == _armedFrame)
                {
                    WakeAgainSoon();
                    return;
                }

                FinishCapture();
            }

            if (frame != _steppedFrame)
            {
                _active ??= Dequeue();
                Command? command = _active;
                if (command is not null)
                {
                    if (command.CancelRequested)
                    {
                        _active = null;
                        StartCancellationCleanup(command);
                    }
                    else if (command.Tcs.Task.IsCompleted)
                    {
                        command.Discard();
                        _active = null;
                    }
                    else if (command.Index >= command.Steps.Length)
                    {
                        command.Discard();
                        command.Tcs.TrySetResult(new LiveSnapshot(frame, [], null, 0, 0, _frameState.FrameReady));
                        _active = null;
                    }
                    else
                    {
                        Apply(command.Steps[command.Index++], command, frame);
                        _steppedFrame = frame;
                        if (command.CancelRequested)
                        {
                            if (ReferenceEquals(_completing, command))
                            {
                                _armed = false;
                                _completing = null;
                            }

                            _active = null;
                            StartCancellationCleanup(command);
                        }
                    }
                }
            }
        }
        catch (Exception error)
        {
            _completing?.CompleteWithFault(error);
            _active?.CompleteWithFault(error);
            _cancelCleanup?.CompleteWithFault(error);
            _armed = false;
            _completing = null;
            _active = null;
            _cancelCleanup = null;
            ReleaseInjectedPress();
        }

        if (HasWork()) WakeAgainSoon();
        else StopWake();
    }

    private void Apply(Step step, Command command, ulong frame)
    {
        switch (step.Kind)
        {
            case StepKind.Frame:
                break;
            case StepKind.Move:
                Native.InputMouseMove(_ctx, step.X, step.Y);
                _lastInjectedFrame = frame;
                break;
            case StepKind.Down:
                Native.InputMouseButton(_ctx, MouseButton.Left, 1);
                _injectedPress = true;
                _lastInjectedFrame = frame;
                AfterPressHookForTests?.Invoke();
                break;
            case StepKind.Up:
                Native.InputMouseButton(_ctx, MouseButton.Left, 0);
                _injectedPress = false;
                _lastInjectedFrame = frame;
                AfterReleaseHookForTests?.Invoke();
                break;
            case StepKind.Wheel:
                Native.InputScroll(_ctx, step.X, step.Y);
                _lastInjectedFrame = frame;
                break;
            case StepKind.Focus:
                Native.InputFocus(_ctx, 1);
                _lastInjectedFrame = frame;
                break;
            case StepKind.KeyDown:
                Native.InputKey(_ctx, step.Key, 1, 0);
                _lastInjectedFrame = frame;
                break;
            case StepKind.KeyUp:
                Native.InputKey(_ctx, step.Key, 0, 0);
                _lastInjectedFrame = frame;
                break;
            case StepKind.Text:
                if (step.Text is { Length: > 0 } text)
                    ApplyText(text);

                _lastInjectedFrame = frame;
                break;
            case StepKind.Capture:
                WidgetRecorder.BeginFrame(_recorder);
                _armed = true;
                _armedFrame = frame;
                _completing = command;
                break;
        }

        Native.RequestRedraw(_ctx);
    }

    /// <summary>Increments the managed callback nesting depth so boundaries inside nested pumps defer.</summary>
    internal void EnterCallback() => _callbackDepth++;

    /// <summary>Decrements the managed callback nesting depth.</summary>
    internal void ExitCallback()
    {
        if (_callbackDepth > 0) _callbackDepth--;
    }

    /// <summary>
    /// The managed draw callback is starting. Every boundary until <see cref="SealFrame"/> defers:
    /// a modal dialog pumped from the callback would otherwise dispatch one mid-frame.
    /// </summary>
    internal void EnterFrame()
    {
        _callbackDepth++;
        if (!_armed || !_bound) return;

        // If an extra frame slipped in before the boundary ran (e.g. a modal resize callback),
        // restart the recording so a snapshot never mixes widgets from two frames.
        ulong current = Native.FrameCount(_ctx);
        if (current != _armedFrame + 1)
        {
            WidgetRecorder.BeginFrame(_recorder);
            _armedFrame = current - 1;
        }
    }

    /// <summary>
    /// Called at the end of the draw callback: the recorded labels are frame memory. The callback
    /// of the frame being recorded posts the finishing boundary itself, so the finish dispatch
    /// happens in the wait right after this frame's end.
    /// </summary>
    internal void SealFrame()
    {
        if (_callbackDepth > 0) _callbackDepth--;
        if (!_armed || !_bound) return;
        _recorder.SealPendingText();
        if (Native.FrameCount(_ctx) == _armedFrame + 1)
            Win32.PostMessage(_messageHwnd, WmBoundary, 0, 0);
    }

    private void FinishCapture()
    {
        Command command = _completing!;
        _armed = false;
        _completing = null;

        WidgetRecorder.EndFrame(_recorder, previous: null, _ctx);
        command.Discard();
        if (command.CancelRequested)
        {
            command.Tcs.TrySetCanceled();
            return;
        }

        SemanticWidget[] entries = [.. _recorder.Entries];
        ulong frame = Native.FrameCount(_ctx);
        bool ready = _frameState.FrameReady;
        byte[]? pixels = null;
        int width = 0, height = 0;
        if (command.WantPixels) (pixels, width, height) = CapturePixels();
        var snapshot = new LiveSnapshot(frame, entries, pixels, width, height, ready);
        _lastSnapshot = snapshot;
        command.Tcs.TrySetResult(snapshot);
    }

    private unsafe void ApplyText(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text + "\0");
        fixed (byte* p = utf8) Native.InputText(_ctx, (nint)p);
    }

    private unsafe (byte[] Pixels, int Width, int Height) CapturePixels()
    {
        nint data = Native.GetDrawData(_ctx);
        if (data == 0) throw new IOException("No completed frame to capture.");

        var frame = *(NativeDrawData*)data;
        int w = frame.WidthPx, h = frame.HeightPx;
        if (w <= 0 || h <= 0) throw new IOException($"The completed frame reported a {w}x{h} pixel area.");

        long bytes = checked((long)w * h * 4);
        if (bytes > MaxPixelPayloadBytes)
        {
            throw new IOException(
                $"A {w}x{h} frame is {bytes} bytes, past the live session's {MaxPixelPayloadBytes}-byte pixel cap.");
        }

        var buffer = new byte[(int)bytes];
        fixed (byte* p = buffer)
        {
            if (Native.AppScreenshot(_app, (nint)p, w, h) == 0)
                throw new IOException($"cui_app_screenshot rejected the {w}x{h} frame buffer.");
        }

        return (buffer, w, h);
    }

    /// <summary>
    /// A dropped cancel must not land as a click or acknowledge before an already-injected step
    /// has been consumed by a frame: if a press is down, park and release; if any step was
    /// injected on the current frame count, wait for one frame to draw before acknowledging.
    /// </summary>
    private void StartCancellationCleanup(Command command)
    {
        command.Discard();
        ulong currentFrame = _bound ? Native.FrameCount(_ctx) : 0;
        if (_injectedPress)
        {
            Native.InputMouseLeave(_ctx);
            Native.InputMouseButton(_ctx, MouseButton.Left, 0);
            _injectedPress = false;
            Native.RequestRedraw(_ctx);
            _cancelCleanup = command;
            _cancelFrame = currentFrame;
            return;
        }

        if (_bound && _lastInjectedFrame != 0 && currentFrame == _lastInjectedFrame)
        {
            Native.RequestRedraw(_ctx);
            _cancelCleanup = command;
            _cancelFrame = currentFrame;
            return;
        }

        command.Tcs.TrySetCanceled();
    }

    private void ReleaseInjectedPress()
    {
        if (!_injectedPress) return;
        _injectedPress = false;
        if (!_bound) return;
        Native.InputMouseButton(_ctx, MouseButton.Left, 0);
        Native.RequestRedraw(_ctx);
    }

    private bool HasWork()
    {
        lock (_gate) return _queue.Count > 0 || _active is not null || _completing is not null || _cancelCleanup is not null;
    }

    private void WakeAgainSoon()
    {
        if (_timerArmed || _messageHwnd == 0 || _disposed || _closing) return;
        if (Win32.SetTimer(_messageHwnd, TimerId, TimerMs, 0) != 0)
        {
            _timerArmed = true;
        }
        else
        {
            FailAllPending("The live session's wake timer could not be armed.");
        }
    }

    private void StopWake()
    {
        if (!_timerArmed) return;
        _timerArmed = false;
        Win32.KillTimer(_messageHwnd, TimerId);
    }

    private Command? Dequeue()
    {
        lock (_gate) return _queue.Count == 0 ? null : _queue.Dequeue();
    }

    private void FailAllPending(string message)
    {
        List<Command> unfinished = [];
        lock (_gate)
        {
            unfinished.AddRange(_queue);
            _queue.Clear();
            if (_active is not null) unfinished.Add(_active);
            if (_completing is not null && !ReferenceEquals(_completing, _active)) unfinished.Add(_completing);
            if (_cancelCleanup is not null
                && !ReferenceEquals(_cancelCleanup, _active)
                && !ReferenceEquals(_cancelCleanup, _completing))
            {
                unfinished.Add(_cancelCleanup);
            }

            _active = null;
            _completing = null;
            _cancelCleanup = null;
            _armed = false;
        }

        foreach (Command command in unfinished)
        {
            command.Discard();
            command.Tcs.TrySetException(new InvalidOperationException(message));
        }
    }

    // ---- NamedPipe server & protocol execution ------------------------------------------------

    private async Task RunPipeServerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                await ServePipeClientAsync(server, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                if (ct.IsCancellationRequested) break;
                await Task.Delay(25, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task ServePipeClientAsync(NamedPipeServerStream stream, CancellationToken ct)
    {
        // Wait briefly for the first frame to bind the window if a client connected during startup.
        Stopwatch bindWait = Stopwatch.StartNew();
        while (!_bound && !_disposed && !_closing && bindWait.Elapsed < TimeSpan.FromSeconds(15))
        {
            await Task.Delay(10, ct).ConfigureAwait(false);
        }

        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = false };

        writer.Write($"CUI-READY {_options.Width} {_options.Height}\n");
        writer.Flush();

        while (!ct.IsCancellationRequested && stream.IsConnected)
        {
            string? read;
            try
            {
                read = HeadlessLoop.ReadCommand(reader);
            }
            catch (ArgumentException error)
            {
                HeadlessLoop.Answer(writer, ok: false, error.Message);
                return;
            }
            catch (IOException)
            {
                return;
            }

            if (read is null) return;
            string line = read.Trim();
            if (line.Length == 0) continue;

            int space = line.IndexOf(' ', StringComparison.Ordinal);
            string command = (space < 0 ? line : line[..space]).ToLowerInvariant();
            string argument = space < 0 ? "" : line[(space + 1)..];

            if (command is "quit" or "exit")
            {
                HeadlessLoop.Answer(writer, ok: true, "");
                RequestClose();
                return;
            }

            try
            {
                string payload = await ExecuteCommandAsync(command, argument, ct).ConfigureAwait(false);
                HeadlessLoop.Answer(writer, ok: true, payload);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or FormatException or OverflowException)
            {
                HeadlessLoop.Answer(writer, ok: false, error.Message);
            }
        }
    }

    /// <summary>Executes one headless-protocol command against the live window.</summary>
    internal async Task<string> ExecuteCommandAsync(string command, string argument, CancellationToken ct = default)
    {
        switch (command)
        {
            case "tree" or "print":
                {
                    LiveSnapshot snap = await SnapshotAsync(pixels: false, ct).ConfigureAwait(false);
                    return snap.Print();
                }

            case "layout":
                {
                    LiveSnapshot snap = await SnapshotAsync(pixels: false, ct).ConfigureAwait(false);
                    return LayoutFormatter.Print(snap.Tree);
                }

            case "find":
                {
                    ArgumentException.ThrowIfNullOrEmpty(argument);
                    LiveSnapshot snap = await SnapshotAsync(pixels: false, ct).ConfigureAwait(false);
                    SemanticNode node = FindNode(snap, argument);
                    return $"{node.Kind} \"{node.Text}\" at {node.Rect}";
                }

            case "click":
                {
                    ArgumentException.ThrowIfNullOrEmpty(argument);
                    LiveSnapshot before = await SnapshotAsync(pixels: false, ct).ConfigureAwait(false);
                    SemanticNode node = FindNode(before, argument);
                    float cx = node.Rect.X + (node.Rect.W * 0.5f);
                    float cy = node.Rect.Y + (node.Rect.H * 0.5f);
                    LiveSnapshot after = await ClickAsync(cx, cy, pixels: false, ct).ConfigureAwait(false);
                    return after.Print();
                }

            case "move":
                {
                    string[] parts = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length != 2)
                        throw new ArgumentException("move needs two numbers: `move 10 20`.", nameof(argument));
                    float x = float.Parse(parts[0], CultureInfo.InvariantCulture);
                    float y = float.Parse(parts[1], CultureInfo.InvariantCulture);
                    LiveSnapshot snap = await MoveAsync(x, y, pixels: false, ct).ConfigureAwait(false);
                    return snap.Print();
                }

            case "wheel":
                {
                    string[] parts = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length is < 3 or > 4)
                        throw new ArgumentException("wheel needs three numbers and an optional fourth: `wheel 120 400 -8 [0]`.", nameof(argument));
                    float x = float.Parse(parts[0], CultureInfo.InvariantCulture);
                    float y = float.Parse(parts[1], CultureInfo.InvariantCulture);
                    float dy = float.Parse(parts[2], CultureInfo.InvariantCulture);
                    float dx = parts.Length == 4 ? float.Parse(parts[3], CultureInfo.InvariantCulture) : 0f;
                    LiveSnapshot snap = await WheelAsync(x, y, dx, dy, ct).ConfigureAwait(false);
                    return snap.Print();
                }

            case "focus":
                {
                    LiveSnapshot snap = await FocusAsync(ct).ConfigureAwait(false);
                    return snap.Print();
                }

            case "key":
                {
                    Key key = HeadlessLoop.ParseKey(argument);
                    LiveSnapshot snap = await KeyAsync(key, ct).ConfigureAwait(false);
                    return snap.Print();
                }

            case "type":
                {
                    LiveSnapshot snap = await TypeAsync(argument, ct).ConfigureAwait(false);
                    return snap.Print();
                }

            case "frame":
                {
                    int count = argument.Length == 0 ? 1 : int.Parse(argument, CultureInfo.InvariantCulture);
                    if (count < 1) throw new ArgumentException($"frame needs a count of at least 1, not {count}.");
                    LiveSnapshot snap = await FrameAsync(count, capture: true, ct).ConfigureAwait(false);
                    return snap.Print();
                }

            case "pulse":
                {
                    int count = argument.Length == 0 ? 1 : int.Parse(argument, CultureInfo.InvariantCulture);
                    if (count < 1) throw new ArgumentException($"pulse needs a count of at least 1, not {count}.");
                    await FrameAsync(count, capture: false, ct).ConfigureAwait(false);
                    return $"{count} heap {GC.GetTotalMemory(false)}";
                }

            case "wait":
                {
                    double seconds = argument.Length == 0
                        ? HeadlessLoop.DefaultWaitSeconds
                        : HeadlessLoop.ParseWaitSeconds(argument);
                    return await WaitReadyAsync(seconds, ct).ConfigureAwait(false);
                }

            case "waittext":
                {
                    int space = argument.IndexOf(' ', StringComparison.Ordinal);
                    if (space < 0)
                        throw new ArgumentException("waittext needs a timeout in seconds and a query: `waittext 5 Button \"Save\"`.", nameof(argument));
                    double seconds = HeadlessLoop.ParseWaitSeconds(argument[..space]);
                    string query = argument[(space + 1)..];
                    if (query.Trim().Length == 0)
                        throw new ArgumentException("waittext needs a query after the timeout.", nameof(argument));
                    return await WaitTextAsync(seconds, query, ct).ConfigureAwait(false);
                }

            case "faults":
                {
                    lock (_gate) return _faults.Count == 0 ? "" : string.Join("\n", _faults);
                }

            case "png":
                {
                    string path = argument.Length == 0
                        ? Path.Combine(Path.GetTempPath(), $"cui-{Guid.NewGuid():N}.png")
                        : Path.GetFullPath(argument);
                    LiveSnapshot snap = await SnapshotAsync(pixels: true, ct).ConfigureAwait(false);
                    SavePng(path, snap.Pixels!, snap.PixelWidth, snap.PixelHeight);
                    return path;
                }

            default:
                throw new InvalidOperationException($"Unknown command \"{command}\". Known: {string.Join(", ", HeadlessLoop.Known)}.");
        }
    }

    private static SemanticNode FindNode(LiveSnapshot snapshot, string query)
    {
        SemanticNode tree = snapshot.Tree;
        if (tree.TryFind(query, out SemanticNode node)) return node;
        throw new InvalidOperationException($"No widget matching \"{query}\". The frame drew:\n{tree.Print()}");
    }

    private async Task<string> WaitReadyAsync(double seconds, CancellationToken ct)
    {
        TimeSpan budget = TimeSpan.FromSeconds(seconds);
        Stopwatch watch = Stopwatch.StartNew();
        int frames = 0;
        while (true)
        {
            LiveSnapshot snap = await SnapshotAsync(pixels: false, ct).ConfigureAwait(false);
            frames++;
            if (snap.Ready) return snap.Print();
            if (watch.Elapsed >= budget)
            {
                throw new InvalidOperationException(
                    $"No frame marked itself ready within {seconds}s ({frames} frames drawn). " +
                    "The app declares readiness with ui.MarkReady() once its data is complete.");
            }
        }
    }

    private async Task<string> WaitTextAsync(double seconds, string query, CancellationToken ct)
    {
        TimeSpan budget = TimeSpan.FromSeconds(seconds);
        Stopwatch watch = Stopwatch.StartNew();
        int frames = 0;
        while (true)
        {
            LiveSnapshot snap = await SnapshotAsync(pixels: false, ct).ConfigureAwait(false);
            frames++;
            if (snap.Tree.TryFind(query, out _)) return snap.Print();
            if (watch.Elapsed >= budget)
            {
                throw new InvalidOperationException(
                    $"No frame drew \"{query}\" within {seconds}s ({frames} frames drawn). " +
                    $"The last frame drew:\n{snap.Print()}");
            }
        }
    }

    private static unsafe void SavePng(string path, byte[] pixels, int w, int h)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        byte[] pathBytes = Encoding.UTF8.GetBytes(path + "\0");
        fixed (byte* p = pixels)
        fixed (byte* name = pathBytes)
        {
            if (Native.WritePng((nint)name, (nint)p, w, h, w * 4) == 0)
                throw new IOException($"Could not write {path}.");
        }
    }

    private void WriteDiscoveryFile()
    {
        try
        {
            string directory = GetDiscoveryDirectory();
            Directory.CreateDirectory(directory);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("pid", Environment.ProcessId);
                writer.WriteString("pipe", PipeName);
                writer.WriteString("title", _options.Title ?? "");
                writer.WriteString("processPath", Environment.ProcessPath ?? "");
                writer.WriteString("workingDirectory", Directory.GetCurrentDirectory());
                writer.WriteNumber("width", _options.Width);
                writer.WriteNumber("height", _options.Height);
                writer.WriteEndObject();
            }

            File.WriteAllBytes(DiscoveryFilePath, stream.ToArray());
        }
        catch (IOException)
        {
            // Discovery is best-effort if the local appdata directory is locked.
        }
    }

    private void DeleteDiscoveryFile()
    {
        try
        {
            if (File.Exists(DiscoveryFilePath)) File.Delete(DiscoveryFilePath);
        }
        catch (IOException)
        {
        }
    }

    // ---- the message window: wake, boundary, teardown ----------------------------------------

    private static bool _classRegistered;
    private static nint _instance;
    private static nint _wndProc;

    private unsafe bool CreateMessageWindow()
    {
        _instance = Win32.GetModuleHandle(null);
        if (!_classRegistered)
        {
            _wndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&Proc;
            fixed (char* name = ClassName)
            {
                var windowClass = new Win32.WndClass
                {
                    WndProc = _wndProc,
                    Instance = _instance,
                    ClassName = (nint)name,
                };
                if (Win32.RegisterClass(in windowClass) == 0) return false;
            }

            _classRegistered = true;
        }

        nint hwnd = Win32.CreateWindowEx(
            0, ClassName, "CuiSharp Live", 0, 0, 0, 0, 0,
            Win32.HWNDMessage, 0, _instance, 0);
        _messageHwnd = hwnd;
        return hwnd != 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint Proc(nint hwnd, uint message, nint wparam, nint lparam)
    {
        if (message is WmBoundary || (message == Win32.WmTimer && wparam == TimerId))
        {
            Active?.OnBoundaryFor(hwnd);
            return 0;
        }

        return Win32.DefWindowProc(hwnd, message, wparam, lparam);
    }

    private void OnBoundaryFor(nint hwnd)
    {
        if (hwnd == _messageHwnd) OnBoundary();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _closing = true;
        }

        DeleteDiscoveryFile();
        _pipeCts.Cancel();

        FailAllPending("The live app closed before it could answer.");

        if (_bound) WidgetRecorder.Attach(_recorder, enabled: false);
        StopWake();
        if (_messageHwnd != 0)
        {
            Win32.DestroyWindow(_messageHwnd);
            _messageHwnd = 0;
        }

        if (_classRegistered)
        {
            Win32.UnregisterClass(ClassName, _instance);
            _classRegistered = false;
        }

        if (_reserved)
        {
            lock (StaticGate)
            {
                if (ReferenceEquals(_published.Session, this) || _published.Session is null)
                    _published = new Published(_published.Generation + 1, null);
            }
        }

        try
        {
            _pipeTask.Wait(TimeSpan.FromMilliseconds(200));
        }
        catch (AggregateException)
        {
        }

        _pipeCts.Dispose();
    }

    private enum StepKind
    {
        Frame,
        Move,
        Down,
        Up,
        Wheel,
        Focus,
        KeyDown,
        KeyUp,
        Text,
        Capture,
    }

    private readonly struct Step(StepKind kind, float x = 0, float y = 0, Key Key = default, string? Text = null)
    {
        internal readonly StepKind Kind = kind;
        internal readonly float X = x;
        internal readonly float Y = y;
        internal readonly Key Key = Key;
        internal readonly string? Text = Text;
    }

    private sealed class Command(Step[] steps, bool pixels, nint messageHwnd)
    {
        internal readonly Step[] Steps = steps;
        internal readonly bool WantPixels = pixels;
        internal readonly nint MessageHwnd = messageHwnd;
        internal readonly TaskCompletionSource<LiveSnapshot> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenRegistration Registration;
        internal int Index;

        internal void Discard() => Registration.Dispose();

        internal void Cancel()
        {
            CancelRequested = true;
            Win32.PostMessage(MessageHwnd, WmBoundary, 0, 0);
        }

        internal volatile bool CancelRequested;

        internal void CompleteWithFault(Exception error)
        {
            Discard();
            Tcs.TrySetException(error);
        }
    }
}

/// <summary>
/// One frame a driven window finished: what it drew, whether it marked itself ready, and the
/// pixels it presented. Immutable, so a reader cannot be overtaken by the next frame.
/// </summary>
internal sealed record LiveSnapshot(
    ulong Frame,
    SemanticWidget[] Entries,
    byte[]? Pixels,
    int PixelWidth,
    int PixelHeight,
    bool Ready = false)
{
    private SemanticNode? _tree;

    /// <summary>The frame's semantic tree.</summary>
    internal SemanticNode Tree => _tree ??= SemanticNode.Build(Entries);

    /// <summary>The frame's controls nested the way the app drew them.</summary>
    internal string Print() => Tree.Print();
}
