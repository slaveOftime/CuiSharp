namespace CuiSharp.Tests;

/// <summary>
/// The newer generator mechanisms only fail at runtime: an array copied into the frame
/// arena, a record written back through a ref parameter, a text field that reads its
/// buffer back, and a scope whose end call reports a value. Each one is exercised in a
/// real headless frame here.
/// </summary>
public class GeneratedWrapperRuntimeTests
{
    private const int Width = 720;
    private const int Height = 520;

    [Fact]
    public void RecordArraysAndEditorStateSurviveAFrame()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(Width, Height);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        var likes = 3;
        var liked = false;
        var markdown = default(MarkdownResult);

        surface.Draw(ui =>
        {
            using (ui.Column(new() { Width = Size.Fill, Pad = 10 }))
            {
                ui.Treemap("tree", [new VizItem("A", 3, Color.Accent), new VizItem("B", 1, Color.Success)],
                    new() { Width = Size.Fill });
                ui.BarList("bars", [new VizItem("run", 2, Color.Info)]);
                ui.Reactions("reactions", [new Reaction("happy", 2, true), new Reaction("sad", 0, false)]);
                ui.Presence("people", [new PresenceUser("Ada", Color.Accent, 0, false)], 4, 28);
                ui.Comment("c", "Ada", "hello", ref likes, ref liked);
                ui.SpeedDial("dial", [new SpeedAction("plus", "Add"), new SpeedAction("trash", "Delete")]);
                ui.ChatMessage("hi there", ref markdown, new() { Markdown = true });
            }
        }, pixels);

        Assert.Contains(pixels, b => b != 0);
        Assert.Empty(surface.Faults);
        Assert.Equal(3, likes);
        Assert.False(liked);
    }

    [Fact]
    public void ATextFieldRentsABufferAndReadsItBack()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(Width, Height);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        var text = "hello";

        DrawFn draw = ui =>
        {
            using (ui.Column(new() { Width = Size.Fill, Pad = 10 }))
            {
                ui.InlineEdit("edit", ref text, 64);
                ui.PropText("Title", ref text, 64);
            }
        };

        surface.Draw(draw, pixels);
        // Steady state: nothing changed, so nothing is decoded and nothing allocates.
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5; i++) surface.Draw(draw, pixels);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal("hello", text);
        Assert.Equal(0, allocated);
        Assert.Empty(surface.Faults);
    }

    [Fact]
    public void AScopeWithAResultClosesAndReports()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(Width, Height);
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        int active = 0;
        var moved = default(KanbanMove);
        var closed = false;

        surface.Draw(ui =>
        {
            using (ui.Column(new() { Width = Size.Fill }))
            {
                active = ui.CardStack("stack", 3, stack =>
                {
                    using (stack.Column(new() { Gap = 8 }))
                    {
                        stack.Text("card one");
                        stack.Text("card two");
                    }
                }, new() { Height = 300 });

                var didMove = ui.Kanban("board", out moved, board => board.Text("todo"));
                Assert.False(didMove);
                closed = true;
            }
        }, pixels);

        Assert.True(closed);
        Assert.InRange(active, -1, 2);
        Assert.Empty(surface.Faults);
    }
}
