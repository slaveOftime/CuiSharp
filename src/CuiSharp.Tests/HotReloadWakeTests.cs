using CuiSharp.Hosting;
using CuiSharp.Interop;

namespace CuiSharp.Tests;

/// <summary>
/// A window must draw the frame a hot reload invalidates without the user touching it, and must
/// still draw nothing when nothing reloaded. The wake is observed the way a user observes it — on
/// the frame counter of a real <see cref="CuiApp.Run"/> window that no input ever reaches — and
/// the notification is delivered through <see cref="HotReloadWake.UpdateApplication"/>, the method
/// the runtime itself calls.
/// </summary>
public class HotReloadWakeTests
{
    private static bool CanOpenWindow => OperatingSystem.IsWindows();

    [Fact]
    public void AReloadNotificationDrawsAFrameWithoutAnyInput()
    {
        if (!CanOpenWindow) return;

        using var window = new IdleWindow();
        int idle = window.WaitUntilIdle(TimeSpan.FromSeconds(30));

        // Nothing animates, nothing is clicked and nothing is reloaded: the loop must sleep. A
        // window that kept drawing here would be polling, which is what the wake exists to avoid.
        Thread.Sleep(TimeSpan.FromSeconds(2));
        int idleDrift = window.Frames - idle;
        Assert.True(idleDrift <= 1, $"an idle window drew {idleDrift} frames in two seconds — it is polling, not sleeping");

        long reloads = HotReloadWake.Reloads;
        HotReloadWake.UpdateApplication(null);

        Assert.True(window.WaitForFrames(idle + 1, TimeSpan.FromSeconds(10)),
            "the reload notification did not produce a frame: a reloaded UI would stay invisible until the user moved the mouse");
        Assert.True(HotReloadWake.Reloads > reloads, "the notification was not counted as a reload");
    }

    [Fact]
    public void AWindowsWindowNeverPollsEvenUnderAWatchSession()
    {
        if (!CanOpenWindow) return;

        using var surface = CuiSurface.Create(160, 120);
        surface.Draw(_ => { }, new byte[160 * 120 * 4]);

        WithWatchSession(watch: true, () =>
        {
            Assert.True(HotReloadWake.WatchSession);

            HotReloadWake.ForFrame(surface.Handle);

            // Windows is woken by the notification, so it must not also poll: the frame loop's
            // sleep stays unbounded and an idle developer window costs no CPU.
            Assert.True(Native.NextWakeup(surface.Handle) < 0f,
                "a watched window armed a wake-up it does not need on this platform");
        });
    }

    [Fact]
    public void APlainRunSleepsUntilInput()
    {
        if (!CanOpenWindow) return;

        using var surface = CuiSurface.Create(160, 120);
        surface.Draw(_ => { }, new byte[160 * 120 * 4]);

        WithWatchSession(watch: false, () =>
        {
            Assert.False(HotReloadWake.WatchSession);

            HotReloadWake.ForFrame(surface.Handle);

            Assert.True(Native.NextWakeup(surface.Handle) < 0f,
                "a plain window armed a wake-up: an idle app would pay for a feature it cannot use");
        });
    }

    [Theory]
    [InlineData(true, false, true)]    // watching, and the loop cannot be woken: poll
    [InlineData(true, true, false)]    // watching, and it can be: the reload wakes it
    [InlineData(false, false, false)]  // not watching: sleep until something real happens
    [InlineData(false, true, false)]
    public void TheHeartbeatOnlyArmsWhereTheLoopCannotBeWoken(bool watchSession, bool windows, bool expected) =>
        Assert.Equal(expected, HotReloadWake.HeartbeatArmedFor(watchSession, windows));

    /// <summary>
    /// Runs the check with <c>DOTNET_WATCH</c> forced either way. The variable is what
    /// <c>dotnet watch</c> itself sets, and the answer is cached like the process would cache it,
    /// so both the variable and the cache are put back afterwards.
    /// </summary>
    private static void WithWatchSession(bool watch, Action check)
    {
        var saved = Environment.GetEnvironmentVariable("DOTNET_WATCH");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_WATCH", watch ? "1" : null);
            HotReloadWake.Reset();
            check();
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_WATCH", saved);
            HotReloadWake.Reset();
        }
    }

    /// <summary>
    /// A real window on its own thread that draws a still page and counts its frames. It is
    /// hidden and no test sends it input, so every frame after the first is either a wake or the
    /// window polling on its own — the difference this file is about.
    /// </summary>
    private sealed class IdleWindow : IDisposable
    {
        private readonly Thread _thread;
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile Exception? _error;
        private nint _hwnd;
        private int _frames;

        internal IdleWindow()
        {
            _thread = new Thread(() =>
            {
                try
                {
                    CuiApp.Run(
                        new CuiAppOptions
                        {
                            Title = $"CuiSharp reload proof {Guid.NewGuid():N}"[..34],
                            Width = 420,
                            Height = 320,
                            Hidden = true,
                            WindowCreated = window => Volatile.Write(ref _hwnd, window.NativeWindow),
                        },
                        ui =>
                        {
                            ui.Heading("Reload proof", 2);
                            ui.Secondary("nothing animates here");
                            Interlocked.Increment(ref _frames);
                        });
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
        }

        internal int Frames => Volatile.Read(ref _frames);

        /// <summary>Waits for the loop to go quiet on its own, and answers the count it stopped at.</summary>
        internal int WaitUntilIdle(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                int seen = Frames;
                if (seen > 0)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(600));
                    if (Frames == seen) return seen;
                }
                else
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(50));
                }

                if (_closed.Task.IsCompleted)
                    throw new InvalidOperationException("The proof window closed before it settled.", _error);
            }

            throw new TimeoutException($"The proof window never settled; it drew {Frames} frames.");
        }

        internal bool WaitForFrames(int target, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (Frames >= target) return true;
                if (_closed.Task.IsCompleted) return Frames >= target;
                Thread.Sleep(TimeSpan.FromMilliseconds(20));
            }

            return Frames >= target;
        }

        public void Dispose()
        {
            if (!_closed.Task.IsCompleted)
            {
                nint hwnd = Volatile.Read(ref _hwnd);
                if (hwnd != 0) Win32.RequestClose(hwnd);
            }

            _closed.Task.Wait(TimeSpan.FromSeconds(15));
            _thread.Join(TimeSpan.FromSeconds(5));
            if (_error is not null)
                throw new InvalidOperationException("The proof window died before it closed.", _error);
        }
    }
}
