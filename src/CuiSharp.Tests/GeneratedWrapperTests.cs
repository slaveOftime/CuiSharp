using CuiSharp.Interop;

namespace CuiSharp.Tests;

public class GeneratedWrapperTests
{
    [Theory]
    [InlineData("Checkbox")]
    [InlineData("Switch")]
    [InlineData("Chip")]
    public void NativeKeyboardChangesAreWrittenBackThroughRefBool(string widget)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var surface = CuiSurface.Create(320, 200);
        bool value = false;
        bool Frame()
        {
            surface.BeginFrame();
            var ui = surface.Ui;
            bool changed;
            using (ui.Column(new() { Pad = 8 }))
            {
                changed = widget switch
                {
                    "Checkbox" => ui.Checkbox("toggle", ref value),
                    "Switch" => ui.Switch("toggle", ref value),
                    _ => ui.Chip("toggle", ref value),
                };
            }
            surface.EndFrame();
            return changed;
        }
        Frame();
        Native.InputKey(surface.Ui.Handle, Key.Tab, 1, 0);
        Assert.False(Frame()); // Focus the only control.
        Native.InputKey(surface.Ui.Handle, Key.Tab, 0, 0);
        Native.InputKey(surface.Ui.Handle, Key.Space, 1, 0);
        Assert.True(Frame());
        Assert.True(value);
        Native.InputKey(surface.Ui.Handle, Key.Space, 0, 0);
        Assert.False(Frame());
        Assert.True(value);
    }

    [Theory]
    [InlineData("Segmented")]
    [InlineData("Tabs")]
    public void IconArraysAreCheckedAgainstTheDerivedItemCount(string widget)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var surface = CuiSurface.Create(320, 200);
        surface.BeginFrame();
        var error = Assert.Throws<ArgumentException>(() =>
        {
            int selected = 0;
            if (widget == "Segmented") surface.Ui.Segmented("s", ["one", "two"], ref selected,
                new SegmentedOpts { Icons = [Icons.Plus] });
            else surface.Ui.Tabs("t", ["one", "two"], ref selected, new TabsOpts { Icons = [Icons.Plus] });
        });
        Assert.Equal("o", error.ParamName);
        surface.EndFrame();
    }

    [Fact]
    public void StringSpansAreCopiedBeforeDeferredPaint()
    {
        if (!OperatingSystem.IsWindows()) return;
        static byte[] Render(bool overwrite)
        {
            using var surface = CuiSurface.Create(320, 200);
            string[] labels = ["unused", "one", "two", "unused"];
            var pixels = new byte[320 * 200 * 4];
            surface.BeginFrame();
            var ui = surface.Ui;
            using (ui.Column(new() { Pad = 8 }))
            {
                int selected = 0;
                ui.Segmented("span", labels.AsSpan(1, 2), ref selected);
                if (overwrite) Array.Fill(labels, "corrupted");
            }
            surface.EndFrame();
            surface.Render(pixels);
            return pixels;
        }
        Assert.Equal(Render(false), Render(true));
    }

    [Fact]
    public void GeneratedOpenCallbackBalancesIdsOnExceptions()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var surface = CuiSurface.Create(320, 200);
        surface.BeginFrame();
        var ui = surface.Ui;
        using (ui.Column())
        {
            uint before = ui.MakeId("sentinel");
            ui.Button("anchor");
            ui.PopoverOpen("open");
            bool ran = false;
            try
            {
                ui.Popover("open", body =>
                {
                    ran = true;
                    body.Text("content");
                    throw new ApplicationException();
                });
            }
            catch (ApplicationException) { }
            Assert.True(ran);
            Assert.Equal(before, ui.MakeId("sentinel"));
            ui.Text("sibling");
        }
        surface.EndFrame();
    }

    [Fact]
    public void RecordArraysAreCopiedBeforeNativeReadsThem()
    {
        if (!OperatingSystem.IsWindows()) return;
        static byte[] Render(bool overwrite)
        {
            using var surface = CuiSurface.Create(320, 200);
            TableColumn[] columns = [new("Name", Size.Px(120)), new("Role", Size.Fill)];
            var pixels = new byte[320 * 200 * 4];
            surface.BeginFrame();
            var ui = surface.Ui;
            var sort = new TableSort();
            using (var table = ui.Table("records", columns, ref sort))
            {
                // The wrapper converts the span into the frame arena, so a later
                // mutation of the caller's array must not change the drawn table.
                if (overwrite) Array.Fill(columns, new TableColumn("changed"));
                if (table)
                {
                    ui.TableRow(false);
                    ui.TableCellText("Ada");
                    ui.TableCellText("Engineer");
                }
            }
            surface.EndFrame();
            surface.Render(pixels);
            return pixels;
        }
        Assert.Equal(Render(false), Render(true));
    }

    private sealed class SplitState { public float Ratio = 0.25f; }

    [Fact]
    public void RetainedSplitRatioUsesStableFrameStorage()
    {
        if (!OperatingSystem.IsWindows()) return;
        static byte[] Render(bool collect)
        {
            using var surface = CuiSurface.Create(320, 200);
            var pixels = new byte[320 * 200 * 4];
            var state = new SplitState();
            surface.BeginFrame();
            var ui = surface.Ui;
            using (ui.Split("split", ref state.Ratio, new() { MinFirst = 8, MinSecond = 8 }))
            {
                ui.Text("first");
                if (collect)
                {
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    // SplitNext must read the begin-call snapshot, not a moved
                    // managed field or a later change to application state.
                    state.Ratio = 0.75f;
                }
                ui.SplitNext();
                ui.Text("second");
            }
            surface.EndFrame();
            surface.Render(pixels);
            GC.KeepAlive(state);
            return pixels;
        }
        Assert.Equal(Render(false), Render(true));
    }

    [Fact]
    public unsafe void NumericSpansUseCopiedArenaStorage()
    {
        Utf8.BeginFrame();
        Span<float> samples = stackalloc float[] { 3, 7 };
        nint pointer = UiMarshal.Values<float>(samples);
        samples.Fill(100);
        Assert.Equal(3f, ((float*)pointer)[0]);
        Assert.Equal(7f, ((float*)pointer)[1]);
        Assert.Equal((nint)0, UiMarshal.Values<float>([]));
        Assert.Equal((nint)0, UiMarshal.Strings([]));
    }
}
