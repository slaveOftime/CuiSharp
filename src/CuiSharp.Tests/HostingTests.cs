using CuiSharp.Hosting;

namespace CuiSharp.Tests;

/// <summary>
/// The hosting layer's two promises: a mutation never blocks the frame and always
/// settles, and a component that throws cannot take the window down with it.
/// </summary>
[Collection("ProcessWindowState")]
public class HostingTests
{
    private sealed class SlowWork
    {
        private readonly TaskCompletionSource<int> _done = new();

        public Task<int> Task => _done.Task;

        public Mutation<int> Create() => new(_ => _done.Task);

        public void Finish(int value) => _done.SetResult(value);
    }

    [Fact]
    public void MutationRunsWithoutBlockingAndPublishesOnALaterCall()
    {
        var work = new SlowWork();
        var mutation = work.Create();

        Assert.False(mutation.TryPublish(out _));
        Assert.Equal(0, Component.PendingOperations);

        mutation.Run();
        Assert.True(mutation.IsRunning);
        Assert.Equal(1, Component.PendingOperations);

        // Re-entry must not start the work twice or double-count it.
        mutation.Run();
        Assert.Equal(1, Component.PendingOperations);

        Assert.False(mutation.TryPublish(out _));   // still running

        work.Finish(42);
        SpinUntil(() => !mutation.IsRunning && Component.PendingOperations == 0);
        Assert.True(mutation.TryPublish(out var result));
        Assert.Equal(42, result);
        Assert.Equal(0, Component.PendingOperations);
        Assert.False(mutation.IsRunning);

        mutation.Dispose();
    }

    [Fact]
    public void AFailedMutationSettlesAndReportsItsError()
    {
        var mutation = new Mutation<int>(_ => System.Threading.Tasks.Task.FromException<int>(new InvalidOperationException("boom")));
        mutation.Run();

        SpinUntil(() => mutation.Error is not null);
        Assert.False(mutation.TryPublish(out _));
        Assert.Equal("boom", mutation.Error?.Message);
        Assert.Equal(0, Component.PendingOperations);

        mutation.Dispose();
    }

    private sealed class Probe : Component
    {
        protected override void Draw(Ui ui) { }
    }

    private sealed class StreamWork
    {
        private readonly TaskCompletionSource _done = new();
        private Action<Snapshot>? _publish;

        public Task Task => _done.Task;

        public bool Connected => Volatile.Read(ref _publish) is not null;

        public void Connect(Action<Snapshot> publish) => Volatile.Write(ref _publish, publish);

        public void Publish(int value) => Volatile.Read(ref _publish)?.Invoke(new Snapshot(value));

        public void Finish() => _done.TrySetResult();
    }

    private sealed record Snapshot(int Value);

    [Fact]
    public void AProgressivePublishesWhileItRunsAndKeepsTheLastValue()
    {
        var work = new StreamWork();
        using var progressive = new Progressive<Snapshot>();

        progressive.Run((_, publish) =>
        {
            work.Connect(publish);
            return work.Task;
        });

        Assert.True(progressive.IsRunning);
        Assert.Equal(1, Component.PendingOperations);
        Assert.Null(progressive.Latest);

        SpinUntil(() => work.Connected);

        work.Publish(1);
        SpinUntil(() => progressive.Latest?.Value == 1);
        Assert.True(progressive.IsRunning);

        work.Publish(2);
        SpinUntil(() => progressive.Latest?.Value == 2);

        work.Finish();
        SpinUntil(() => !progressive.IsRunning);
        Assert.Equal(0, Component.PendingOperations);

        // The frame that shows the last report is still owed after the work ends; the run does
        // not clear the value the component is about to draw.
        Assert.Equal(2, progressive.Latest?.Value);
        Assert.True(PendingWork.Due);
        PendingWork.MarkDrawn();
        Assert.False(PendingWork.Due);
    }

