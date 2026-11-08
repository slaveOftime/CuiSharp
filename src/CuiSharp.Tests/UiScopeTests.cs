namespace CuiSharp.Tests;

public class UiScopeTests
{
    [Fact]
    public void DefaultScopesAreClosedAndSafeToDispose()
    {
        LayoutScope layout = default;
        layout.Dispose();
        layout.Dispose();
        OpenScope conditional = default;
        Assert.False(conditional.Open);
        Assert.False(conditional);
        conditional.Dispose();
        conditional.Dispose();
    }

    [Fact]
    public void ClosedScopesSkipCallbacksAndLeaveIdsBalanced()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var surface = CuiSurface.Create(320, 200);
        surface.BeginFrame();
        var ui = surface.Ui;
        using (ui.Column())
        {
            uint before = ui.MakeId("sentinel");
            bool open = false;
            // These tests are about the scope itself, so they read Open through the assertion
            // instead of guarding the body: the analyzer cannot see that.
#pragma warning disable CUI003
            using (var scope = ui.Tree("closed", ref open)) Assert.False(scope);
#pragma warning restore CUI003
            using (ui.Card(new(), out bool clicked))
            {
                Assert.False(clicked); // A card opens even though its bool result means "clicked".
                ui.Label("card body");
            }
            DrawFn unexpected = static _ => throw new InvalidOperationException("Closed body executed");
            ui.Menu("closed menu", unexpected);
            ui.ContextMenu("closed context", unexpected);
            ui.Popover("closed popover", unexpected);
            ui.Dialog("closed dialog", ref open, unexpected);
            ui.Sheet("closed sheet", ref open, unexpected);
            ui.Tree("closed callback tree", ref open, unexpected);
            ui.Accordion("closed accordion", ref open, unexpected);
            Assert.Equal(before, ui.MakeId("sentinel"));
        }
        surface.EndFrame();
    }

    [Fact]
    public void OpenCallbackAndManualScopesBalanceIdsEvenOnExceptions()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var surface = CuiSurface.Create(320, 200);
        surface.BeginFrame();
        var ui = surface.Ui;
        using (ui.Column())
        {
            uint before = ui.MakeId("sentinel");
            bool open = true;
            try
            {
                ui.Tree("callback tree", ref open, static body =>
                {
                    body.Label("body");
                    throw new ApplicationException();
                });
                Assert.Fail("The body did not execute");
            }
            catch (ApplicationException) { }
            Assert.Equal(before, ui.MakeId("sentinel"));

            try
            {
#pragma warning disable CUI003 // the assertion below is the Open check
                using var scope = ui.Tree("manual tree", ref open);
                Assert.True(scope);
#pragma warning restore CUI003
                ui.Label("body");
                throw new ApplicationException();
            }
            catch (ApplicationException) { }
            Assert.Equal(before, ui.MakeId("sentinel"));
            ui.Label("sibling");
        }
        surface.EndFrame();
    }

    [Fact]
    public void IdScopesSeparateEqualLabelsAndKeepTheStackBalanced()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var surface = CuiSurface.Create(320, 200);
        surface.BeginFrame();
        var ui = surface.Ui;
        using (ui.Column())
        {
            uint baseline = ui.MakeId("sentinel");
            uint first, second;
            using (ui.Id(7)) first = ui.MakeId("same label");
            using (ui.Id(8)) second = ui.MakeId("same label");
            Assert.NotEqual(first, second);
            Assert.Equal(baseline, ui.MakeId("sentinel"));

            // Keyed enumerates inside id scopes and pops the last one on exit.
            int[] rows = [3, 4, 5];
            uint[] keys = new uint[rows.Length];
            int i = 0;
            foreach (int row in ui.Keyed(rows, static (in int r) => r))
                keys[i++] = ui.MakeId("row");
            Assert.Equal(3, i);
            Assert.Equal(3, keys.Distinct().Count());
            Assert.Equal(baseline, ui.MakeId("sentinel"));
        }
        surface.EndFrame();
    }

    [Fact]
    public void LayoutDisposalOnExceptionsPreservesRendering()
    {
        if (!OperatingSystem.IsWindows()) return;
        static byte[] Render(bool throwInside)
        {
            using var surface = CuiSurface.Create(320, 200);
            var pixels = new byte[320 * 200 * 4];
            surface.Draw(ui =>
            {
                using (ui.Column(new() { Pad = 8 }))
                {
                    try
                    {
                        using var row = ui.Row(new() { Gap = 8 });
                        ui.Label("first");
                        if (throwInside) throw new ApplicationException();
                    }
                    catch (ApplicationException) { }
                    ui.Label("sibling");
                }
            }, pixels);
            return pixels;
        }
        Assert.Equal(Render(false), Render(true));
    }
}
