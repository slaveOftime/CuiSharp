using CuiSharp.Tools;

namespace CuiSharp.Tests;

/// <summary>
/// How <c>dotnet cui</c> reads its own arguments. The important distinctions: a word is the
/// command, everything after <c>--</c> belongs to the app, and a typo in a flag is reported
/// instead of being sent to the app as a command it will reject.
/// </summary>
public class ToolArgumentTests
{
    [Fact]
    public void AWordIsTheCommandAndTheRestIsItsArgument()
    {
        Cli.Options options = Cli.Options.Parse(["click", "Save"]);

        Assert.Equal("click", options.Command);
        Assert.Equal("Save", options.Argument);
        Assert.Equal("click Save", options.Line);
        Assert.False(options.Help);
    }

    [Fact]
    public void ALabelWithSpacesStaysOneArgument()
    {
        Cli.Options options = Cli.Options.Parse(["type", "Hello", "world"]);

        Assert.Equal("Hello world", options.Argument);
        Assert.Equal("type Hello world", options.Line);
    }

    [Fact]
    public void FlagsBeforeTheSeparatorAreTheToolsAndAfterItAreTheApps()
    {
        Cli.Options options = Cli.Options.Parse(["tree", "--frames", "3", "--no-build", "--", "--page=2", "--dark"]);

        Assert.Equal("tree", options.Command);
        Assert.Equal(3, options.Frames);
        Assert.True(options.NoBuild);
        Assert.Equal(["--page=2", "--dark"], options.AppArgs);
    }

    [Fact]
    public void AFlagTheToolDoesNotKnowIsReportedRatherThanForwarded()
    {
        var error = Assert.Throws<ToolException>(() => Cli.Options.Parse(["tree", "--pgae=2"]));
        Assert.Contains("--pgae=2", error.Message, StringComparison.Ordinal);
        Assert.Contains("--", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoCommandIsHelp()
    {
        Assert.True(Cli.Options.Parse([]).Help);
        Assert.True(Cli.Options.Parse(["--frames", "2"]).Help);
        // Streaming supplies its commands on stdin, so it needs no command word.
        Assert.False(Cli.Options.Parse(["--stdin"]).Help);
    }

    [Fact]
    public void RunningTheToolWithoutACommandExplainsItself()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.Equal(2, Cli.Run([], output, error));
        Assert.Contains("dotnet cui", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("tree", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyAFramedAnswerLooksLikeOne()
    {
        // The app may print whatever it likes, so the reader has to recognize its own framing
        // and skip everything else: a line is a header only when it says OK/ERR and a length.
        Assert.True(HeadlessClient.IsHeader("CUI-OK 12"));
        Assert.True(HeadlessClient.IsHeader("CUI-ERR 0"));
        Assert.False(HeadlessClient.IsHeader("CUI-OK"));
        Assert.False(HeadlessClient.IsHeader("CUI-OK twelve"));
        Assert.False(HeadlessClient.IsHeader("frame at click 0"));
        Assert.False(HeadlessClient.IsHeader(""));
    }
}