    [Fact]
    public void AProgressiveBuiltWithWorkCanRunCancelAndRestart()
    {
        int runs = 0;
        using var progressive = new Progressive<Snapshot>((_, publish) =>
        {
            publish(new Snapshot(Interlocked.Increment(ref runs)));
            return System.Threading.Tasks.Task.CompletedTask;
        });

        progressive.Run();
        SpinUntil(() => !progressive.IsRunning);
        Assert.Equal(1, progressive.Latest?.Value);

        progressive.Cancel();
        Assert.Null(progressive.Latest);
        Assert.Null(progressive.Error);
        Assert.False(progressive.IsRunning);

        progressive.Restart();
        SpinUntil(() => !progressive.IsRunning);
        Assert.Equal(2, progressive.Latest?.Value);
    }

    [Fact]
    public void AnInitialSnapshotIsVisibleBeforeTheProducerReports()
    {
        var work = new StreamWork();
        using var progressive = new Progressive<Snapshot>();

        progressive.Restart((_, publish) =>
        {
            work.Connect(publish);
            return work.Task;
        }, initial: new Snapshot(0));

        // The frame that started the run sees the seed immediately, before the pool thread runs.
        Assert.Equal(0, progressive.Latest?.Value);

        SpinUntil(() => work.Connected);
        work.Publish(5);
        SpinUntil(() => progressive.Latest?.Value == 5);

        work.Finish();
        SpinUntil(() => !progressive.IsRunning);
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void AnInvalidFrameRateIsRejected(float rate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Progressive<Snapshot> { FrameRate = rate });
    }

    [Fact]
    public void ARunIsIgnoredWhileProgressiveWorkIsInFlight()
    {
        var work = new StreamWork();
        int starts = 0;
        using var progressive = new Progressive<Snapshot>();

        progressive.Run((_, _) =>
        {
            Interlocked.Increment(ref starts);
            return work.Task;
        });

        SpinUntil(() => starts == 1);
        Assert.Equal(1, Component.PendingOperations);

        // A second click during the run must not start another producer or double-count it.
        progressive.Run((_, _) =>
        {
            Interlocked.Increment(ref starts);
            return work.Task;
        });

        Thread.Sleep(50);
        Assert.Equal(1, starts);
        Assert.Equal(1, Component.PendingOperations);

        work.Finish();
        SpinUntil(() => !progressive.IsRunning);
        Assert.Equal(0, Component.PendingOperations);
    }

    [Fact]
    public void ARestartedProgressiveDropsThePreviousRunsReports()
    {
        var first = new StreamWork();
        var second = new StreamWork();
        using var progressive = new Progressive<Snapshot> { FrameRate = 5 };

        progressive.Run((_, publish) =>
        {
            first.Connect(publish);
            return first.Task;
        });

        SpinUntil(() => first.Connected);
        PendingWork.MarkDrawn();

        progressive.Restart((_, publish) =>
        {
            second.Connect(publish);
            return second.Task;
        });

        // Superseding a run must not claim a completion frame: the new run still wants its own
        // throttled cadence rather than an immediate redraw.
        Assert.False(PendingWork.Due);
        Assert.InRange(PendingWork.NextFrameSeconds, 0.19f, 0.21f);

        SpinUntil(() => second.Connected);
        Assert.Equal(1, Component.PendingOperations);

        // The work the app moved on from finished late and reported: it must not land in the
        // state the new run owns.
        first.Publish(1);
        Thread.Sleep(50);
        Assert.Null(progressive.Latest);

        second.Publish(2);
        SpinUntil(() => progressive.Latest?.Value == 2);
        first.Finish();

        Thread.Sleep(50);
        Assert.Equal(2, progressive.Latest?.Value);
        Assert.True(progressive.IsRunning);

        second.Finish();
        SpinUntil(() => !progressive.IsRunning);
        Assert.Null(progressive.Error);
        Assert.Equal(0, Component.PendingOperations);
    }

    [Fact]
    public void ADisposedProgressiveCannotPublishOrKeepTheLoopAwake()
    {
        var work = new StreamWork();
        var progressive = new Progressive<Snapshot>();

        progressive.Run((_, publish) =>
        {
            work.Connect(publish);
            return work.Task;
        });

        SpinUntil(() => work.Connected);
        work.Publish(1);
        SpinUntil(() => progressive.Latest?.Value == 1);

        progressive.Dispose();
        Assert.Null(progressive.Latest);
        Assert.False(progressive.IsRunning);
        Assert.Equal(0, Component.PendingOperations);

        work.Publish(2);
        work.Finish();
        Thread.Sleep(50);
        Assert.Null(progressive.Latest);
        Assert.Equal(0, Component.PendingOperations);
    }

