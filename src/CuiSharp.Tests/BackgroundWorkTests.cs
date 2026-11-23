using CuiSharp.Hosting;

namespace CuiSharp.Tests;

[Collection("ProcessWindowState")]
public class BackgroundWorkTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private sealed class Owner : IDisposable
    {
        private readonly UiFrameState _frame = new();

        internal float NextFrameSeconds => _frame.Work?.NextFrameSeconds ?? -1f;

        internal void Draw(Action? draw = null)
        {
            using (_frame.EnterDraw(0, window: false)) draw?.Invoke();
        }

        public void Dispose() => _frame.CloseWork();
    }

    private sealed record Snapshot(int Value);

    [Fact]
    public void AWorkerDoesNotOwnTheFrameAndOnlyItsOwnerAppliesTheResult()
    {
        using var owner = new Owner();
        using var other = new Owner();
        var finish = new TaskCompletionSource<int>();
        var workerThread = new TaskCompletionSource<int>();
        var applied = new List<(int Value, int Thread)>();
        using var mutation = new Mutation<int>(_ =>
        {
            workerThread.SetResult(Environment.CurrentManagedThreadId);
            return finish.Task;
        }, value => applied.Add((value, Environment.CurrentManagedThreadId)));

        int frameThread = Environment.CurrentManagedThreadId;
        owner.Draw(mutation.Run);
        Assert.True(mutation.IsRunning);
        Assert.NotEqual(frameThread, Completed(workerThread.Task));

        other.Draw();
        Assert.Equal(-1f, other.NextFrameSeconds);
        finish.SetResult(7);
        Until(() => !mutation.IsRunning);
        Assert.Equal(7, mutation.Latest);
        Assert.Equal(0, Component.PendingOperations);
        Assert.Equal(0f, owner.NextFrameSeconds);
        other.Draw();
        Assert.Empty(applied);
        Assert.Equal(0f, owner.NextFrameSeconds);

        owner.Draw(() => Assert.Equal([(7, frameThread)], applied));
        owner.Draw();
        Assert.Single(applied);
        Assert.Equal(-1f, owner.NextFrameSeconds);
    }

    [Fact]
    public void AnOperationRequiresItsOwningFrameToStartAndItsThreadToControlIt()
    {
        using var owner = new Owner();
        using var other = new Owner();
        using var mutation = new Mutation(_ => Task.CompletedTask);

        Assert.Throws<InvalidOperationException>(mutation.Run);
        owner.Draw(mutation.Run);
        Until(() => !mutation.IsRunning);
        other.Draw(() => Assert.Throws<InvalidOperationException>(mutation.Restart));
        Exception? crossThread = null;
        var otherThread = new Thread(() => crossThread = Record.Exception(mutation.Cancel)) { IsBackground = true };
        otherThread.Start();
        Assert.True(otherThread.Join(Timeout));
        Assert.IsType<InvalidOperationException>(crossThread);
        owner.Draw(mutation.Restart);
        Until(() => !mutation.IsRunning);
    }

    [Fact]
    public void AReplacementDropsAQueuedApplyAndKeepsThePreviousSuccessfulResult()
    {
        using var owner = new Owner();
        var second = new TaskCompletionSource<int>();
        var applied = new List<int>();
        using var mutation = new Mutation<int>(applied.Add);
        owner.Draw(() =>
        {
            mutation.Run(_ => Task.FromResult(1));
            Until(() => !mutation.IsRunning);
            mutation.Restart(_ => second.Task);
            Assert.Equal(1, mutation.Latest);
        });

        owner.Draw();
        Assert.Empty(applied);
        Assert.True(mutation.IsRunning);
        second.SetResult(2);
        Until(() => !mutation.IsRunning);
        owner.Draw(() => Assert.Equal([2], applied));
    }

    [Fact]
    public void ACompletionCanStartAnotherRunButDoesNotApplyItInTheSameFrame()
    {
        using var owner = new Owner();
        var applied = new List<int>();
        bool runningAtApply = true;
        Mutation<int>? operation = null;
        using var mutation = new Mutation<int>(value =>
        {
            applied.Add(value);
            if (value != 1) return;
            runningAtApply = operation!.IsRunning;
            operation.Run(_ => Task.FromResult(2));
            // Force the second completion to arrive while this frame's batch is in progress.
            Until(() => !operation.IsRunning);
        });
        operation = mutation;
        owner.Draw(() => mutation.Run(_ => Task.FromResult(1)));
        Until(() => !mutation.IsRunning);
        owner.Draw();
        Assert.False(runningAtApply);
        Assert.Equal([1], applied);
        owner.Draw();
        Assert.Equal([1, 2], applied);
        Assert.Null(mutation.Error);
    }

    [Fact]
    public void AnApplyThatCancelsASiblingInTheSameBatchPreventsItsApply()
    {
        using var owner = new Owner();
        var applied = new List<int>();
        using var second = new Mutation<int>(_ => Task.FromResult(2), applied.Add);
        using var first = new Mutation(_ => Task.CompletedTask, () =>
        {
            applied.Add(1);
            second.Cancel();
        });

        owner.Draw(() =>
        {
            first.Run();
            Until(() => !first.IsRunning);
            second.Run();
            Until(() => !second.IsRunning);
        });

        owner.Draw();
        Assert.Equal([1], applied);
        Assert.Equal(0, second.Latest);
    }

    [Fact]
    public void ClosingTheOwnerDropsQueuedApplyAndOutstandingRedrawDemand()
    {
        using var owner = new Owner();
        using var other = new Owner();
        var release = new TaskCompletionSource<int>();
        var applied = new List<int>();
        using var pending = new Mutation<int>(_ => release.Task, applied.Add);
        using var finished = new Mutation<int>(_ => Task.FromResult(2), applied.Add);

        owner.Draw(() => { pending.Run(); finished.Run(); });
        Until(() => !finished.IsRunning);
        Assert.Equal(1, Component.PendingOperations);
        owner.Dispose();
        Assert.Equal(0, Component.PendingOperations);
        Assert.Equal(-1f, owner.NextFrameSeconds);
        release.SetResult(1);
        Until(() => !pending.IsRunning);
        other.Draw();
        Assert.Empty(applied);
    }

    [Fact]
    public void CancelDetachesImmediatelyEvenWhenACallbackBlocksAndThrows()
    {
        using var owner = new Owner();
        using var registered = new ManualResetEventSlim();
        var callbackThread = new TaskCompletionSource<int>();
        var releaseCallback = new TaskCompletionSource();
        var finishWorker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerSawCancellation = new TaskCompletionSource<bool>();
        using var mutation = new Mutation<int>(async token =>
        {
            using var registration = token.Register(() =>
            {
                callbackThread.SetResult(Environment.CurrentManagedThreadId);
                if (!releaseCallback.Task.Wait(Timeout)) throw new TimeoutException();
                throw new InvalidOperationException("failure in abandoned callback");
            });
            registered.Set();
            await finishWorker.Task;
            workerSawCancellation.SetResult(token.WaitHandle.WaitOne(0));
            return 1;
        });

        owner.Draw(mutation.Run);
        Assert.True(registered.Wait(Timeout));
        try
        {
            mutation.Cancel();
            Assert.False(mutation.IsRunning);
            Assert.Equal(0, Component.PendingOperations);
            Assert.Equal(-1f, owner.NextFrameSeconds);
            Assert.NotEqual(Environment.CurrentManagedThreadId, Completed(callbackThread.Task));

            // The old worker can still use its token, but cannot publish or stop the new run.
            owner.Draw(() => mutation.Run(_ => Task.FromResult(2)));
            finishWorker.SetResult();
            Assert.True(Completed(workerSawCancellation.Task));
            Until(() => !mutation.IsRunning);
            Assert.Equal(2, mutation.Latest);
            Assert.Null(mutation.Error);
        }
        finally
        {
            finishWorker.TrySetResult();
            releaseCallback.TrySetResult();
        }
    }

    [Fact]
    public void DisposeDropsAQueuedApplyAndPreventsFutureStarts()
    {
        using var owner = new Owner();
        int applied = 0;
        using var mutation = new Mutation<int>(_ => Task.FromResult(1), value => applied = value);
        owner.Draw(mutation.Run);
        Until(() => !mutation.IsRunning);
        mutation.Dispose();
        owner.Draw(mutation.Run);
        Assert.Equal(0, applied);
        Assert.Equal(0, mutation.Latest);
        Assert.False(mutation.IsRunning);
    }

    [Fact]
    public void AWorkerFailureIsReadableWithoutADrainAndANewRunClearsIt()
    {
        using var owner = new Owner();
        var release = new TaskCompletionSource();
        using var mutation = new Mutation(_ => throw new InvalidOperationException("worker failed"));
        owner.Draw(mutation.Run);
        Until(() => !mutation.IsRunning);
        Assert.Equal("worker failed", mutation.Error?.Message);
        owner.Draw(() => mutation.Run(_ => release.Task));
        Assert.Null(mutation.Error);
        release.SetResult();
        Until(() => !mutation.IsRunning);
        Assert.Null(mutation.Error);
    }

    [Fact]
    public void IndependentlyCancelledTasksAreNotFailuresForEitherKindOfWork()
    {
        using var owner = new Owner();
        using var mutation = new Mutation<int>(_ => Task.FromCanceled<int>(new CancellationToken(true)));
        using var progressive = new Progressive<Snapshot>((_, _) => Task.FromCanceled(new CancellationToken(true)));
        owner.Draw(() => { mutation.Run(); progressive.Run(); });
        Until(() => !mutation.IsRunning && !progressive.IsRunning);
        Assert.Null(mutation.Error);
        Assert.Null(progressive.Error);
        Assert.Equal(0, Component.PendingOperations);
    }

    [Fact]
    public void ProgressiveKeepsOnlyTheLatestSnapshotAndIgnoresReportsAfterTheRun()
    {
        using var owner = new Owner();
        var connected = new TaskCompletionSource<Action<Snapshot>>();
        var stop = new TaskCompletionSource();
        using var progressive = new Progressive<Snapshot> { FrameRate = 5 };
        owner.Draw(() => progressive.Run((_, publish) =>
        {
            connected.SetResult(publish);
            return stop.Task;
        }, initial: new Snapshot(0)));
        Assert.Equal(0, progressive.Latest?.Value);
        Assert.InRange(owner.NextFrameSeconds, 0.19f, 0.21f);
        Action<Snapshot> report = Completed(connected.Task);
        report(new Snapshot(1));
        report(new Snapshot(2));
        Assert.Equal(2, progressive.Latest?.Value);
        Assert.True(progressive.IsRunning);

        stop.SetResult();
        Until(() => !progressive.IsRunning);
        Assert.Equal(0f, owner.NextFrameSeconds);
        report(new Snapshot(3));
        Assert.Equal(2, progressive.Latest?.Value);
        owner.Draw();
        Assert.Equal(-1f, owner.NextFrameSeconds);
    }

    [Fact]
    public void RestartDropsTheOldProducerImmediatelyAndRunDoesNotStartAnotherWhileActive()
    {
        using var owner = new Owner();
        var first = new TaskCompletionSource<Action<Snapshot>>();
        var releaseFirst = new TaskCompletionSource();
        var second = new TaskCompletionSource<Action<Snapshot>>();
        var releaseSecond = new TaskCompletionSource();
        using var progressive = new Progressive<Snapshot>((_, publish) =>
        {
            first.SetResult(publish);
            return releaseFirst.Task;
        })
        { FrameRate = 5 };

        owner.Draw(progressive.Run);
        Action<Snapshot> publishFirst = Completed(first.Task);
        owner.Draw(() => progressive.Restart((_, publish) =>
        {
            second.SetResult(publish);
            return releaseSecond.Task;
        }));
        Action<Snapshot> publishSecond = Completed(second.Task);
        owner.Draw(() => progressive.Run((_, _) => throw new InvalidOperationException("should not run")));
        Assert.InRange(owner.NextFrameSeconds, 0.19f, 0.21f);
        Assert.Equal(1, Component.PendingOperations);
        publishFirst(new Snapshot(1));
        Assert.Null(progressive.Latest);
        releaseFirst.SetException(new InvalidOperationException("old producer"));
        publishSecond(new Snapshot(2));
        releaseSecond.SetResult();
        Until(() => !progressive.IsRunning);
        Assert.Equal(2, progressive.Latest?.Value);
        Assert.Null(progressive.Error);
        progressive.Cancel();
        Assert.Null(progressive.Latest);
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void AnInvalidFrameRateIsRejected(float rate) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Progressive<Snapshot> { FrameRate = rate });

    // The test must stay on one frame thread; the task is signalled independently by the worker.
    private static T Completed<T>(Task<T> task)
    {
        Until(() => task.IsCompleted);
        return task.GetAwaiter().GetResult();
    }

    private static void Until(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, Timeout));
}
