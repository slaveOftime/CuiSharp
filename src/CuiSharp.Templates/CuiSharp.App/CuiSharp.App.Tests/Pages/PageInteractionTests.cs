using CuiSharp;
using CuiSharp.App.Pages;
using CuiSharp.App.Services;
using CuiSharp.Hosting;

namespace CuiSharp.App.Tests.Pages;

/// <summary>
/// Headless UI interaction tests driving real frames, clicks, keyboard input, and mutations.
/// </summary>
public class PageInteractionTests
{
    [Fact]
    public void TypingAndClickingAddAppendsATodoItem()
    {
        var todos = new TodoService();
        var before = todos.Items.Count;

        using var app = HeadlessApp.Start(new TodoPage(todos), TestHarness.Width, TestHarness.Height);

        app.FindById("draft").Click();
        app.Type("Milk");
        app.FindButton("Add").ClickAndFrame();

        Assert.Equal(before + 1, todos.Items.Count);
        Assert.Equal("Milk", todos.Items[^1].Title);
        Assert.Contains(app.Tree.Descendants(), node => node.Text == "Milk");
    }

    [Fact]
    public void TogglingAnItemAndClearingCompletedRemovesOnlyDoneItems()
    {
        var todos = new TodoService();

        using var app = HeadlessApp.Start(new TodoPage(todos), TestHarness.Width, TestHarness.Height);

        app.Find("Checkbox", "Sketch the onboarding flow").ClickAndFrame();

        Assert.Equal(1, todos.DoneCount);
        Assert.Contains(app.Tree.Descendants(), node => node.Kind == "Badge" && node.Text == "Done");

        app.FindButton("Clear completed").ClickAndFrame();

        Assert.Equal(0, todos.DoneCount);
        Assert.Equal(2, todos.Items.Count);
        Assert.DoesNotContain(app.Tree.Descendants(), node => node.Text == "Sketch the onboarding flow");
    }

    [Fact]
    public void TheFilterSegmentsShowOnlyMatchingItems()
    {
        using var app = HeadlessApp.Start(new TodoPage(new TodoService()), TestHarness.Width, TestHarness.Height);

        Assert.Contains(app.Tree.Descendants(), node => node.Text == "Sketch the onboarding flow");

        app.Find("Segment", "Done").ClickAndFrame();
        Assert.DoesNotContain(app.Tree.Descendants(), node => node.Text == "Sketch the onboarding flow");
        Assert.Contains(app.Tree.Descendants(), node => node.Text == "Nothing done yet");

        app.Find("Segment", "All").ClickAndFrame();
        Assert.Contains(app.Tree.Descendants(), node => node.Text == "Sketch the onboarding flow");
    }

    [Fact]
    public void SubmittingTheDraftWithEnterAddsAnItemAndResetsTheFilter()
    {
        var todos = new TodoService();

        using var app = HeadlessApp.Start(new TodoPage(todos), TestHarness.Width, TestHarness.Height);

        app.Find("Segment", "Done").ClickAndFrame();
        Assert.Contains(app.Tree.Descendants(), node => node.Text == "Nothing done yet");

        app.FindById("draft").Click();
        app.Type("Ship 1.0").Key(Key.Enter);

        Assert.Equal("Ship 1.0", todos.Items[^1].Title);
        Assert.Contains(app.Tree.Descendants(), node => node.Text == "Ship 1.0");
        Assert.DoesNotContain(app.Tree.Descendants(), node => node.Text == "Nothing done yet");
    }

    [Fact]
    public void TheShellSwitchesPagesAndThemeByClickAndShortcut()
    {
        var shell = new App(new TodoPage(new TodoService()), TestHarness.CreateMarketsPage(historyPoints: 120));

        using var app = HeadlessApp.Start(shell, TestHarness.Width, TestHarness.Height);

        Assert.Contains(app.Tree.Descendants(), node => node.Kind == "Heading" && node.Text == "Todos");

        app.Find("Tab", "Markets").ClickAndFrame();
        Assert.Contains(app.Tree.Descendants(), node => node.Kind == "Heading" && node.Text == "Markets");

        app.Key(Key.Tab, Mods.Ctrl);
        Assert.Contains(app.Tree.Descendants(), node => node.Kind == "Heading" && node.Text == "Todos");

        app.Find("Segment", "Dark").ClickAndFrame();
        Assert.Equal(Theme.Dark, app.Surface.Ui.Theme);

        app.Key(Key.J, Mods.Ctrl);
        Assert.Equal(Theme.Light, app.Surface.Ui.Theme);
    }

    [Fact]
    public void AMutationAppliesItsResultOnTheNextFrameBoundary()
    {
        var completion = new TaskCompletionSource<int>();
        var applied = -1;

        using var mutation = new Mutation<int>(_ => completion.Task, value => applied = value);
        using var surface = CuiSurface.Create(64, 64);

        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];

        mutation.Run();
        surface.Draw(_ => { }, pixels);

        Assert.True(mutation.IsRunning);
        Assert.Equal(-1, applied);

        completion.SetResult(7);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            surface.Draw(_ => { }, pixels);
            return !mutation.IsRunning && applied == 7;
        }, TimeSpan.FromSeconds(5)));

        Assert.Equal(7, applied);
        Assert.Null(mutation.Error);
    }
}