    [Fact]
    public void AFailedProgressiveSettlesAndReportsItsError()
    {
        using var progressive = new Progressive<Snapshot>();
        progressive.Run((_, _) => throw new InvalidOperationException("boom"));

        SpinUntil(() => progressive.Error is not null);

        Assert.Equal("boom", progressive.Error?.Message);
        Assert.Null(progressive.Latest);
        Assert.False(progressive.IsRunning);
        Assert.Equal(0, Component.PendingOperations);
    }

    [Fact]
    public void ACancelledProgressiveIsNotAFailure()
    {
        var started = new TaskCompletionSource();
        var progressive = new Progressive<Snapshot>();

        progressive.Run(async (token, _) =>
        {
            started.TrySetResult();
            await System.Threading.Tasks.Task.Delay(Timeout.Infinite, token);
        });

        SpinUntil(() => started.Task.IsCompleted);
        progressive.Dispose();

        SpinUntil(() => !progressive.IsRunning);
        Assert.Null(progressive.Error);
        Assert.Equal(0, Component.PendingOperations);
    }

    [Fact]
    public void ProgressiveWorkRunsOffTheFrameThread()
    {
        int frameThread = Environment.CurrentManagedThreadId;
        int workThread = 0;
        using var progressive = new Progressive<Snapshot>();

        progressive.Run((_, _) =>
        {
            workThread = Environment.CurrentManagedThreadId;
            return System.Threading.Tasks.Task.CompletedTask;
        });

        SpinUntil(() => workThread != 0);
        Assert.NotEqual(frameThread, workThread);
    }

    [Fact]
    public void FrameRateThrottlesTheWakeUp()
    {
        var work = new StreamWork();
        using var progressive = new Progressive<Snapshot> { FrameRate = 4 };

        progressive.Run((_, _) => work.Task);
        Assert.InRange(PendingWork.NextFrameSeconds, 0.24f, 0.26f);

        work.Finish();
        SpinUntil(() => !progressive.IsRunning);
    }

    [Fact]
    public void ACuiAppFrameAppliesFinishedWork()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = CuiApp.Create(new CuiAppOptions
        {
            Title = "HostFrame",
            Width = 320,
            Height = 240,
            Hidden = true,
            Renderer = Renderer.Software,
        });
        Assert.NotNull(app);
        Assert.True(app.Frame());

        int applied = 0;
        using var mutation = new Mutation<int>(_ => System.Threading.Tasks.Task.FromResult(7), value => applied = value);
        mutation.Run();
        SpinUntil(() => !mutation.IsRunning && Component.PendingOperations == 0);

