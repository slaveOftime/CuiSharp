using CuiSharp.Interop;

namespace CuiSharp.Tests;

/// <summary>
/// Per-frame bookkeeping that must not depend on how many frames have run: the
/// frame arena, the deferred canvas painters, and the text-input buffers. Each
/// of these grew or lost data before Phase 0, and only a multi-frame test shows
/// it.
/// </summary>
public class FrameLifecycleTests
{
    private static bool NeedsNative => OperatingSystem.IsWindows();

    [Fact]
    public void HeadlessFramesDoNotGrowTheArenaOrThePainterList()
    {
        if (!NeedsNative) return;

        using var surface = CuiSurface.Create(320, 200);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        int painted = 0;

        void Frame()
        {
            surface.BeginFrame();
            using (surface.Ui.Column(new Box { Pad = 8 }))
            {
                surface.Ui.Heading("Headless", 3);
                surface.Ui.Canvas("c", Size.Fill, Size.Px(40), (ui, box) =>
                {
                    painted++;
                    ui.DrawRect(box, Color.Accent, 4);
                });
            }
            surface.EndFrame();
            surface.Render(pixels);
        }

        // Warm up: the first frames grow the arena to its high-water mark.
        for (int i = 0; i < 10; i++) Frame();
        int capacity = Utf8.Capacity;

        for (int i = 0; i < 1_000; i++) Frame();

        Assert.Equal(capacity, Utf8.Capacity);
        Assert.Equal(1_010, painted);   // one paint per frame, none dropped
    }

    [Fact]
    public void HeadlessFramesAllocateNothingPerFrameInSteadyState()
    {
        if (!NeedsNative) return;

        using var surface = CuiSurface.Create(320, 200);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];

        var feed = new float[50_000];
        for (int i = 0; i < feed.Length; i++) feed[i] = MathF.Sin(i * 0.01f) * 100;
        var series = new Series[] { new() { Values = feed } };

        void Frame()
        {
            surface.BeginFrame();
            using (surface.Ui.Column(new Box { Pad = 8 }))
            {
                var ui = surface.Ui;
                ui.Heading("Steady", 3);
                ui.Text($"count {42,4:D3}");
                ui.Metric($"metric {42}", $"{1.25:F2}");
                ui.DrawText($"tick {42}", 4, 120, Color.Foreground);
                ui.Canvas("canvas", Size.Fill, Size.Px(20), static (paint, rect) =>
                    paint.DrawText($"width {rect.W:F1}", rect.X, rect.Y, Color.Foreground));
                // A long feed must decimate into the frame arena, not into arrays.
                ui.LineChart("feed", series);
            }
            surface.EndFrame();
            surface.Render(pixels);
        }

        for (int i = 0; i < 10; i++) Frame(); // JIT, delegate cache, font atlas, arena growth

        // A recorder attached to this thread would allocate per widget, so the steady state
        // below is only meaningful with it off.
        Assert.False(WidgetRecorder.Enabled, "a recorder leaked onto this thread");

        // The typical frame, not one lucky run: a batch that straddles a one-off allocation
        // (another test class finishing on this thread, a pool growing) says nothing about the
        // frame, while a real per-frame regression shows up in every batch.
        var perFrame = new long[5];
        for (int batch = 0; batch < perFrame.Length; batch++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20; i++) Frame();
            perFrame[batch] = (GC.GetAllocatedBytesForCurrentThread() - before) / 20;
        }

        Assert.Equal(0, perFrame.Median());
    }
}

/// <summary>The interpolated-string handler must never drop text.</summary>
public class Utf8HandlerTests
{
    [Fact]
    public unsafe void LongFormattedValuesAreNotTruncated()
    {
        Utf8.BeginFrame();
        string value = new('x', 200);

        var h = new Utf8Handler(4, 1);
        h.AppendLiteral("#");
        h.AppendFormatted(value);

        Assert.Equal("#" + value, ReadUtf8(h.Ptr));
    }

    [Fact]
    public unsafe void ManyValuesGrowTheBlockAndKeepEverything()
    {
        Utf8.BeginFrame();

        var h = new Utf8Handler(0, 300);
        for (int i = 0; i < 300; i++) h.AppendFormatted(i);

        Assert.Equal(string.Concat(Enumerable.Range(0, 300).Select(i => i.ToString())), ReadUtf8(h.Ptr));
    }

    [Fact]
    public unsafe void AlignmentPadsLikeStringInterpolation()
    {
        Utf8.BeginFrame();

        var h = new Utf8Handler(0, 3);
        h.AppendLiteral("[");
        h.AppendFormatted(42, 6);
        h.AppendLiteral("|");
        h.AppendFormatted(42, -6);
        h.AppendLiteral("]");

        Assert.Equal("[    42|42    ]", ReadUtf8(h.Ptr));
    }

    [Fact]
    public unsafe void UnusedCapacityGoesBackToTheArena()
    {
        Utf8.BeginFrame();

        var shortText = new Utf8Handler(2, 0);
        shortText.AppendLiteral("ab");
        _ = shortText.Ptr;

        // A 2-byte label must not be charged for the per-value guess.
        Assert.InRange(Utf8.FrameBytes, 3, 32);
    }

    internal static unsafe string ReadUtf8(byte* b)
    {
        int len = 0;
        while (b[len] != 0) len++;
        return System.Text.Encoding.UTF8.GetString(new ReadOnlySpan<byte>(b, len));
    }
}

/// <summary>The editable text buffer's managed view must cost nothing per frame.</summary>
public class TextBufferCacheTests
{
    [Fact]
    public void ByteLengthCountsUtf8BytesNotChars()
    {
        using var buffer = new TextBuffer(64);
        buffer.Set("héllo");

        Assert.Equal(6, buffer.ByteLength);   // é is two bytes
        Assert.Equal(5, buffer.Value.Length);
    }

    [Fact]
    public void ValueIsDecodedOncePerChange()
    {
        using var buffer = new TextBuffer(64);
        buffer.Set("Maya");

        var first = buffer.Value;
        Assert.Same(first, buffer.Value);

        buffer.Invalidate();
        var second = buffer.Value;
        Assert.NotSame(first, second);   // re-decoded, then cached again
        Assert.Equal(first, second);
        Assert.Same(second, buffer.Value);
    }

    [Fact]
    public unsafe void NativeEditsShowUpOnceTheCacheIsInvalidated()
    {
        using var buffer = new TextBuffer(64);
        buffer.Set("abc");
        Assert.Equal("abc", buffer.Value);   // populates the cache

        // What CUI does on an edit: it writes the bytes itself and reports a
        // change flag, which is what Ui.Input turns into an invalidation.
        "xyz"u8.CopyTo(new Span<byte>(buffer.Ptr, 3));
        buffer.Ptr[3] = 0;

        Assert.Equal("abc", buffer.Value);   // still the cached string
        buffer.Invalidate();
        Assert.Equal("xyz", buffer.Value);
    }

    [Fact]
    public void SpanOverwriteRejectsValuesThatDoNotFit()
    {
        using var buffer = new TextBuffer(8);

        Assert.Throws<ArgumentException>(() => buffer.Set("0123456789"u8));
        buffer.Set("0123456"u8);
        Assert.Equal("0123456", buffer.Value);
    }
}
