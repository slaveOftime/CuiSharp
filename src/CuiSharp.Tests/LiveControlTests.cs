using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CuiSharp.Hosting;
using CuiSharp.Tools;

namespace CuiSharp.Tests;

/// <summary>
/// Live-attach verification on a real window with a real message loop and a local named pipe:
/// a click driven from another thread or via <c>dotnet cui --attach</c> increments the counter
/// that same window draws, snapshots carry the completed frame's pixels, idle windows wake via
/// <c>PostMessage</c>, nested message pumps inside <c>Draw</c> or <c>Canvas</c> painters defer
/// boundary execution, cancelled commands wait for cleanup frames before acknowledging, and
/// <c>dotnet cui ps</c> / <c>dotnet cui --attach</c> drive the live window over its named pipe.
/// </summary>
public class LiveControlTests
{
    private static bool CanRun => OperatingSystem.IsWindows();

    /// <summary>A real <see cref="CuiApp.Run"/> window on its own thread, with an isolated discovery directory.</summary>
    private sealed class LiveWindow : IDisposable
    {
        private readonly Thread _thread;
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string? _previousLiveDir;
        private volatile Exception? _error;
        private nint _hwnd;

        private LiveWindow(bool live, DrawFn draw)
        {
            DiscoveryDir = Path.Combine(Path.GetTempPath(), $"cui-live-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(DiscoveryDir);
            _previousLiveDir = Environment.GetEnvironmentVariable(LiveSession.DirectoryEnvironmentVariable);
            Environment.SetEnvironmentVariable(LiveSession.DirectoryEnvironmentVariable, DiscoveryDir);

            long generationBefore = LiveSession.Generation;
            _thread = new Thread(() =>
            {
                try
                {
                    CuiApp.Run(
                        new CuiAppOptions
                        {
                            Title = $"CuiSharp live proof {Guid.NewGuid():N}"[..36],
                            Width = 420,
                            Height = 320,
                            Hidden = true,
                            LiveAttach = live,
                            WindowCreated = window => Volatile.Write(ref _hwnd, window.NativeWindow),
                        },
                        draw);
                }
                catch (Exception error)
                {
                    _error = error;
                }
                finally
                {
                    _closed.TrySetResult();
                }
            })
            { IsBackground = true };
            _thread.Start();

            if (!live) return;
            try
            {
                Session = LiveSession.WaitForActive(generationBefore, TimeSpan.FromSeconds(30));
            }
            catch (Exception waitError)
            {
                throw _error ?? waitError;
            }
        }

        internal string DiscoveryDir { get; }

        internal LiveSession Session { get; } = null!;

        internal nint WindowHandle => Volatile.Read(ref _hwnd);

        internal bool IsClosed => _closed.Task.IsCompleted;

        internal static LiveWindow StartLive(DrawFn draw) => new(live: true, draw);

        internal static LiveWindow StartPlain(DrawFn draw) => new(live: false, draw);

        internal bool WaitClosed(TimeSpan timeout) => _closed.Task.Wait(timeout);

        public void Dispose()
        {
            try
            {
                if (!_closed.Task.IsCompleted)
                {
                    if (WindowHandle != 0) Interop.Win32.RequestClose(WindowHandle);
                    else Session?.RequestClose();
                }

                _closed.Task.Wait(TimeSpan.FromSeconds(15));
                _thread.Join(TimeSpan.FromSeconds(5));
                if (_error is not null)
                    throw new InvalidOperationException("The proof window died before it closed.", _error);
            }
            finally
            {
                Environment.SetEnvironmentVariable(LiveSession.DirectoryEnvironmentVariable, _previousLiveDir);
                try
                {
                    if (Directory.Exists(DiscoveryDir)) Directory.Delete(DiscoveryDir, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static T Await<T>(Task<T> task) =>
        task.WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();

    [Fact]
    public void AClickFromTheToolIncrementsTheCounterThatSameWindowDraws()
    {
        if (!CanRun) return;
        int count = 0;
        using LiveWindow window = LiveWindow.StartLive(ui =>
        {
            ui.Heading("Live proof", 2);
            ui.Secondary($"Count: {count}");
            if (ui.Button("Increase", new ButtonOpts { Variant = ButtonVariant.Primary })) count++;
        });

        LiveSnapshot before = Await(window.Session.SnapshotAsync());
        Assert.Contains(before.Entries, entry => entry.Text == "Count: 0");
        SemanticWidget button = Assert.Single(before.Entries, entry => entry.Kind == "Button" && entry.Text == "Increase");
        Assert.NotNull(before.Pixels);
        Assert.Contains(before.Pixels, byteValue => byteValue != 0);

        LiveSnapshot after = Await(window.Session.ClickAsync(button.Center.X, button.Center.Y));

        Assert.Equal(1, count);
        Assert.Contains(after.Entries, entry => entry.Text == "Count: 1");
        Assert.DoesNotContain(after.Entries, entry => entry.Text == "Count: 0");
        Assert.False(before.Pixels.AsSpan().SequenceEqual(after.Pixels!.AsSpan()));
        Assert.True(after.Frame > before.Frame, "the answer must be a later frame than the one measured");
    }

    [Fact]
    public void AnIdleWindowWakesForACommandWithoutDrawingFramesOnItsOwn()
    {
        if (!CanRun) return;
        using LiveWindow window = LiveWindow.StartLive(ui =>
        {
            ui.Heading("Idle proof", 2);
            ui.Secondary("nothing animates here");
        });

        LiveSnapshot settled = Await(window.Session.SnapshotAsync(pixels: false));
        Thread.Sleep(TimeSpan.FromMilliseconds(1500));
        LiveSnapshot woken = Await(window.Session.SnapshotAsync(pixels: false));

        ulong drawn = woken.Frame - settled.Frame;
        Assert.True(drawn <= 6, $"the idle window drew {drawn} frames across the sleep — that is continuous mode, not a wake");
    }

    [Fact]
    public void AWindowWithoutLiveAttachPublishesNoSession()
    {
        if (!CanRun) return;
        long before = LiveSession.Generation;
        using LiveWindow window = LiveWindow.StartPlain(ui => ui.Heading("Nobody drives me", 2));

        Assert.NotEqual(nint.Zero, SpinUntilWindow(window, TimeSpan.FromSeconds(30)));
        Thread.Sleep(TimeSpan.FromMilliseconds(300));
        Assert.Equal(before, LiveSession.Generation);
        Assert.Null(LiveSession.Active);
    }

    [Fact]
    public void ACancelledPressIsParkedReleasedAndOnlyThenAcknowledgedWithoutClicking()
    {
        if (!CanRun) return;
        int count = 0;
        using LiveWindow window = LiveWindow.StartLive(ui =>
        {
            ui.Heading("Cancel proof", 2);
            ui.Secondary($"Count: {count}");
            if (ui.Button("Increase", new ButtonOpts { Variant = ButtonVariant.Primary })) count++;
        });
        LiveSession session = window.Session;

        LiveSnapshot first = Await(session.SnapshotAsync(pixels: false));
        SemanticWidget button = Assert.Single(first.Entries, entry => entry.Kind == "Button" && entry.Text == "Increase");
        (float x, float y) = button.Center;

        using var midCancel = new CancellationTokenSource();
        session.AfterPressHookForTests = midCancel.Cancel;
        try
        {
            Task<LiveSnapshot> clickTask = session.ClickAsync(x, y, pixels: false, midCancel.Token);
            TaskCanceledException canceled = Assert.Throws<TaskCanceledException>(
                () => Await(clickTask));
            Assert.True(midCancel.IsCancellationRequested, $"unexpected completion: {canceled.Message}");
        }
        finally
        {
            session.AfterPressHookForTests = null;
        }

        Thread.Sleep(TimeSpan.FromMilliseconds(150));
        Assert.Equal(0, count);
        LiveSnapshot later = Await(session.SnapshotAsync(pixels: false));
        Assert.Contains(later.Entries, entry => entry.Text == "Count: 0");

        LiveSnapshot clicked = Await(session.ClickAsync(x, y, pixels: false));
        Assert.Equal(1, count);
        Assert.Contains(clicked.Entries, entry => entry.Text == "Count: 1");
    }

    [Fact]
    public void ACancelledReleaseAcknowledgesOnlyAfterTheFrameConsumedIt()
    {
        if (!CanRun) return;
        int count = 0;
        int drawnFramesAfterRelease = 0;
        bool releaseSeen = false;
        using LiveWindow window = LiveWindow.StartLive(ui =>
        {
            if (Volatile.Read(ref releaseSeen))
                Interlocked.Increment(ref drawnFramesAfterRelease);
            ui.Heading("Cancel after Up", 2);
            ui.Secondary($"Count: {count}");
            if (ui.Button("Increase", new ButtonOpts { Variant = ButtonVariant.Primary })) count++;
        });
        LiveSession session = window.Session;

        LiveSnapshot first = Await(session.SnapshotAsync(pixels: false));
        SemanticWidget button = Assert.Single(first.Entries, entry => entry.Kind == "Button" && entry.Text == "Increase");
        (float x, float y) = button.Center;

        using var cancelAfterUp = new CancellationTokenSource();
        session.AfterReleaseHookForTests = () =>
        {
            Volatile.Write(ref releaseSeen, true);
            cancelAfterUp.Cancel();
        };
        try
        {
            Task<LiveSnapshot> clickTask = session.ClickAsync(x, y, pixels: false, cancelAfterUp.Token);
            Assert.Throws<TaskCanceledException>(() => Await(clickTask));
        }
        finally
        {
            session.AfterReleaseHookForTests = null;
        }

        // By the time TaskCanceledException is observed, at least one frame after the release
        // has already been drawn, so the committed click happened before acknowledgement.
        Assert.True(Volatile.Read(ref drawnFramesAfterRelease) >= 1);
        Assert.Equal(1, count);
    }

    [Fact]
    public void ANestedPumpInsideDrawOrCanvasPaintCannotEndACaptureOrInjectMidFrame()
    {
        if (!CanRun) return;
        bool painted = false;
        using LiveWindow window = LiveWindow.StartLive(ui =>
        {
            if (LiveSession.Active is { } session)
            {
                session.Kick();
                PumpThreadMessages();
            }

            ui.Heading("Pumping proof", 2);
            ui.Canvas("pump-canvas", Size.Px(120), Size.Px(40), (cui, rect) =>
            {
                if (LiveSession.Active is { } live)
                {
                    live.Kick();
                    PumpThreadMessages();
                }

                cui.DrawRect(rect, Color.Accent);
                cui.MarkReady();
                painted = true;
            });
            ui.Button("Below", new ButtonOpts());
        });

        LiveSnapshot shot = Await(window.Session.SnapshotAsync());
        Assert.True(painted);
        Assert.True(shot.Ready);
        Assert.Contains(shot.Entries, entry => entry.Kind == "Heading" && entry.Text == "Pumping proof");
        Assert.Contains(shot.Entries, entry => entry.Kind == "Canvas" && entry.Text == "pump-canvas");
        Assert.Contains(shot.Entries, entry => entry.Kind == "Button" && entry.Text == "Below");
        Assert.NotNull(shot.Pixels);
        Assert.Contains(shot.Pixels, byteValue => byteValue != 0);
    }

    [Fact]
    public void DotnetCuiPsAndAttachDriveTheLiveWindowOverNamedPipeAndLeaveItOpenUntilQuit()
    {
        if (!CanRun) return;
        int clicks = 0;
        using LiveWindow window = LiveWindow.StartLive(ui =>
        {
            using (ui.Column(new Box { Id = "main", Gap = 8, Pad = 16 }))
            {
                ui.Heading("Live Pipe App", 1);
                ui.Text($"Clicks: {clicks}");
                if (ui.Button("Increment", new ButtonOpts { Variant = ButtonVariant.Primary }))
                    clicks++;
                if (clicks >= 1) ui.MarkReady();
            }
        });

        // 1. `dotnet cui ps --json` discovers the running live window and prunes dead PIDs.
        File.WriteAllText(
            Path.Combine(window.DiscoveryDir, "stale-dead.json"),
            """{"pid":99999999,"pipe":"cui-live-dead","title":"Dead","processPath":"","workingDirectory":"","width":100,"height":100}""");

        (int exit, string psJson, string err) = RunCli("ps", "--json");
        Assert.Equal(0, exit);
        Assert.Equal("", err);
        Assert.False(File.Exists(Path.Combine(window.DiscoveryDir, "stale-dead.json")));
        using (JsonDocument doc = JsonDocument.Parse(psJson))
        {
            JsonElement item = Assert.Single(doc.RootElement.EnumerateArray());
            Assert.Equal(Environment.ProcessId, item.GetProperty("pid").GetInt32());
            Assert.Equal(window.Session.PipeName, item.GetProperty("pipe").GetString());
        }

        // 2. `dotnet cui tree --attach` reads the live window's tree without closing it.
        (exit, string treeBefore, err) = RunCli("tree", "--attach");
        Assert.Equal(0, exit);
        Assert.Contains("Heading \"Live Pipe App\"", treeBefore, StringComparison.Ordinal);
        Assert.Contains("Text \"Clicks: 0\"", treeBefore, StringComparison.Ordinal);
        Assert.False(window.IsClosed);

        // 3. `dotnet cui click Increment -a` clicks the button in the live window and leaves it open.
        (exit, string treeAfter, err) = RunCli("click", "Increment", "-a");
        Assert.Equal(0, exit);
        Assert.Equal(1, clicks);
        Assert.Contains("Text \"Clicks: 1\"", treeAfter, StringComparison.Ordinal);
        Assert.False(window.IsClosed);

        // 4. `dotnet cui layout -a --wait ready` verifies readiness and prints the layout tree.
        (exit, string layout, err) = RunCli("layout", "-a", "--wait", "ready");
        Assert.Equal(0, exit);
        Assert.Contains("Column \"main\"", layout, StringComparison.Ordinal);
        Assert.Contains("Button \"Increment\"", layout, StringComparison.Ordinal);

        // 5. `dotnet cui png <path> -a` captures the live window's pixels to a PNG file.
        string pngPath = Path.Combine(window.DiscoveryDir, "live-shot.png");
        (exit, string savedPath, err) = RunCli("png", pngPath, "-a");
        Assert.Equal(0, exit);
        Assert.True(File.Exists(pngPath));
        Assert.True(new FileInfo(pngPath).Length > 100);
        Assert.Contains("live-shot.png", savedPath, StringComparison.Ordinal);

        // 6. `dotnet cui quit -a` closes the live window cleanly.
        (exit, _, err) = RunCli("quit", "-a");
        Assert.Equal(0, exit);
        Assert.True(window.WaitClosed(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ACanceledKeyIsReleasedBeforeAcknowledgementAndTheNextKeyStillWorks()
    {
        if (!CanRun) return;
        using var cancel = new CancellationTokenSource();
        bool sawDown = false, held = false;
        int presses = 0;
        using LiveWindow window = LiveWindow.StartLive(ui =>
        {
            held = ui.KeyDown(Key.A);
            if (held)
            {
                sawDown = true;
                if (ui.KeyPressed(Key.A)) presses++;
                cancel.Cancel();
            }
            ui.Text(held ? "Held" : "Released");
        });

        await Assert.ThrowsAsync<TaskCanceledException>(() => window.Session.KeyAsync(Key.A, cancel.Token).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(sawDown);
        Assert.False(held);
        LiveSnapshot later = await window.Session.FrameAsync(3).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(later.Entries, widget => widget.Text == "Released");
        int before = presses;
        await window.Session.KeyAsync(Key.A).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(before + 1, presses);
        Assert.False(held);
    }

    [Fact]
    public async Task ABackgroundSurfacePainterDoesNotBlockTheLiveWindowsBoundary()
    {
        if (!CanRun) return;
        using LiveWindow window = LiveWindow.StartLive(ui => ui.Text("Live window"));
        Await(window.Session.SnapshotAsync(pixels: false));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task background = Task.Run(() =>
        {
            using var surface = CuiSurface.Create(100, 100);
            surface.BeginFrame();
            surface.Ui.Canvas("background", Size.Px(80), Size.Px(80), (ui, rect) =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                ui.DrawRect(rect, Color.Accent);
            });
            surface.EndFrame();
        });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            LiveSnapshot snapshot = await window.Session.SnapshotAsync(pixels: false)
                .WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Contains(snapshot.Entries, widget => widget.Text == "Live window");
            Assert.False(background.IsCompleted);
        }
        finally
        {
            release.Set();
            await background.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData("wait", "0.2")]
    [InlineData("waittext", "0.2 Target")]
    public async Task AWaitExpiresWhileItsFrameIsBlockedInsteadOfAcceptingALateSuccess(string command, string argument)
    {
        if (!CanRun) return;
        bool armed = false;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using LiveWindow window = LiveWindow.StartLive(ui =>
        {
            if (Volatile.Read(ref armed))
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                ui.MarkReady();
                ui.Text("Target");
            }
            else ui.Text("Loading");
        });
        Await(window.Session.SnapshotAsync(pixels: false));
        Volatile.Write(ref armed, true);
        Task<LiveSnapshot> blocked = window.Session.SnapshotAsync(pixels: false);
        try
        {
            // Start the clock only once the UI is definitely blocked; scheduling a first
            // frame must not consume the deadline this test is trying to exercise.
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Task<string> wait = window.Session.ExecuteCommandAsync(command, argument);
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                wait.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Contains("within 0.2s", error.Message, StringComparison.Ordinal);
            Assert.False(release.IsSet);
        }
        finally { release.Set(); }
        await blocked.WaitAsync(TimeSpan.FromSeconds(5));
        LiveSnapshot later = await window.Session.SnapshotAsync(pixels: false).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(later.Ready);
        Assert.Contains(later.Entries, widget => widget.Text == "Target");
    }

    [Fact]
    public async Task DisconnectingDuringAWaitFreesThePipeForTheNextClient()
    {
        if (!CanRun) return;
        int frames = 0;
        using LiveWindow window = LiveWindow.StartLive(ui =>
        {
            Interlocked.Increment(ref frames);
            ui.Text("Still loading");
        });
        Await(window.Session.SnapshotAsync(pixels: false));
        using (var first = ConnectPipe(window))
        {
            using var reader = new StreamReader(first, Encoding.UTF8, false, 1024, leaveOpen: true);
            Assert.StartsWith("CUI-READY ", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            int before = Volatile.Read(ref frames);
            first.Write(Encoding.UTF8.GetBytes("wait 3600\n"));
            first.Flush();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref frames) >= before + 3, TimeSpan.FromSeconds(5)));
        }
        using var next = ConnectPipe(window);
        using var client = HeadlessClient.Attach(next, next, readyTimeout: TimeSpan.FromSeconds(3));
        Answer answer = client.Send("tree", answerTimeout: TimeSpan.FromSeconds(3));
        Assert.True(answer.Ok, answer.Payload);
        Assert.Contains("Still loading", answer.Payload, StringComparison.Ordinal);
        Assert.False(window.IsClosed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("part of a command")]
    public async Task ClosingTheWindowClosesAnIdleOrPartialLineClientAndJoinsTheServer(string partialLine)
    {
        if (!CanRun) return;
        using LiveWindow window = LiveWindow.StartLive(ui => ui.Text("Shutdown"));
        using var pipe = ConnectPipe(window);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
        Assert.StartsWith("CUI-READY ", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        if (partialLine.Length > 0)
        {
            pipe.Write(Encoding.UTF8.GetBytes(partialLine));
            pipe.Flush();
        }
        window.Session.RequestClose();
        Assert.True(window.WaitClosed(TimeSpan.FromSeconds(5)));
        Assert.Null(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(window.Session.PipeCompletion.IsCompletedSuccessfully);
        Assert.False(File.Exists(window.Session.DiscoveryFilePath));
    }

    private static NamedPipeClientStream ConnectPipe(LiveWindow window)
    {
        var pipe = new NamedPipeClientStream(".", window.Session.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { pipe.Connect(3000); return pipe; }
        catch { pipe.Dispose(); throw; }
    }

    private static (int Exit, string Out, string Err) RunCli(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = Cli.Run(args, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    private static void PumpThreadMessages()
    {
        while (Interop.Win32.PeekMessage(out Interop.Win32.Msg message, 0, 0, 0, Interop.Win32.PmRemove) != 0)
            Interop.Win32.DispatchMessage(in message);
    }

    private static nint SpinUntilWindow(LiveWindow window, TimeSpan timeout)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (window.WindowHandle == 0 && clock.Elapsed < timeout) Thread.Sleep(5);
        return window.WindowHandle;
    }
}
