using CuiSharp.Interop;

namespace CuiSharp.Tests;

public sealed class InputResultTests
{
    [Fact]
    public void ToStringListsReportedEventsInStableOrder()
    {
        Assert.Equal("none", new InputResult(0).ToString());
        Assert.Equal("changed, focused, blurred", new InputResult(
            CuiFlags.CUI_CHANGED | CuiFlags.CUI_FOCUSED | CuiFlags.CUI_BLURRED).ToString());
        Assert.Equal("changed, submitted, focused, blurred", new InputResult(
            CuiFlags.CUI_CHANGED | CuiFlags.CUI_SUBMITTED | CuiFlags.CUI_FOCUSED | CuiFlags.CUI_BLURRED).ToString());
    }
}
