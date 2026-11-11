using CuiSharp.Tools;

namespace CuiSharp.Tests;

/// <summary>
/// The grammar of the one command the tool answers itself: <c>dwell</c>. The sampling it drives
/// needs a live process; what can be tested without one is what parses as a dwell and what does
/// not, so the walk scripts can rely on it.
/// </summary>
public class DwellGrammarTests
{
    [Theory]
    [InlineData("dwell 3", 3)]
    [InlineData("dwell 0.5", 0.5)]
    [InlineData("dwell\t2", 2)]
    public void ADwellNamesSeconds(string line, double expected)
        => Assert.Equal(expected, Parse(line));

    [Theory]
    [InlineData("frame 3")]
    [InlineData("dwell")]
    [InlineData("dwellthree")]
    public void AnythingElseIsNotADwell(string line)
        => Assert.False(Cli.TryReadDwell(line, out _));

    [Fact]
    public void ADwellWithoutNumbersIsAnErrorNotACommandThatFallsThrough()
    {
        // "dwell" plus junk is a typo'd dwell, and a typo the app eats is a confusing error.
        Assert.Throws<ToolException>(() => Cli.TryReadDwell("dwell now", out _));
    }

    [Theory]
    [InlineData("dwell 0")]
    [InlineData("dwell -3")]
    [InlineData("dwell Infinity")]
    [InlineData("dwell NaN")]
    public void ADwellOfNoTimeIsRefused(string line)
    {
        // Infinity would spin the walk forever and zero would report a row of zeros, so both
        // are typos worth naming rather than measuring.
        Assert.Throws<ToolException>(() => Cli.TryReadDwell(line, out _));
    }

    private static double Parse(string line)
    {
        Assert.True(Cli.TryReadDwell(line, out double seconds));
        return seconds;
    }
}
