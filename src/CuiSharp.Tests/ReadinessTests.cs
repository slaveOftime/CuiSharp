using System.Diagnostics;
using CuiSharp.Hosting;

namespace CuiSharp.Tests;

/// <summary>
/// Readiness and bounded waiting. Readiness is one frame's claim that its app data is complete
/// (<c>ui.MarkReady</c>), and a wait is the loop a tool builds on that claim: draw real frames,
/// look between completed frames, stop by the wall clock. Every wait here releases on an
/// observable signal — a completion that drains at a frame boundary — never on an arbitrary
/// sleep.
/// </summary>
public class ReadinessTests
{
    /// <summary>A page whose frames mark themselves ready exactly while <see cref="Loaded"/> holds.</summary>
    private sealed class Hand
    {
        public bool Loaded { get; set; }

        public void Draw(Ui ui)
        {
            ui.Label(Loaded ? "data 7" : "loading");
            if (Loaded) ui.MarkReady();
        }
    }

    /// <summary>
    /// A page whose data arrives the way the framework delivers it: a pool thread posts to
    /// <see cref="Completions"/> and the next frame boundary applies it, and only the frames
    /// after that mark ready. This is the shape a real app has, and the reason a wait needs no
    /// polling of its own.
    /// </summary>
    private sealed class Loader
    {
        private readonly Mutation<string> _load;
        private string? _data;

        public Loader() => _load = new Mutation<string>(_ => Task.FromResult(""), value => _data = value);

        /// <summary>
        /// The release signal: the operation finishes on a pool thread when the test says so,
        /// through the same completion path a mutation's continuation uses. The mutation is
        /// never <c>Run</c> — this is about the drain at the frame boundary, not about an
        /// operation's process-wide bookkeeping, which a sibling test class counts exactly.
        /// </summary>
        public void Deliver(string value) => Task.Run(() => Completions.Post(_load, value, 0));

        public void Draw(Ui ui)
        {
            if (_data is { } data)
            {
                ui.Label($"loaded {data}");
                ui.MarkReady();
            }
            else
            {
                ui.Label("loading");
            }
        }
    }

    [Fact]
    public void ACompletedFrameThatAlreadyAnswersEndsTheWaitWithoutDrawing()
    {
        if (!OperatingSystem.IsWindows()) return;

        // The readiness lives on the latest completed frame, so a wait for what is already on
        // screen needs no new frame and leaves the app exactly as it found it.
        var hand = new Hand { Loaded = true };
        using var app = HeadlessApp.Start(hand.Draw, 400, 300);

        HeadlessApp.WaitOutcome outcome = app.WaitReady(TimeSpan.FromSeconds(10));
        Assert.True(outcome.Satisfied);
        Assert.Equal(0, outcome.Frames);

        HeadlessApp.WaitOutcome text = app.WaitFor("data 7", TimeSpan.FromSeconds(10));
        Assert.True(text.Satisfied);
        Assert.Equal(0, text.Frames);
    }

    [Fact]
    public void AFrameThatAbandonsTheWaitLosesEvenWhenItMarksReady()
    {
        if (!OperatingSystem.IsWindows()) return;

        // The token outranks the frame's own verdict, and an abandoned wait draws no next
        // frame: against a 30 s window, stopping at exactly the cancelling frame is what a
        // wait that honours both rules looks like. (The cancelling frame also marked itself
        // ready; had the verdict been believed, the wait would have exited 0 with a third
        // frame still in hand.)
        int draws = 0;
        using var gone = new CancellationTokenSource();
        using var app = HeadlessApp.Start(ui =>
        {
            ui.Label("loading");
            if (++draws == 2)
            {
                gone.Cancel();
                ui.MarkReady();
            }
        }, 400, 300);

        Assert.Throws<OperationCanceledException>(
            () => app.WaitReady(TimeSpan.FromSeconds(30), gone.Token));
        Assert.Equal(2, draws);
    }

    [Fact]
    public void AReadinessThatArrivesAfterTheDeadlineIsAMissAndNotALateSuccess()
    {
        if (!OperatingSystem.IsWindows()) return;

        // The overrun comes from the draw itself, not from machine speed: the wait's own frame
        // spins until the window has certainly passed before it marks, so the mark happens
        // after the deadline however the test thread is scheduled. A synchronous draw cannot be
        // interrupted, but its answer must not be reported as an in-time success.
        int draws = 0;
        using var app = HeadlessApp.Start(ui =>
        {
            ui.Label(++draws > 1 ? "data" : "loading");
            if (draws > 1)
            {
#pragma warning disable CUI001 // the whole point: this frame blocks until its wait's window has passed
                long until = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
                while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(1000);
#pragma warning restore CUI001
                ui.MarkReady();
            }
        }, 400, 300);

        var timeout = TimeSpan.FromMilliseconds(20);
        HeadlessApp.WaitOutcome outcome = app.WaitReady(timeout);

        Assert.False(outcome.Satisfied);
        Assert.Equal(1, outcome.Frames);
        Assert.True(outcome.Elapsed >= timeout, $"the wait claims it ended after {outcome.Elapsed}");
        Assert.True(app.Ready, "the frame really marked; the wait just refuses to call a late mark in time");
    }

