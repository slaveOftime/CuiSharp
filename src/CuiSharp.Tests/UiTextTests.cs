using System.Text;

namespace CuiSharp.Tests;

public class UiTextTests
{
    [Fact]
    public void StringsUtf8AndInterpolationAgree()
    {
        Utf8.BeginFrame();
        Assert.Equal("héllo", Read("héllo"));
        Assert.Equal("héllo", Read("héllo"u8));
        string value = "héllo";
        Assert.Equal(value, Read($"{value}"));
        Assert.Equal("[    42|42    ] 1.50 FF", Read($"[{42,6}|{42,-6}] {1.5:F2} {255:X}"));
        Assert.Equal("[   héllo|héllo   ] true é", Read($"[{value,8}|{value,-8}] {true} {'é'}"));
        UiText nested = $"nested {42}";
        Assert.Equal("[nested 42]", Read($"[{nested}]"));
        Assert.Equal("", Read(default));
        Assert.Equal("", Read((string?)null));
    }

    [Fact]
    public unsafe void ByteLengthCountsUtf8AndInputSpanIsCopied()
    {
        Utf8.BeginFrame();
        Span<byte> source = stackalloc byte[3];
        "é!"u8.CopyTo(source);
        UiText text = (ReadOnlySpan<byte>)source;
        source.Fill((byte)'x');
        nint pointer = text.GetPointer(out int length);
        Assert.Equal(3, length);
        Assert.Equal(0, ((byte*)pointer)[length]);
        Assert.Equal("é!", Read(text));
    }

    [Fact]
    public void LongTextGrowsAndEarlierTextSurvives()
    {
        Utf8.BeginFrame();
        string value = new('界', 10_000);
        UiText first = $"prefix {value}";
        UiText second = $"value {123}";
        Assert.Equal("prefix " + value, Read(first));
        Assert.Equal("value 123", Read(second));
    }

    [Fact]
    public unsafe void ReadingACopyCannotReclaimAnotherAllocationsTail()
    {
        Utf8.BeginFrame();
        UiText text = $"{42}";
        Assert.Equal("42", Read(text));

        // Match the old handler reservation's end address: previously an
        // end-address-only ReturnTail would mistake this for its own slack.
        byte* marker = Utf8.AllocFrameBytes(37);
        new Span<byte>(marker, 37).Fill((byte)'!');
        int before = Utf8.FrameBytes;
        Assert.Equal("42", Read(text));
        Assert.Equal(before, Utf8.FrameBytes);
        _ = Utf8.Get(new string('z', 37));
        Assert.True(new ReadOnlySpan<byte>(marker, 37).IndexOfAnyExcept((byte)'!') < 0);
    }

    [Fact]
    public void InterpolationAndReusedValuesAllocateNothing()
    {
        static void Format()
        {
            Utf8.BeginFrame();
            UiText text = $"rows {1234,6:D4} of {5000} {1.25:F2}";
            _ = text.GetPointer(out _);
            _ = text.GetPointer(out _);
        }
        for (int i = 0; i < 10; i++) Format();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++) Format();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static unsafe string Read(UiText text)
    {
        nint pointer = text.GetPointer(out int length);
        return length == 0 ? "" : Encoding.UTF8.GetString(new ReadOnlySpan<byte>((void*)pointer, length));
    }
}
