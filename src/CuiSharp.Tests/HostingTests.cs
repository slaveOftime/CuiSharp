using CuiSharp.Hosting;

namespace CuiSharp.Tests;

[Collection("ProcessWindowState")]
public class HostingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void ActionButtonAndAddPageWireComponentsAndOperationsCleanly()
    {
        var builder = CuiApplication.CreateBuilder();
        builder.AddPage<Asleep>();
        builder.AddPage<Live>();
        using var built = builder.Build();

        var pages = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetServices<Component>(built.Services)
            .ToArray();
        Assert.Equal(2, pages.Length);
        Assert.Same(
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Asleep>(built.Services),
            pages[0]);

        int runs = 0;
        bool fail = true;
        using var save = new Mutation(_ =>
        {
            Interlocked.Increment(ref runs);
            return fail ? Task.FromException(new InvalidOperationException("disk full")) : Task.CompletedTask;
        });

        using var app = HeadlessApp.Start(ui =>
        {
            ui.ActionButton("Save", save, loadingLabel: "Saving", retryLabel: "Retry save");
        }, 320, 160);

        app.FindButton("Save").ClickAndFrame();
        Until(() => !save.IsRunning);
        app.Frame();
        Assert.Equal(1, runs);
        Assert.Contains(app.Tree.Descendants(), node => node.Kind == "Badge" && node.Text == "disk full");

        fail = false;
        app.FindButton("Retry save").ClickAndFrame();
        Until(() => !save.IsRunning);
        Assert.Equal(2, runs);
        Assert.Null(save.Error);
    }

    [Fact]
    public void AnUnrelatedSurfaceNeitherAppliesResultsNorReportsItselfLive()
    {
        using var owner = HeadlessApp.Start(ui => ui.Label("owner"), 120, 80);
        using var observer = HeadlessApp.Start(ui => ui.Label("observer"), 120, 80);
        var finish = new TaskCompletionSource<int>();
        int applied = 0;
        using var mutation = new Mutation<int>(_ => finish.Task, value => applied = value);

        owner.Surface.BeginFrame();
        mutation.Run();
        owner.Surface.EndFrame();
        Assert.True(owner.Surface.LastFrame.IsLive);
        observer.Frame();
        Assert.False(observer.Surface.LastFrame.IsLive);

        finish.SetResult(7);
        Until(() => !mutation.IsRunning);
        observer.Frame();
        Assert.Equal(0, applied);
        owner.Frame();
        Assert.Equal(7, applied);
        Assert.False(owner.Surface.LastFrame.IsLive);
    }

    [Fact]
    public void AThrowingApplyIsReportedWithoutInterruptingOtherAppliesOrDraw()
    {
        int applied = 0;
        using var broken = new Mutation<int>(_ => Task.FromResult(7), _ => throw new InvalidOperationException("apply failed"));
        using var healthy = new Mutation<int>(_ => Task.FromResult(42), value => applied = value);
        using var app = HeadlessApp.Start(ui =>
        {
            if (broken.Error is { } error) ui.Label(error.Message);
            ui.Label($"applied {applied}");
        }, 320, 160);

        app.Surface.BeginFrame();
        broken.Run();
        Until(() => !broken.IsRunning); // enqueue this one first
        healthy.Run();
        Until(() => !healthy.IsRunning);
        app.Surface.EndFrame();
        app.Frame();

        Assert.Equal(7, broken.Latest);
        Assert.Equal("apply failed", broken.Error?.Message);
        Assert.Equal(["apply failed"], app.Faults);
        Assert.True(app.TryFind("apply failed", out _));
        Assert.True(app.TryFind("applied 42", out _));
    }

    [Fact]
    public void AnAbortedSurfaceFrameDoesNotLeakDrawingStateOrDropSubsequentApplies()
    {
        int applied = 0;
        using var mutation = new Mutation<int>(_ => Task.FromResult(9), value => applied = value);
        using (var surface = CuiSurface.Create(160, 100))
        {
            surface.BeginFrame();
            mutation.Run();
            Until(() => !mutation.IsRunning);

            // Simulate an unclosed frame followed by a fresh frame on the same surface.
            surface.BeginFrame();
            surface.EndFrame();
            Assert.Equal(9, applied);

            // An unclosed frame at disposal must also unlink this surface from the thread.
            surface.BeginFrame();
        }

        Assert.Null(UiFrameState.Drawing);
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
        using var mutation = new Mutation<int>(_ => Task.FromResult(7), value => applied = value);
        mutation.Run();
        Until(() => !mutation.IsRunning);
        Assert.Equal(0, applied);
        Assert.True(app.Frame());
        Assert.Equal(7, applied);
    }

    [Fact]
    public void ACuiAppRunAppliesFinishedWork()
    {
        if (!OperatingSystem.IsWindows()) return;

        int applied = 0;
        bool started = false;
        CuiWindow? window = null;
        using var mutation = new Mutation<int>(_ => Task.FromResult(7), value => applied = value);
        using var watchdog = new Timer(_ =>
        {
            try { window?.RequestClose(); }
            catch (ObjectDisposedException) { }
        }, null, TimeSpan.FromSeconds(10), System.Threading.Timeout.InfiniteTimeSpan);

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

            if (applied == 7) window!.RequestClose();
        });

        Assert.Equal(0, code);
        Assert.Equal(7, applied);
    }

    [Fact]
    public void AThrottledWorkerWakesItsWindowsNextDrawOnCompletion()
    {
        if (!OperatingSystem.IsWindows()) return;

        CuiWindow? window = null;
        int timedOut = 0;
        bool started = false;
        bool drewCompletion = false;
        using var operation = new Progressive<string>(async (token, publish) =>
        {
            // Let the hidden window enter its long wait; no input is injected to wake it.
            await Task.Delay(250, token);
            publish("complete");
        })
        { FrameRate = 1f / 30f };
        using var watchdog = new Timer(_ =>
        {
            Volatile.Write(ref timedOut, 1);
            try { window?.RequestClose(); }
            catch (ObjectDisposedException) { }
        }, null, TimeSpan.FromSeconds(10), System.Threading.Timeout.InfiniteTimeSpan);

        int code = CuiApp.Run(new CuiAppOptions
        {
            Title = "CompletionWake",
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
                operation.Run();
            }

            if (!operation.IsRunning && operation.Latest == "complete" && Volatile.Read(ref timedOut) == 0)
            {
                drewCompletion = true;
                window!.RequestClose();
            }
        });

        Assert.Equal(0, code);
        Assert.True(drewCompletion, "the window did not draw the final snapshot before the watchdog");
    }

    private sealed class Probe : Component
    {
        protected override void Draw(Ui ui) { }
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
        protected override void Draw(Ui ui) => ui.Text(new string('x', 8) + FrameStats.Frames);
    }

    [Fact]
    public void FrameStatsReportTheCostOfTheFrameThatDrew()
    {
        using var surface = CuiSurface.Create(240, 160);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        using var quiet = new Probe();
        using var noisy = new Allocating();

        long frames = surface.LastFrame.Frames;
        surface.Draw(quiet.Render, pixels);
        Assert.True(surface.LastFrame.Frames > frames);
        surface.Draw(quiet.Render, pixels);
        Assert.Equal(0, surface.LastFrame.AllocatedBytes);
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
        using var surface = CuiSurface.Create(240, 160);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        using var asleep = new Asleep();
        using var live = new Live();

        surface.Draw(asleep.Render, pixels);
        Assert.False(surface.LastFrame.IsLive);
        for (var i = 0; i < 4; i++) surface.Draw(live.Render, pixels);
        Assert.True(surface.LastFrame.IsLive);
        Assert.True(surface.LastFrame.Fps > 0);
        surface.Draw(asleep.Render, pixels);
        Assert.False(surface.LastFrame.IsLive);
    }

    [Fact]
    public void TheThemeIsReadAndWrittenFromInsideAFrame()
    {
        using var surface = CuiSurface.Create(200, 120);
        Ui ui = surface.Ui;
        Assert.Equal(Theme.Light, ui.Theme);
        ui.Theme = Theme.Dark;
        Assert.Equal(Theme.Dark, ui.Theme);
        ui.ToggleTheme();
        Assert.Equal(Theme.Light, ui.Theme);
        ui.Accent = Accent.Rose;
        Assert.Equal(Accent.Rose, ui.Accent);
    }

    private sealed class Exploding(bool duringInitialization) : Component
    {
        protected override void OnInitialized()
        {
            if (duringInitialization) throw new InvalidOperationException("component failed");
        }

        protected override void Draw(Ui ui) => throw new InvalidOperationException("component failed");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AThrowingComponentIsContainedByItsErrorBoundary(bool duringInitialization)
    {
        using var surface = CuiSurface.Create(320, 200);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        using var exploding = new Exploding(duringInitialization);
        surface.Draw(exploding.Render, pixels);

        Assert.Contains(pixels, b => b != 0);
        Assert.Equal("component failed", Assert.Single(surface.Faults).Message);
    }

    private static void Until(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, Timeout));
}