    [Fact]
    public void AWindowSpentBeforeTheFirstFrameDrawsNothing()
    {
        if (!OperatingSystem.IsWindows()) return;

        // The wait's first act is asking the completed frame, and asking is real work: the
        // tree search over this page's two hundred rows spends a one-tick window by orders of
        // magnitude. The loop must then report a miss having drawn nothing — the counter was 1
        // when the wait started (the frame Start itself drew) and stays 1.
        int draws = 0;
        using var app = HeadlessApp.Start(ui =>
        {
            draws++;
            for (int i = 0; i < 200; i++) ui.Label($"row {i}");
        }, 400, 300);

        var window = TimeSpan.FromTicks(1);
        HeadlessApp.WaitOutcome outcome = app.WaitFor("never drawn", window);

        Assert.False(outcome.Satisfied);
        Assert.Equal(0, outcome.Frames);
        Assert.True(draws == 1, "an expired window buys no frames, not even one");
        Assert.True(outcome.Elapsed >= window, $"the wait claims it ended after {outcome.Elapsed}");
    }

    [Fact]
    public void ACancellationDuringTheFinalPaceIsACancellationAndNotATimeout()
    {
        if (!OperatingSystem.IsWindows()) return;

        // The clock and the pace in the test's hand turn the deadline's last instant into
        // staged events instead of a millisecond race: the wait's frame comes back with one
        // millisecond of window left, so the next pace is the last the loop can ever take, and
        // the test cancels inside that pace while the clock runs past the deadline before the
        // pace hands back. Cancellation and expiry arriving together is exactly what no
        // real-time test can stage — and the token must still win, at the pace, with no
        // further frame drawn.
        int draws = 0;
        using var gone = new CancellationTokenSource();
        using var app = HeadlessApp.Start(ui =>
        {
            ui.Label("loading");
            draws++;
        }, 400, 300);

        var window = TimeSpan.FromMilliseconds(50);
        var spent = TimeSpan.FromMilliseconds(49);

        Assert.Throws<OperationCanceledException>(() => app.Wait(
            () => app.Ready,
            window,
            gone.Token,
            () => spent,
            _ =>
            {
                gone.Cancel(); // abandoned while the final pace is being taken…
                spent = window; // …and the window expires before the pace hands back
            }));

        Assert.True(draws == 2, $"the token ended the wait at the pace, but {draws} draws happened");
        Assert.False(app.Ready, "nothing marked itself ready on the way out");
    }

    [Fact]
    public void ReadinessIsPublishedOnlyWhenThePaintThatEarnedItIsDone()
    {
        if (!OperatingSystem.IsWindows()) return;

        // CUI runs canvas painters inside EndFrame, after the draw body has finished. Two rules
        // follow and are checked here: mid-paint a reader still sees the previous completed
        // frame's verdict, and a mark made from inside the paint counts for the frame being
        // painted — visible once EndFrame completes, and reset by the frame after it.
        bool markFromPaint = false;
        bool? seenMidPaint = null;
        HeadlessApp? app = null;
        app = HeadlessApp.Start(ui =>
        {
            ui.Label("canvas");
            ui.Canvas("paint", Size.Percent(100), Size.Px(40), (paintUi, _) =>
            {
                if (markFromPaint)
                {
                    seenMidPaint ??= app?.Ready;
                    paintUi.MarkReady();
                }
            });
        }, 400, 300);

        try
        {
            Assert.False(app.Ready);

            markFromPaint = true;
            app.Frame();
            Assert.NotNull(seenMidPaint);
            Assert.False(seenMidPaint.Value,
                "mid-paint the reader still sees the previous completed frame's verdict, not this frame's mark");
            Assert.True(app.Ready, "a mark made from the deferred paint counts for the frame it painted");

            markFromPaint = false;
            app.Frame();
            Assert.False(app.Ready, "and the mark resets with the frame, like any other");
        }
        finally
        {
            app.Dispose();
        }
    }

    [Fact]
    public void AFrameThatMarksItselfReadyIsReadyFromItsFirstFrame()
    {
        if (!OperatingSystem.IsWindows()) return;

        var hand = new Hand { Loaded = true };
        using var app = HeadlessApp.Start(hand.Draw, 400, 300);

        Assert.True(app.Ready);
        Assert.True(app.Surface.LastFrameReady);
    }

    [Fact]
    public void AFrameThatDoesNotMarkIsNotReadyHoweverManyFramesPass()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = HeadlessApp.Start(new Hand().Draw, 400, 300);
        app.Frame(5);

