namespace CuiSharp.Tests;

/// <summary>
/// The vocabulary a search uses. It has to be readable in a shell and unambiguous for the
/// three shapes worth knowing; anything else is a label, because a label may contain
/// anything.
/// </summary>
public class WidgetQueryTests
{
    [Fact]
    public void ALabelIsALabel()
    {
        WidgetQuery query = WidgetQuery.Parse("Save");

        Assert.Equal("Save", query.Text);
        Assert.Null(query.Kind);
        Assert.Empty(query.Path);
    }

    [Fact]
    public void AKindAndALabelSayWhichOne()
    {
        WidgetQuery query = WidgetQuery.Parse("Button \"Save\"");

        Assert.Equal("Save", query.Text);
        Assert.Equal("Button", query.Kind);
    }

    [Fact]
    public void APathNarrowsTheSearchAndOnlyItsLeafCanBeTyped()
    {
        WidgetQuery query = WidgetQuery.Parse("Card \"editor\" > Row \"actions\" > Button \"Save\"");

        Assert.Equal(["Card \"editor\"", "Row \"actions\""], query.Path);
        Assert.Equal("Save", query.Text);
        Assert.Equal("Button", query.Kind);
    }

    [Fact]
    public void ALabelThatLooksLikeAQueryIsStillALabel()
    {
        // Two words before the quote is a label, not a kind: only `Kind "text"` has a kind.
        WidgetQuery label = WidgetQuery.Parse("Save now \"later\"");
        Assert.Null(label.Kind);
        Assert.Equal("Save now \"later\"", label.Text);

        // A lower-case word before a quote is not a kind: no wrapper is spelled that way.
        WidgetQuery lower = WidgetQuery.Parse("say \"hi\"");
        Assert.Null(lower.Kind);
        Assert.Equal("say \"hi\"", lower.Text);
    }

    [Fact]
    public void TheQueryPrintsBackTheWayItWasWritten()
    {
        Assert.Equal("Save", WidgetQuery.Parse("Save").ToString());
        Assert.Equal("Button \"Save\"", WidgetQuery.Parse("Button \"Save\"").ToString());
        Assert.Equal("editor > Save", WidgetQuery.Parse("editor > Save").ToString());
    }
}
