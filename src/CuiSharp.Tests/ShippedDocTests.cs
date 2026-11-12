using System.Text;
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
                m => Render(m.Element("summary")!),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// A summary's text with its inline markup intact. <c>XElement.Value</c> would read as the
    /// documentation were plain text: it drops the element nodes, so `Draws &lt;paramref name="x"/&gt;`
    /// comes back as `Draws ,` and an assertion on it would be measuring the reader, not the docs.
    /// </summary>
    private static string Render(XElement summary)
    {
        var text = new StringBuilder();
        foreach (XNode node in summary.Nodes())
        {
            switch (node)
            {
                case XText literal:
                    text.Append(literal.Value);
                    break;
                case XElement { Name.LocalName: "paramref" or "typeparamref" } reference:
                    text.Append(reference.Attribute("name")?.Value);
                    break;
                case XElement { Name.LocalName: "see" } reference:
                    // `see cref` names the member as `M:Type.Member(params)`: the last segment of the
                    // name, without the parameter list, is what a reader would have seen inline.
                    string named = reference.Attribute("cref")?.Value ?? reference.Value;
                    int parameters = named.IndexOf('(', StringComparison.Ordinal);
                    if (parameters >= 0) named = named[..parameters];
                    text.Append(named[(named.LastIndexOf('.') + 1)..]);
                    break;
                default:
                    text.Append(node.ToString(SaveOptions.DisableFormatting));
                    break;
            }
        }

        return text.ToString().Trim();
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
    public void ADescriptionAttachedToTheItemRecordStillReachesTheWidget()
    {
        // CUI writes Blame's paragraph above `cui_blame_commit`, the item record, because that is the
        // first declaration after the section title — libclang attaches it there and `cui_blame_opts`
        // is left bare. The widget is the reader's entry point, so it takes the sentence.
        Assert.StartsWith("Source with who last changed each line:",
            Summaries.Value["T:CuiSharp.BlameOpts"], StringComparison.Ordinal);
        Assert.Contains(Summaries.Value, entry =>
            entry.Key.Contains("UiExtensions.<G>", StringComparison.Ordinal)
            && entry.Key.Contains(".Blame(", StringComparison.Ordinal)
            && entry.Value.StartsWith("Source with who last changed each line:", StringComparison.Ordinal));
    }

    [Fact]
    public void NoSummaryIsAnInternalNote()
    {
        // `left to right in any interface: see cui__keep_ltr` is written above fifteen thin wrappers
        // in CUI's sources, and it reached the wrappers as their summary: a sentence about the
        // implementation, in place of the description the header does have. The note also arrived
        // with its opening delimiter attached — `* left to right …` — which is the bug that let it
        // through in the first place.
        string[] notes = [.. Summaries.Value
            .Where(e => e.Value.StartsWith('*') || e.Value.Contains("cui__keep_ltr", StringComparison.Ordinal))
            .Select(e => $"{e.Key} = {e.Value}")];

        Assert.True(notes.Length == 0, "a summary is an internal note: " + string.Join(" | ", notes.Take(3)));
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
