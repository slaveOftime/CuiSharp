using CuiSharp.App.Pages;
using CuiSharp.App.Services;
using CuiSharp.Hosting;

namespace CuiSharp.App.Tests.Pages;

/// <summary>
/// Headless render and steady-state 0 B/frame allocation tests for every screen.
/// </summary>
public class PageRenderTests
{
    [Fact]
    public void TheTodoPageDrawsItsSeededItemsWithNoFaults()
    {
        using var app = HeadlessApp.Start(new TodoPage(new TodoService()), TestHarness.Width, TestHarness.Height);

        Assert.True(app.PaintedAnything());
        Assert.Empty(app.Faults);
        Assert.Contains(app.Tree.Descendants(), node => node.Text == "Sketch the onboarding flow");
        Assert.Contains(app.Tree.Descendants(), node => node.Text == "3 open");
    }

    [Fact]
    public void TheTodoPageAllocatesNothingPerFrame()
    {
        using var page = new TodoPage(new TodoService());

        TestHarness.AssertZeroAllocations(page);
    }

    [Fact]
    public void TheTodoPageAllocatesNothingOnceItHasBeenEdited()
    {
        var todos = new TodoService();
        using var page = new TodoPage(todos);

        // Keep the draft nonempty while measuring; Add would clear it and hide draft-only allocations.
        TestHarness.AssertZeroAllocations(page, prepare: app =>
        {
            app.FindById("draft").Click();
            app.Type("Milk");
        }, verify: app =>
        {
            app.FindById("add-todo").Click();
            Assert.Contains(todos.Items, item => item.Title == "Milk");
        });
    }

    [Fact]
    public void TheTodoPageAllocatesNothingAfterAddingAnItem()
    {
        var todos = new TodoService();
        using var page = new TodoPage(todos);

        TestHarness.AssertZeroAllocations(page, prepare: app =>
        {
            app.FindById("draft").Click();
            app.Type("Milk");
            app.FindById("add-todo").Click();
            Assert.Contains(todos.Items, item => item.Title == "Milk");
        });
    }

    [Fact]
    public void TheShellRegionsKeepPageControlsOutOfTheThemeSwitch()
    {
        using var app = HeadlessApp.Start(
            new App((TodoPage.PageTitle, static () => new TodoPage(new TodoService()))),
            TestHarness.Width, TestHarness.Height);

        var tree = app.Tree;
        var header = tree.Find("header");
        var page = tree.Find("page");
        var footer = tree.Find("footer");

        Assert.Same(header.Parent, page.Parent);
        Assert.Same(header.Parent, footer.Parent);
        Assert.Contains(page.Find("todo-composer").Descendants(), node => node.AutomationId == "draft");
        Assert.DoesNotContain(header.Descendants(), node => node.AutomationId == "draft");
        Assert.Equal("Segment", tree.Find("theme > Dark").Kind);

        Assert.DoesNotContain(header.Descendants(), node => node.Kind == "Tabs");
        Assert.Empty(app.Faults);
    }

    [Fact]
    public void TheTodoShellAllocatesNothingPerSteadyFrame()
    {
        using var shell = new App((TodoPage.PageTitle, static () => new TodoPage(new TodoService())));

        TestHarness.AssertZeroAllocations(shell,
            verify: app => Assert.Equal(TodoPage.PageTitle, app.Find("Heading", TodoPage.PageTitle).Text));
    }
}
