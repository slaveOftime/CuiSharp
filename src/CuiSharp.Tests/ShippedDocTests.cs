using System.Xml.Linq;

namespace CuiSharp.Tests;

/// <summary>
/// The documentation a package consumer reads. CUI writes most of its documentation in the headers —
/// above a struct, beside a declaration, next to an enum constant — and the binding's job is to carry
/// it out to IntelliSense. <c>CuiSharp.xml</c> is that artifact: it is what an IDE shows for a member
/// of the package, so the assertions here run on it rather than on the generator's own output.
/// </summary>
public class ShippedDocTests
{
    private static readonly Lazy<Dictionary<string, string>> Summaries = new(Load);

    private static Dictionary<string, string> Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "CuiSharp.xml");
        Assert.True(File.Exists(path), $"{path} is the documentation file the package ships");

        return XDocument.Load(path).Descendants("member")
            .Where(m => m.Element("summary") is not null)
            .ToDictionary(
                m => m.Attribute("name")!.Value,
                m => m.Element("summary")!.Value.Trim(),
                StringComparer.Ordinal);
    }

    /// <summary>Every summary that exists, for the rules below that hold for all of them.</summary>
    private static IEnumerable<string> All => Summaries.Value.Values;

    [Fact]
    public void AConstantSaysWhatItMeans()
    {
        // `CUI_AXIS_HOURS, /* hours shown as a time of day: 12.25 is 12:15 */` — a constant's
        // meaning is written beside it, and a dev choosing one should not have to read cui.h.
        Assert.Contains("time of day", Summaries.Value["F:CuiSharp.AxisUnits.Hours"], StringComparison.Ordinal);
    }

    [Fact]
    public void AFunctionDocumentedBesideItsDeclarationKeepsThatSentence()
    {
        // `CUI_API void cui_spacer(cui_ctx *ui);   /* flexible space */`: libclang attaches only a
        // leading comment, so the beside-comment is read off the line by the generator.
        Assert.Contains(Summaries.Value, entry =>
            entry.Key.StartsWith("M:CuiSharp.Interop.Native.Spacer(", StringComparison.Ordinal)
            && entry.Value == "flexible space");
    }

    [Fact]
    public void AnOptionsStructSaysWhatTheComponentIs()
    {
        // The paragraph above `cui_split_opts` explains the component; the summary of the struct a
        // caller fills in should be that sentence, not the struct's C name.
        Assert.StartsWith("Resizable split view:", Summaries.Value["T:CuiSharp.SplitOpts"], StringComparison.Ordinal);
        Assert.Contains("after values in the readout",
            Summaries.Value["F:CuiSharp.DotChartOpts.Unit"], StringComparison.Ordinal);
    }

    [Fact]
    public void ASectionTitleIsNotASummary()
    {
        // The block above `cui_check_opts` is the *Selection controls* title bar. libclang attaches
        // it to the struct, so without the banner rule the summary reads "Selection controls".
        Assert.DoesNotContain(Summaries.Value, entry =>
            entry.Value.StartsWith("Selection controls", StringComparison.Ordinal));
    }

    [Fact]
    public void AWidgetWithoutACommentOfItsOwnTakesItsComponentsDescription()
    {
        // `cui_dot_display_ex` has no comment; the paragraph above `cui_dot_display_opts` describes
        // the component, and that is the sentence a reader wants beside the call.
        Assert.Contains(Summaries.Value, entry =>
            entry.Key.Contains("UiExtensions", StringComparison.Ordinal)
            && entry.Key.Contains("DotDisplay", StringComparison.Ordinal)
            && entry.Value.StartsWith("Dot-matrix display:", StringComparison.Ordinal));
    }

    [Fact]
    public void NoSummaryOpensWithASectionRuler()
    {
        // CUI separates sections with `---- Terminal ----`, and libclang attaches that block to the
        // declaration underneath: a summary that starts with the ruler is a bug a reader sees.
        string[] ruled = [.. All.Where(text => text.Contains("----", StringComparison.Ordinal))];

        Assert.True(ruled.Length == 0, "a summary carries a section ruler: " + string.Join(" | ", ruled.Take(3)));
    }
}