        // The manual loop has no callback of its own, so this is the frame that must drain.
        Assert.True(app.Frame());
        Assert.Equal(7, applied);
    }

    [Fact]
    public void ACuiAppRunAppliesFinishedWork()
    {
        if (!OperatingSystem.IsWindows()) return;

        int applied = 0;
        bool started = false;
        bool closed = false;
        CuiWindow? window = null;
        using var mutation = new Mutation<int>(_ => System.Threading.Tasks.Task.FromResult(7), value => applied = value);
        using var watchdog = new Timer(_ =>
        {
            try
            {
                window?.RequestClose();
            }
            catch
            {
                // The window was already gone; the loop is about to return.
            }
        }, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);

        int code = CuiApp.Run(new CuiAppOptions
        {
            Title = "HostRun",
            Width = 320,
            Height = 240,
            Hidden = true,
            Renderer = Renderer.Software,
            WindowCreated = created => window = created,
        }, _ =>
        {
            if (!started)
            {
                started = true;
                mutation.Run();
            }

            if (applied == 7 && !closed)
            {
                closed = true;
                window!.RequestClose();
            }
        });

        Assert.Equal(0, code);
        Assert.Equal(7, applied);
    }


    [Fact]
    public void AFinishedMutationDropsPendingWorkEvenWhenNobodyPublishes()
    {
        // The regression: a component that is no longer drawn (the user switched
        // tabs) never polls, so counting work until TryPublish pinned the window at
        // the frame rate forever.
        var work = new SlowWork();
        var mutation = work.Create();
        mutation.Run();
        Assert.Equal(1, Component.PendingOperations);

        work.Finish(1);
        SpinUntil(() => Component.PendingOperations == 0);

        // The frame that shows the result is still requested exactly once.
        Assert.True(PendingWork.Any);
        PendingWork.MarkDrawn();
        Assert.False(PendingWork.Any);

        mutation.Dispose();
    }

    [Fact]
    public void AVoidMutationReportsItsErrorWithoutBeingPolled()
    {
        var mutation = new Mutation(_ => System.Threading.Tasks.Task.FromException(new InvalidOperationException("nope")));
        mutation.Run();

        // The failure lands on the completing thread, so `Error` is what to wait for: a frame that
        // reads it is never a frame too early.
        SpinUntil(() => mutation.Error is not null);

        // No TryPublish in Draw needed: Error is current as soon as the work ends.
        Assert.Equal("nope", mutation.Error?.Message);
        Assert.Equal(0, Component.PendingOperations);

        // Running again clears the previous failure, then reports it again.
        mutation.Run();
        SpinUntil(() => mutation.Error is not null);
        Assert.Equal("nope", mutation.Error?.Message);   // same faulting delegate, reported again
        mutation.Dispose();
    }

    [Fact]
    public void AMutationThatThrowsSynchronouslyDoesNotEscapeTheFrame()
    {
        // "Synchronously" is about the delegate, not the thread: the throw happens in the
        // work's own synchronous prefix, which now runs on the pool, so the frame never sees it.
        var mutation = new Mutation(_ => throw new InvalidOperationException("sync boom"));
        mutation.Run();

        SpinUntil(() => mutation.Error is not null);
        Assert.Equal("sync boom", mutation.Error?.Message);
        Assert.False(mutation.IsRunning);
        Assert.Equal(0, Component.PendingOperations);

        mutation.Dispose();
    }

    [Fact]
    public void TheWorksSynchronousPrefixRunsOffTheFrameThread()
    {
        // The docs promise the work runs on the pool. An `async` method runs its prefix on
        // whatever thread called it, so invoking `_work` inline put that prefix — a file read
        // before the first await, a lock, a parse — inside Draw, and the frame waited for it.
        int frameThread = Environment.CurrentManagedThreadId;
        int workThread = 0;
        var mutation = new Mutation<int>(_ =>
        {
            workThread = Environment.CurrentManagedThreadId;
            return System.Threading.Tasks.Task.FromResult(1);
        });

        mutation.Run();
        SpinUntil(() => workThread != 0);

        Assert.NotEqual(frameThread, workThread);
        mutation.Dispose();
    }

    [Fact]
    public void ASlowSynchronousPrefixInTheWorkDoesNotHoldUpRun()
    {
        // Run is called from Draw, so anything it does inline is frame time.
        var mutation = new Mutation<int>(_ =>
        {
            Thread.Sleep(300);
            return System.Threading.Tasks.Task.FromResult(1);
        });

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        mutation.Run();
        elapsed.Stop();

        Assert.True(elapsed.ElapsedMilliseconds < 150, $"Run blocked the frame for {elapsed.ElapsedMilliseconds} ms");
        mutation.Dispose();
    }

    [Fact]
    public void ADisposedMutationCannotApplyItsResult()
    {
        // The regression: a page is torn down while its work is in flight. The work finishes,
        // the completion is already queued, and the next frame applies it into a component that
        // no longer exists — or, worse, resurrects one the user navigated away from.
        var applied = new List<int>();
        var work = new SlowWork();
        var mutation = new Mutation<int>(_ => work.Task, applied.Add);

        mutation.Run();
        mutation.Dispose();
        work.Finish(7);

        Completions.Drain();
        Assert.Empty(applied);
        Assert.Equal(0, Component.PendingOperations);
    }

    [Fact]
    public void ACancelledWorkCannotApplyItsResult()
    {
        var applied = new List<int>();
        var started = new TaskCompletionSource();
        var mutation = new Mutation<int>(async token =>
        {
            started.TrySetResult();
            await System.Threading.Tasks.Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return 3;
        }, applied.Add);

        mutation.Run();
        SpinUntil(() => started.Task.IsCompleted);
        mutation.Dispose();

        SpinUntil(() => !mutation.IsRunning && Component.PendingOperations == 0);
        Completions.Drain();
        Assert.Empty(applied);
        Assert.Null(mutation.Error);   // a cancelled run is not a failure
    }

    [Fact]
    public void ACompletedOperationDoesNotOverwriteANewerOne()
    {
        // Run #1 completes and queues its apply. Run #2 starts before that apply is drained.
        // Applying #1 would publish stale state and clear the pending work of #2.
        var applied = new List<int>();
        var appliedLate = new List<bool>();
        var first = new SlowWork();
        int run = 0;
        var second = new TaskCompletionSource<int>();
        var mutation = new Mutation<int>(
            _ => ++run == 1 ? first.Task : second.Task,
            value =>
            {
                applied.Add(value);
                appliedLate.Add(value == 1 && Volatile.Read(ref run) > 1);
            });

        mutation.Run();
        first.Finish(1);
        SpinUntil(() => Component.PendingOperations == 0);

        mutation.Run();   // a second operation, before the frame that would have applied #1
        Assert.True(mutation.IsRunning);

        Completions.Drain();
        Assert.DoesNotContain(true, appliedLate);   // #1 never lands after #2 started
        Assert.True(mutation.IsRunning);            // #2 still owns this mutation

        second.SetResult(2);
        SpinUntil(() => { Completions.Drain(); return applied.Contains(2); });
        Assert.Equal(2, applied[^1]);
        Assert.DoesNotContain(true, appliedLate);
    }

    [Fact]
    public void AFrameOnAnotherThreadDoesNotApplyThisThreadsResult()
    {
        // The mutation is constructed on this thread, as DI can do before the window starts, but
        // Run and the frame loop belong to the UI thread. The completion must follow Run's thread,
        // not the constructor's, and a frame on the constructor thread must not apply it.
        int constructedOn = Environment.CurrentManagedThreadId;
        int pendingBefore = Component.PendingOperations;
        var applied = new List<int>();
        var work = new SlowWork();
        using var mutation = new Mutation<int>(_ => work.Task, applied.Add);
        using var ready = new ManualResetEventSlim();
        using var allowDrain = new ManualResetEventSlim();
        Exception? uiError = null;
        int runOn = 0;

        var ui = new Thread(() =>
        {
            try
            {
                runOn = Environment.CurrentManagedThreadId;
                mutation.Run();
                ready.Set();
                if (!allowDrain.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("the test never released the UI frame");
                Completions.Drain();
            }
            catch (Exception error)
            {
                uiError = error;
                ready.Set();
            }
        });
        ui.Start();

        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(10)), "the UI thread never started the mutation");
            Assert.Null(uiError);
            Assert.NotEqual(constructedOn, runOn);

            work.Finish(7);
            SpinUntil(() => Component.PendingOperations == pendingBefore);

            // A frame on the construction thread is not the owner of this result.
            Completions.Drain();
            Assert.Empty(applied);
        }
        finally
        {
            allowDrain.Set();
            Assert.True(ui.Join(TimeSpan.FromSeconds(10)), "the UI thread never finished its frame");
        }

        Assert.Null(uiError);
        Assert.Equal([7], applied);
    }

    [Fact]
    public void RepeatedOperationsSettlePendingWorkExactly()
    {
        // A stale completion that settles the newest run's count leaks a pending frame for the
        // life of the process: the window stays awake at the frame rate with nothing to draw.
        var applied = new List<int>();
        int next = 0;
        var mutation = new Mutation<int>(_ => System.Threading.Tasks.Task.FromResult(++next), applied.Add);

        for (var i = 0; i < 32; i++)
        {
            mutation.Run();
            SpinUntil(() => Component.PendingOperations == 0);
            Completions.Drain();          // the frame that applies the result
            PendingWork.MarkDrawn();
        }

        Assert.Equal(0, Component.PendingOperations);
        Assert.Equal(32, applied.Count);
        mutation.Dispose();
    }

    [Fact]
    public void DisposingAComponentTwiceIsSafe()
    {
        var probe = new Probe();
        probe.Dispose();
        probe.Dispose();
        Assert.Equal("Probe", probe.Title);
    }

    private sealed class Allocating : Component
    {
        protected override void Draw(Ui ui)
        {
            // The shape of a real regression: a string built per frame.
            string text = new string('x', 8) + FrameStats.Frames;
            ui.Text(text);
        }
    }

    [Fact]
    public void FrameStatsReportTheCostOfTheFrameThatDrew()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(240, 160);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        var quiet = new Probe();
        var noisy = new Allocating();

        // A surface fills these in by itself, so a test and a window report the same numbers.
        long frames = surface.LastFrame.Frames;
        surface.Draw(quiet.Render, pixels);
        Assert.True(surface.LastFrame.Frames > frames);

        // A component that allocates nothing must measure zero...
        surface.Draw(quiet.Render, pixels);
        Assert.Equal(0, surface.LastFrame.AllocatedBytes);

        // ...and one that allocates must be caught by the same number.
        surface.Draw(noisy.Render, pixels);
        Assert.True(surface.LastFrame.AllocatedBytes > 0);
    }

    private sealed class Asleep : Component
    {
        protected override void Draw(Ui ui) => ui.Button("Save");
    }

    private sealed class Live : Component
    {
        protected override bool WantsFrames => true;

        protected override void Draw(Ui ui) => ui.Button("Save");
    }

    [Fact]
    public void AnIdleFrameReportsNoFrameRateAndALiveOneDoes()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(240, 160);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];

        // CUI sleeps when nothing asks for a frame, so "fps" would describe a rate that has
        // nothing to do with the app: a footer says idle instead.
        var asleep = new Asleep();
        surface.Draw(asleep.Render, pixels);
        Assert.False(surface.LastFrame.IsLive);

        var live = new Live();
        for (var i = 0; i < 4; i++) surface.Draw(live.Render, pixels);
        Assert.True(surface.LastFrame.IsLive);
        Assert.True(surface.LastFrame.Fps > 0);

        // Returning to a still component resets the per-frame flag on the surface too.
        surface.Draw(asleep.Render, pixels);
        Assert.False(surface.LastFrame.IsLive);

        asleep.Dispose();
        live.Dispose();
    }

    [Fact]
    public void TheThemeIsReadAndWrittenFromInsideAFrame()
    {
        using var surface = CuiSurface.Create(200, 120);
        Ui ui = surface.Ui;   // the frame context, the same value Draw receives

        Assert.Equal(Theme.Light, ui.Theme);

        ui.Theme = Theme.Dark;
        Assert.Equal(Theme.Dark, ui.Theme);

        // What a Ctrl+J command is: no state of the app's own, one call.
        ui.ToggleTheme();
        Assert.Equal(Theme.Light, ui.Theme);

        ui.Accent = Accent.Rose;
        Assert.Equal(Accent.Rose, ui.Accent);
    }

    private sealed class Exploding : Component
    {
        protected override void Draw(Ui ui) => throw new InvalidOperationException("component failed");
    }

    [Fact]
    public void AThrowingComponentIsContainedByItsErrorBoundary()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(320, 200);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        var exploding = new Exploding();

        // No exception escapes: the frame is drawn with an inline error instead,
        // and the failure is recorded so a test cannot mistake it for a success.
        surface.Draw(exploding.Render, pixels);

        Assert.Contains(pixels, b => b != 0);
        Assert.Single(surface.Faults);
        Assert.Equal("component failed", surface.Faults[0].Message);
        exploding.Dispose();
    }

    private static void SpinUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) Thread.Sleep(10);
        Assert.True(condition());
    }
}