        Assert.False(app.Ready);
        Assert.False(app.Surface.LastFrameReady);
    }

    [Fact]
    public void ReadinessArrivesWithTheFrameThatMarksAndLeavesWithTheFrameThatDoesNot()
    {
        if (!OperatingSystem.IsWindows()) return;

        var hand = new Hand();
        using var app = HeadlessApp.Start(hand.Draw, 400, 300);
        Assert.False(app.Ready);

        hand.Loaded = true;
        app.Frame();
        Assert.True(app.Ready);

        // A frame that goes back to loading is not ready on the strength of an earlier mark:
        // the marker resets at the frame boundary, so the verdict always describes the latest
        // completed frame, not the most dramatic one so far.
        hand.Loaded = false;
        app.Frame();
        Assert.False(app.Ready);

        hand.Loaded = true;
        app.Frame();
        Assert.True(app.Ready);
    }

    [Fact]
    public void AWaitSeesTheFrameThatArrivedThroughACompletion()
    {
        if (!OperatingSystem.IsWindows()) return;

        var loader = new Loader();
        using var app = HeadlessApp.Start(loader.Draw, 400, 300);
        Assert.False(app.Ready);
        Assert.True(app.TryFind("loading", out _));

        // The pool posts the completion; the wait releases because each frame drains Completions
        // at its boundary, so no timing assumption is built into this test.
        loader.Deliver("42 rows");

        HeadlessApp.WaitOutcome ready = app.WaitReady(TimeSpan.FromSeconds(10));
        Assert.True(ready.Satisfied, ready.ToString());
        Assert.True(ready.Frames >= 1);
        Assert.True(app.Ready);
        Assert.True(app.TryFind("loaded 42 rows", out _));

        // The same arrival, seen through the query side of the wait.
        Assert.True(app.WaitFor("loaded 42 rows", TimeSpan.FromSeconds(10)).Satisfied);
    }

    [Fact]
    public void AWaitThatNeverSeesAReadyFrameEndsByTheClockAndReportsWhatItDrew()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = HeadlessApp.Start(new Hand().Draw, 400, 300);
        var timeout = TimeSpan.FromMilliseconds(250);

        var clock = Stopwatch.StartNew();
        HeadlessApp.WaitOutcome outcome = app.WaitReady(timeout);
        clock.Stop();

        Assert.False(outcome.Satisfied);
        Assert.True(outcome.Frames >= 1, $"a bounded wait gave up after {outcome.Frames} frames");

        // What a miss must guarantee is observable, not how many frames a particular machine
        // draws in it: the deadline was honoured, and no frame ever claimed a readiness the
        // app never marked.
        Assert.True(outcome.Elapsed >= timeout, $"the wait gave up after {outcome.Elapsed}");
        Assert.True(clock.Elapsed >= timeout);
        Assert.False(app.Ready);
    }

    [Fact]
    public void AWaitForTextTimesOutAloneWhenOnlyOtherTextIsDrawn()
    {
        if (!OperatingSystem.IsWindows()) return;

        var hand = new Hand { Loaded = true };
        using var app = HeadlessApp.Start(hand.Draw, 400, 300);

        Assert.True(app.WaitFor("data 7", TimeSpan.FromSeconds(10)).Satisfied);
        Assert.False(app.WaitFor("data 8", TimeSpan.FromMilliseconds(200)).Satisfied);
    }

    [Fact]
    public void ACancelledWaitEndsByTheTokenAndNotByTheClock()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = HeadlessApp.Start(new Hand().Draw, 400, 300);

        // Already cancelled: the wait never draws its first frame.
        using var gone = new CancellationTokenSource();
        gone.Cancel();
        Assert.Throws<OperationCanceledException>(
            () => app.WaitReady(TimeSpan.FromSeconds(30), gone.Token));

        // Cancelled mid-wait: the loop asks the token every frame, so it ends at the next one.
        using var soon = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        Assert.Throws<OperationCanceledException>(
            () => app.WaitFor("never drawn", TimeSpan.FromSeconds(30), soon.Token));
    }

    [Fact]
    public void ATimeoutThatCannotBoundAWaitIsRefused()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var app = HeadlessApp.Start(new Hand().Draw, 400, 300);

        Assert.Throws<ArgumentOutOfRangeException>(() => app.WaitReady(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => app.WaitReady(TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => app.WaitReady(Timeout.InfiniteTimeSpan));
        Assert.Throws<ArgumentException>(() => app.WaitFor("", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ReadinessIsNotTheHeadlessBanner()
    {
        if (!OperatingSystem.IsWindows()) return;

        // An app that never marks anything still serves the protocol: CUI-READY only proves the
        // channel is open, and conflating the two would make every headless app look loaded.
        using var app = HeadlessApp.Start(new Hand().Draw, 400, 300);
        Assert.False(app.Ready);

        var output = new StringWriter();
        HeadlessLoop.Run(app, new StringReader("tree\nquit\n"), output);

        Assert.StartsWith("CUI-READY 400 300\n", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("CUI-OK", output.ToString(), StringComparison.Ordinal);
    }
}
