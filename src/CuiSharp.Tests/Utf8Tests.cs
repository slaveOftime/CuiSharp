namespace CuiSharp.Tests;

/// <summary>UTF-8 marshalling and the editable text buffer.</summary>
public class Utf8Tests
{
    [Fact]
    public void ArenaBumpAllocatesAndResets()
    {
        Utf8.BeginFrame();
        nint first = Utf8.Get("Overview");
        nint second = Utf8.Get("Overview");

        // A bump allocator hands out fresh blocks rather than interning.
        Assert.NotEqual(0, first);
        Assert.NotEqual(first, second);
        Assert.True(Utf8.FrameBytes >= 18);   // two 8-byte strings plus terminators

        // A new frame starts over at the same address: that is the whole point.
        Utf8.BeginFrame();
        Assert.Equal(0, Utf8.FrameBytes);
        Assert.Equal(first, Utf8.Get("Overview"));
    }

    [Fact]
    public void ArenaGrowsToFitOneLargeFrameWithoutLosingEarlierWrites()
    {
        Utf8.BeginFrame();
        nint first = Utf8.Get("kept");
        int before = Utf8.Capacity;

        // Force growth in a single frame; the earlier pointer must survive it.
        nint big = Utf8.Get(new string('x', 400_000));

        Assert.True(Utf8.Capacity > before);
        Assert.Equal("kept", ReadUtf8(first));
        Assert.Equal(400_000, ReadUtf8(big).Length);
    }

    [Fact]
    public void Utf8LiteralAndStringPathsAgree()
    {
        Utf8.BeginFrame();
        Assert.Equal("Save", ReadUtf8(Utf8.Get("Save")));
        Assert.Equal("Save", ReadUtf8(Utf8.Get("Save"u8)));
    }

    [Fact]
    public unsafe void HandlerAllocationDoesNotScaleWithWork()
    {
        // The point of the handler is that formatting cost is independent of how
        // many values are appended: no string, no boxed argument, no growing buffer.
        static long CostOf(int values)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var h = new Utf8Handler(16, values);
            h.AppendLiteral("n=");
            for (int i = 0; i < values; i++) h.AppendFormatted(i);
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.True(h.Ptr[0] != 0);
            return after - before;
        }

        _ = CostOf(4);            // warm Encoding.UTF8 and any pooled encoder
        long small = CostOf(10);
        long large = CostOf(1_000);

        Assert.Equal(small, large);
        Assert.True(large < 1024, $"formatted 1000 values for {large} bytes");
    }

    // First use pays for Encoding.UTF8's lazily created state; keep it out of the measurement.
    private static unsafe void WarmUpEncoding()
    {
        var w = new Utf8Handler(8, 2);
        w.AppendLiteral("abc");
        w.AppendFormatted(7);
        _ = (nint)w.Ptr;
    }

    [Fact]
    public unsafe void HandlerAllocationsAreDistinctWithinAFrame()
    {
        // CUI reads text during the frame's paint phase, so two strings made in
        // the same frame must live in different memory.
        Utf8.BeginFrame();

        var a = new Utf8Handler(4, 1);
        a.AppendLiteral("row ");
        a.AppendFormatted(1);
        nint first = (nint)a.Ptr;

        var b = new Utf8Handler(4, 1);
        b.AppendLiteral("row ");
        b.AppendFormatted(2);
        nint second = (nint)b.Ptr;

        Assert.NotEqual(first, second);
        Assert.Equal("row 1", ReadUtf8(first));
        Assert.Equal("row 2", ReadUtf8(second));
    }

    [Fact]
    public unsafe void HandlerSupportsStandardFormatSpecifiers()
    {
        Utf8.BeginFrame();

        var h = new Utf8Handler(8, 3);
        h.AppendLiteral("p=");
        h.AppendFormatted(1.5, "F2");
        h.AppendLiteral(" n=");
        h.AppendFormatted(1234, "N0");
        h.AppendLiteral(" x=");
        h.AppendFormatted(255, "X");

        Assert.Equal("p=1.50 n=1,234 x=FF", ReadUtf8((nint)h.Ptr));
    }

    private static unsafe string ReadUtf8(nint pointer)
    {
        var b = (byte*)pointer;
        int len = 0;
        while (b[len] != 0) len++;
        return System.Text.Encoding.UTF8.GetString(new ReadOnlySpan<byte>(b, len));
    }

    [Fact]
    public void TextBufferRoundTrips()
    {
        using var buffer = new TextBuffer(64);
        buffer.Set("Maya Okafor");

        Assert.Equal("Maya Okafor", buffer.Value);
        Assert.Equal(64, (int)buffer.Capacity);
    }

    [Fact]
    public void TextBufferRejectsValuesThatDoNotFit()
    {
        using var buffer = new TextBuffer(8);
        Assert.Throws<ArgumentException>(() => buffer.Set("0123456789"));
    }

    [Fact]
    public void TextBufferStartsZeroed()
    {
        using var buffer = new TextBuffer(16);
        Assert.Equal(string.Empty, buffer.Value);
    }
}
