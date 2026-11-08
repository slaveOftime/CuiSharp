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

        SpinUntil(() => !mutation.IsRunning);

        // No TryPublish in Draw needed: Error is current as soon as the work ends.
        Assert.Equal("nope", mutation.Error?.Message);
        Assert.Equal(0, Component.PendingOperations);

        // Running again clears the previous failure.
        mutation.Run();
        SpinUntil(() => !mutation.IsRunning);
        Assert.Equal("nope", mutation.Error?.Message);   // same faulting delegate, reported again
        mutation.Dispose();
    }

    [Fact]
    public void AMutationThatThrowsSynchronouslyDoesNotEscapeTheFrame()
    {
        var mutation = new Mutation(_ => throw new InvalidOperationException("sync boom"));
        mutation.Run();

        Assert.Equal("sync boom", mutation.Error?.Message);
        Assert.False(mutation.IsRunning);
        Assert.Equal(0, Component.PendingOperations);

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
