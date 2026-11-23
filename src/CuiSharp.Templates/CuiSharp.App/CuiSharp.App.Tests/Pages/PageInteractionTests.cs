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
        app.FindById("add-todo").ClickAndFrame();

        Assert.Equal(before + 1, todos.Items.Count);
        Assert.Equal("Milk", todos.Items[^1].Title);
        Assert.Equal("Milk", app.Find("Checkbox", "Milk").Text);
        Assert.Equal(string.Empty, app.FindById("draft").Value);
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

        app.FindById("draft").Click();
        app.Type("Milk");
        app.FindById("add-todo").ClickAndFrame();

        Assert.Contains(app.Tree.Descendants(), node => node.Text == "Sketch the onboarding flow");
        Assert.Equal("Milk", app.Find("Checkbox", "Milk").Text);
    }

    [Fact]
    public void EnterAddsTheDraftAndClearsItAfterAcceptance()
    {
        var todos = new TodoService();
        var before = todos.Items.Count;
        using var app = HeadlessApp.Start(new TodoPage(todos), TestHarness.Width, TestHarness.Height);

        app.FindById("draft").Click();
        app.Type("Milk");
        Assert.Equal("Milk", app.FindById("draft").Value);
        Assert.Equal(before, todos.Items.Count);

        app.Key(Key.Enter);

        Assert.Equal(before + 1, todos.Items.Count);
        Assert.Equal("Milk", todos.Items[^1].Title);
        Assert.Equal("Milk", app.Find("Checkbox", "Milk").Text);
        Assert.Equal(string.Empty, app.FindById("draft").Value);
    }

    [Fact]
    public void ARejectedDraftStaysInTheInputAndShowsFeedback()
    {
        var todos = new TodoService();
        var before = todos.Items.Count;
        using var app = HeadlessApp.Start(new TodoPage(todos), TestHarness.Width, TestHarness.Height);

        app.FindById("draft").Click();
        app.Type("   ").Key(Key.Enter);

        Assert.Equal(before, todos.Items.Count);
        Assert.Equal("   ", app.FindById("draft").Value);
        Assert.Contains(app.Tree.Descendants(), node => node.Text == "Enter a task before adding it.");
    }

    [Fact]
    public void PagesAreCreatedOnFirstVisitAndKeepTheirStateWhenRevisited()
    {
        var todoCreations = 0;
        var otherCreations = 0;
        using var shell = new App((TodoPage.PageTitle, CreateTodos), (OtherPage.PageTitle, CreateOther));
        Assert.Equal(0, todoCreations);
        Assert.Equal(0, otherCreations);

        using var app = HeadlessApp.Start(shell, TestHarness.Width, TestHarness.Height);
        Assert.Equal(1, todoCreations);
        Assert.Equal(0, otherCreations);

        app.FindById("draft").Click();
        app.Type("Milk");
        app.Find("Tab", OtherPage.PageTitle).ClickAndFrame();
        Assert.Equal(1, otherCreations);
        Assert.Equal(OtherPage.PageTitle, app.Find("Heading", OtherPage.PageTitle).Text);

        app.Find("Tab", "Todos").ClickAndFrame();
        Assert.Equal("Milk", app.FindById("draft").Value);
        Assert.Equal(1, todoCreations);
        app.Find("Tab", OtherPage.PageTitle).ClickAndFrame();
        Assert.Equal(1, otherCreations);
        Assert.Empty(app.Faults);

        TodoPage CreateTodos()
        {
            todoCreations++;
            return new TodoPage(new TodoService());
        }

        OtherPage CreateOther()
        {
            otherCreations++;
            return new OtherPage();
        }
    }

    [Fact]
    public void TheShellSwitchesPagesAndThemeByClickAndShortcut()
    {
        var shell = new App(
            (TodoPage.PageTitle, static () => new TodoPage(new TodoService())),
            (OtherPage.PageTitle, static () => new OtherPage()));

        using var app = HeadlessApp.Start(shell, TestHarness.Width, TestHarness.Height);

        Assert.Contains(app.Tree.Descendants(), node => node.Kind == "Heading" && node.Text == "Todos");

        app.Find("Tab", OtherPage.PageTitle).ClickAndFrame();
        Assert.Equal(OtherPage.PageTitle, app.Find("Heading", OtherPage.PageTitle).Text);

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

        surface.Draw(_ => mutation.Run(), pixels);

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

    private sealed class OtherPage : Component
    {
        public const string PageTitle = "Other page";

        protected override void Draw(Ui ui) => ui.Heading(PageTitle, 2);
    }
}
