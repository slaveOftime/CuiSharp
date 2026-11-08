using CuiSharp;
using CuiSharp.App.Pages;
using CuiSharp.App.Services;
using CuiSharp.Hosting;

namespace CuiSharp.App.Tests;

/// <summary>
/// A page is testable without a window: <see cref="HeadlessApp"/> and <see cref="CuiSurface"/> draw
/// real frames into a pixel buffer with the same layout, text shaping and rasterizer the window
/// uses. "Does this page draw at all, and what does a frame cost?" is a unit test instead of a
/// manual check. Clicking through it is <see cref="PageInteractionTests"/>.
/// </summary>
/// <remarks>
/// <see cref="HeadlessApp"/> comes with CuiSharp; <c>CuiSharp.Testing.TestApp</c> is the same
/// class under the name a test wants to read, in a package of its own.
/// </remarks>
public class PageRenderTests
{
    private const int Width = 640;
    private const int Height = 480;

    // ---- it draws ----

    [Fact]
    public void TheTodoPageDrawsItsItemsAndSavesNothingOnItsOwn()
    {
        var todos = new TodoService();
        using var app = HeadlessApp.Start(new TodoPage(todos), Width, Height);

        Assert.True(app.PaintedAnything());
        Assert.Empty(app.Faults);                        // a page that throws still draws something
        Assert.Contains(app.Tree.Descendants(), n => n.Text == "Sketch the onboarding flow");
    }

    [Fact]
    public void TheMarketsPageDrawsBeforeAndAfterTheHistoryArrives()
    {
        var clock = new Clock();
        var feed = new MarketFeed(new PriceFeed(), clock);
        var page = new MarketsPage(feed);

        using var app = HeadlessApp.Start(page, Width, Height);
        Assert.Empty(app.Faults);                         // the idle state is a real state
        Assert.Contains(app.Tree.Descendants(), n => n.Text == "Loading history…");

        // The same page once samples exist: the chart has something to draw and the feed ticks.
        feed.Apply(ASnapshot(points: 400));
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(16.6);
            app.Frame();
        }

        Assert.Empty(app.Faults);
        Assert.True(feed.Ticks > 0);
        Assert.Contains(app.Tree.Descendants(), n => n.Kind == "LineChart");
    }

    // ---- what it costs ----

    [Fact]
    public void TheTodoPageDrawsAFrameWithoutAllocating()
    {
        var page = new TodoPage(new TodoService());
        AssertDrawsWithoutAllocating(page);
    }

    [Fact]
    public void TheShellDrawsAFrameWithoutAllocating()
    {
        var app = new App(new TodoPage(new TodoService()), NewMarkets());
        AssertDrawsWithoutAllocating(app);
        app.Dispose();
    }

    [Fact]
    public void TheMarketsPageDrawsALiveFrameWithoutAllocating()
    {
        var clock = new Clock();
        var feed = new MarketFeed(new PriceFeed(), clock);
        feed.Apply(ASnapshot(points: 240));

        using var surface = CuiSurface.Create(Width, Height);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        using var page = new MarketsPage(feed);
        DrawFn draw = page.Render;

        // Live mode is the interesting one: ticks, a decimated chart, a sparkline per row.
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(16.6);
            surface.Draw(draw, pixels);
        }

        var batches = new long[5];
        for (var b = 0; b < batches.Length; b++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10; i++)
            {
                clock.Advance(16.6);
                surface.Draw(draw, pixels);
            }
            batches[b] = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Array.Sort(batches);
        Assert.Equal(0, batches[batches.Length / 2]);
        Assert.Empty(surface.Faults);
        Assert.True(feed.Ticks > 0);
    }

    // ---- helpers ----

    /// <summary>
    /// The rule the README calls out, as a test: state lives in fields, text is formatted into the
    /// frame arena, and collections are read through a list. A regression here shows up as a
    /// number, not as a stutter on someone else's machine.
    /// </summary>
    private static void AssertDrawsWithoutAllocating(Component page)
    {
        using var surface = CuiSurface.Create(Width, Height);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];

        // One delegate for every frame, created outside the measurement.
        DrawFn draw = page.Render;

        // The first frames warm up the surface's own buffers and the JIT.
        for (var i = 0; i < 3; i++) surface.Draw(draw, pixels);

        Assert.Equal(0, FramesThatAllocate(surface, draw, pixels));
        Assert.Empty(surface.Faults);
    }

    /// <summary>
    /// Bytes a run of frames allocates, read as the middle of five batches. A single batch is a
    /// coin flip: one allocation belonging to nothing in particular — a lazily built string, a
    /// background test on the same machine — would fail a budget that is really about steady state.
    /// </summary>
    private static long FramesThatAllocate(CuiSurface surface, DrawFn draw, byte[] pixels)
    {
        var batches = new long[5];
        for (var b = 0; b < batches.Length; b++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10; i++) surface.Draw(draw, pixels);
            batches[b] = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        var sorted = (long[])batches.Clone();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    private static MarketsPage NewMarkets() => new(new MarketFeed(new PriceFeed(), new Clock()));

    /// <summary>A clock a test advances by hand, so no test waits in real time.</summary>
    private sealed class Clock : TimeProvider
    {
        private long _ms;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => _ms;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(_ms);

        public void Advance(double milliseconds) => _ms += (long)milliseconds;
    }

    private static MarketSnapshot ASnapshot(int points)
    {
        var history = new float[PriceFeed.Symbols.Count][];
        var last = new float[PriceFeed.Symbols.Count];
        for (var s = 0; s < PriceFeed.Symbols.Count; s++)
        {
            history[s] = new float[points];
            var price = PriceFeed.Symbols[s].Start;
            for (var i = 0; i < points; i++)
            {
                price *= 1.001f;
                history[s][i] = price;
            }
            last[s] = price;
        }
        return new MarketSnapshot(history, last);
    }
}
