using CuiSharp.Hosting;

namespace CuiSharp.Tests;

/// <summary>
/// The hosting layer's two promises: a mutation never blocks the frame and always
/// settles, and a component that throws cannot take the window down with it.
/// </summary>
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
        SpinUntil(() => !mutation.IsRunning);
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

        SpinUntil(() => !mutation.IsRunning);
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
