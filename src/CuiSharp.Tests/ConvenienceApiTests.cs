using System.Runtime.InteropServices;
using CuiSharp.Hosting;
using CuiSharp.Interop;
using CuiSharp.Testing;

namespace CuiSharp.Tests;

public class ConvenienceApiTests
{
    [Fact]
    public void InputRecordCountsFollowTheArraysIncludingReplacementAndNull()
    {
        using var surface = CuiSurface.Create(320, 200);
        surface.BeginFrame();
        var field = new QueryField("State", QueryType.Choice, ["open", "closed"]);
        var native = field.ToNative();
        Assert.Equal(2, native.ChoiceCount);
        Assert.Equal("closed", Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(native.Choices, IntPtr.Size)));
        field.Choices = ["merged"];
        Assert.Equal(1, field.ToNative().ChoiceCount);
        field.Choices = null;
        Assert.Equal(0, field.ToNative().ChoiceCount);
        Assert.Equal(0, field.ToNative().Choices);

        var endpoint = new ApiEndpoint("GET", Params: [new ApiParam("id"), new ApiParam("limit")]);
        var api = endpoint.ToNative();
        Assert.Equal(2, api.ParamCount);
        endpoint.Params = [];
        Assert.Equal(0, endpoint.ToNative().ParamCount);
        endpoint.Params = null;
        Assert.Equal(0, endpoint.ToNative().ParamCount);

        Assert.Equal(3, new UptimeService("api", [1f, 0.99f, 1f]).ToNative().DayCount);
        Assert.Equal(2, new StackFrame("main", Source: ["a", "b"]).ToNative().SourceCount);
        Assert.Equal(2, new FilterField("Status", Options: ["Open", "Closed"]).ToNative().OptionCount);
        surface.EndFrame();
    }

    [Fact]
    public void AValueSegmentReportsOnlyTheFrameThatChangedIt()
    {
        var page = new ValueSegments();
        using var app = TestApp.Start(page, 400, 200);
        Assert.Null(page.Change);
        app.FindItem("modes", 1).Click();
        Assert.Equal(1, page.Value);
        Assert.Equal(1, page.Changes);
        app.Frame(2);
        Assert.Null(page.Change);
        Assert.Equal(1, page.Changes);
        app.FindItem("modes", 1).Click();
        Assert.Equal(1, page.Changes);
    }

    [Fact]
    public void GridSelectionAndCursorArePublishedAfterRowsAndSurviveTheNextFrame()
    {
        var page = new GridPage();
        using var app = TestApp.Start(page, 400, 260);
        Assert.InRange(page.State.Last, 1, 4);
        var cell = app.Surface.Ui.RectOf(page.RowId);
        app.MoveTo(cell.X + cell.W / 2, cell.Y + cell.H / 2);
        app.Press();
        app.Release();
        Assert.Equal([false, false, true, false], page.State.Selected);
        Assert.Equal(2, page.State.Cursor);
        Assert.Equal(2, page.State.Anchor);
        app.Frame(2);
        Assert.Equal([false, false, true, false], page.State.Selected);
        Assert.Equal(2, page.State.Cursor);
        app.Key(Key.Down, Mods.Shift);
        Assert.Equal(3, page.State.Cursor);
        Assert.Equal(3, page.CursorDuringRows);
        Assert.Equal([false, false, true, true], page.State.Selected);
        Assert.Equal("DataGrid", app.Tree.Find("row 2").Parent!.Kind);
    }

    [Fact]
    public void GridHeaderSortIsPublishedAndTogglesDirection()
    {
        var page = new GridPage();
        using var app = TestApp.Start(page, 400, 260);
        var grid = app.Find("DataGrid", "rows").Rect;
        Assert.True(grid.H > 36);
        app.MoveTo(grid.X + 120, grid.Y + 18).Press().Release();
        Assert.Equal(0, page.State.Sort.Column);
        Assert.True(page.State.Sort.Descending);
        Assert.True(page.SortDuringRows.Descending);
        app.MoveTo(grid.X + 120, grid.Y + 18).Press().Release();
        Assert.False(page.State.Sort.Descending);
        Assert.False(page.SortDuringRows.Descending);
    }

    [Fact]
    public void GridRejectsShortSelectionBeforeNativeCanReadIt()
    {
        using var surface = CuiSurface.Create(320, 200);
        surface.BeginFrame();
        Assert.Throws<ArgumentException>(() => surface.Ui.DataGrid("grid", [], 2, new GridState(1), static _ => { }));
        surface.Ui.Text("still balanced");
        surface.EndFrame();
    }

    [Fact]
    public void GridCallbackClosesAndWritesBackWhenTheBodyThrows()
    {
        using var surface = CuiSurface.Create(320, 200);
        var state = new GridState(4);
        surface.BeginFrame();
        uint before = surface.Ui.MakeId("sentinel");
        bool ran = false;
        Assert.Throws<ApplicationException>(() => surface.Ui.DataGrid("grid", [new GridColumn("Name")], 4, state, ui =>
        {
            ran = true;
            ui.DataGridRow(0);
            ui.DataGridCellText("first");
            throw new ApplicationException();
        }));
        Assert.True(ran);
        Assert.Equal(before, surface.Ui.MakeId("sentinel"));
        surface.Ui.Text("sibling");
        surface.EndFrame();
    }

    [Fact]
    public void ConvenienceControlsAllocateNothingInSteadyState()
    {
        var page = new GridPage();
        using var app = TestApp.Start(page, 400, 260);
        app.Frame(20);
        for (int i = 0; i < 10; i++)
        {
            app.Frame();
            Assert.Equal(0, app.Surface.LastFrame.AllocatedBytes);
        }
    }

    [Fact]
    public void ConciseLayoutOptionsMoveChildrenExactlyWhereTheCanonicalStructDoes()
    {
        using var app = TestApp.Start(new LayoutParityPage(), 480, 640);

        // Row gap: the space between children is the requested gap, whichever struct built it.
        static float Gap(TestApp app, string first, string second) =>
            (int)Math.Round(app.FindButton(second).Rect.X
                - (app.FindButton(first).Rect.X + app.FindButton(first).Rect.W));
        Assert.Equal(24, Gap(app, "gap canon a", "gap canon b"));
        Assert.Equal(24, Gap(app, "gap concise a", "gap concise b"));
        Assert.Equal(Gap(app, "gap canon a", "gap canon b"), Gap(app, "gap concise a", "gap concise b"));

        // Column pad: the first child starts one pad below where an unpadded stack would put it.
        static float Drop(TestApp app, string from, string to) =>
            (int)Math.Round(app.FindButton(to).Rect.Y - (app.FindButton(from).Rect.Y + app.FindButton(from).Rect.H));
        float baseDrop = Drop(app, "pad base a", "pad base b");
        Assert.Equal(18 + baseDrop, Drop(app, "pad base b", "pad canon"));
        Assert.Equal(Drop(app, "pad base b", "pad canon"), Drop(app, "pad base c", "pad concise"));

        // Card dir + gap: Dir.Row puts the children side by side twelve points apart.
        var canonA = app.FindButton("card canon a").Rect;
        var canonB = app.FindButton("card canon b").Rect;
        var conciseA = app.FindButton("card concise a").Rect;
        var conciseB = app.FindButton("card concise b").Rect;
        // Labels differ per variant, so compare intra-card geometry, never absolute X.
        Assert.Equal((int)Math.Round(canonB.X - (canonA.X + canonA.W)), (int)Math.Round(conciseB.X - (conciseA.X + conciseA.W)));
        Assert.Equal(12, (int)Math.Round(conciseB.X - (conciseA.X + conciseA.W)));
        Assert.Equal(conciseA.Y, conciseB.Y, 1);
        // Without Dir.Row the same pad and gap stacks the children, so the axis is the ctor's doing.
        var stackA = app.FindButton("card stack a").Rect;
        var stackB = app.FindButton("card stack b").Rect;
        Assert.True(stackB.Y > stackA.Y + stackA.H - 1);

        // Text size: the next widget sits where the sized line ends, identically for both spellings.
        static float LineDrop(TestApp app, string text, string after) =>
            (int)Math.Round(app.FindButton(after).Rect.Y - app.Find("Text", text).Rect.Y);
        Assert.Equal(LineDrop(app, "size canon", "after size canon"), LineDrop(app, "size concise", "after size concise"));
        Assert.True(LineDrop(app, "size concise", "after size concise")
            > LineDrop(app, "size base", "after size base"));
    }

    [Fact]
    public void ConciseConstructorsHandNativeExactlyWhatTheEquivalentInitializerDoes()
    {
        AssertSameBytes(new Box(gap: 12, pad: 4, align: Align.Center, width: Size.Px(240), height: Size.Px(100)).ToNative(),
            new Box { Gap = 12, Pad = 4, Align = Align.Center, Width = Size.Px(240), Height = Size.Px(100) }.ToNative());
        AssertSameBytes(new Box().ToNative(), default(Box).ToNative());
        AssertSameBytes(new Card(pad: 16, gap: 12, dir: Dir.Row, width: Size.Px(300), height: Size.Px(200)).ToNative(),
            new Card { Pad = 16, Gap = 12, Dir = Dir.Row, Width = Size.Px(300), Height = Size.Px(200) }.ToNative());
        AssertSameBytes(new Card().ToNative(), default(Card).ToNative());
        AssertSameBytes(new TextOpts(size: 12, color: Color.TextSecondary).ToNative(),
            new TextOpts { Size = 12, Color = Color.TextSecondary }.ToNative());
        AssertSameBytes(new BadgeOpts(Tone.Danger).ToNative(), new BadgeOpts { Tone = Tone.Danger }.ToNative());
        AssertSameBytes(new BadgeOpts().ToNative(), default(BadgeOpts).ToNative());
    }

    [Fact]
    public void AFrameContinuesInItsParentLayoutAfterAConciseContainerBodyThrows()
    {
        var page = new DoomedColumnPage();
        using var app = TestApp.Start(page, 400, 300);
        Assert.True(page.Threw);

        // The sibling drew in the same frame after the throw. The doomed column's using closed on
        // the way out, so the sibling sits in the root column below the padded box: at least the
        // column's 24 bottom pad under the last child it drew. A leaked container would have
        // adopted the sibling inside its padding, placing it right under "doomed".
        var doomed = app.FindButton("doomed").Rect;
        var sibling = app.FindButton("sibling").Rect;
        Assert.True(sibling.Y > doomed.Y + doomed.H,
            "the sibling drew back over the doomed container's last child");
        Assert.True(sibling.Y - (doomed.Y + doomed.H) >= 24,
            $"the sibling sits only {sibling.Y - (doomed.Y + doomed.H):0} px under the doomed column's last child; a disposed pad-24 column owes at least its bottom padding");
    }

    [Fact]
    public void ConciseContainersAllocateNothingInSteadyState()
    {
        using var app = TestApp.Start(new ConciseSteadyPage(), 400, 240);
        app.Frame(5);
        for (int i = 0; i < 10; i++)
        {
            app.Frame();
            Assert.Equal(0, app.Surface.LastFrame.AllocatedBytes);
        }
    }

    private static void AssertSameBytes<T>(in T actual, in T expected) where T : struct =>
        Assert.True(
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in actual, 1))
                .SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in expected, 1))),
            $"{typeof(T).Name}: the concise ctor and the equivalent initializer do not hand native the same bytes");

    private sealed class LayoutParityPage : Component
    {
        protected override void Draw(Ui ui)
        {
            using (ui.Column(new Box { Gap = 6 }))
            {
                using (ui.Row(new Box { Gap = 24 })) { ui.Button("gap canon a"); ui.Button("gap canon b"); }
                using (ui.Row(new(gap: 24))) { ui.Button("gap concise a"); ui.Button("gap concise b"); }

                ui.Button("pad base a");
                ui.Button("pad base b");
                using (ui.Column(new Box { Pad = 18 })) ui.Button("pad canon");
                ui.Button("pad base c");
                using (ui.Column(new(pad: 18))) ui.Button("pad concise");

                using (ui.Card(new Card { Pad = 16, Gap = 12, Dir = Dir.Row })) { ui.Button("card canon a"); ui.Button("card canon b"); }
                using (ui.Card(new(pad: 16, gap: 12, dir: Dir.Row))) { ui.Button("card concise a"); ui.Button("card concise b"); }
                using (ui.Card(new Card { Pad = 16, Gap = 12 })) { ui.Button("card stack a"); ui.Button("card stack b"); }

                ui.Text("size base");
                ui.Button("after size base");
                ui.Text("size canon", new TextOpts { Size = 28 });
                ui.Button("after size canon");
                ui.Text("size concise", new(size: 28));
                ui.Button("after size concise");
            }
        }
    }

    private sealed class DoomedColumnPage : Component
    {
        public bool Threw;

        protected override void Draw(Ui ui)
        {
            using (ui.Column(new Box { Gap = 6 }))
            {
                ui.Button("anchor");
                try
                {
                    using var doomed = ui.Column(new(pad: 24));
                    ui.Button("doomed");
                    throw new ApplicationException();
                }
                catch (ApplicationException)
                {
                    Threw = true;
                }
                using (ui.Row(new(gap: 12))) ui.Button("sibling");
            }
        }
    }

    private sealed class ConciseSteadyPage : Component
    {
        protected override void Draw(Ui ui)
        {
            using (ui.Column(new(pad: 8, gap: 4, height: Size.Fill)))
            {
                using (ui.Row(new(gap: 12, align: Align.Center))) { ui.Button("one"); ui.Button("two"); }
                using (ui.Card(new(pad: 16, gap: 12, dir: Dir.Column))) ui.Text("body");
                ui.Text("Live", new(size: 12, color: Color.TextSecondary));
                ui.Badge("Updating", new(Tone.Danger));
            }
        }
    }

    private sealed class ValueSegments : Component
    {
        private static readonly string[] Items = ["Light", "Dark"];
        public int Value;
        public int? Change;
        public int Changes;
        protected override void Draw(Ui ui)
        {
            Change = ui.Segmented("modes", Items, Value);
            if (Change is { } value) { Value = value; Changes++; }
        }
    }

    private sealed class GridPage : Component
    {
        private static readonly GridColumn[] Columns = [new("Name", 240, Sortable: true)];
        private static readonly string[] Items = ["one", "two"];
        public readonly GridState State = new(4);
        public TableSort SortDuringRows;
        public int CursorDuringRows;
        public uint RowId;
        protected override void Draw(Ui ui)
        {
            ui.Segmented("segments", Items, 0);
            RowId = CuiHash.Of(2, ui.MakeId("rows") ^ 0x20Bu);
            using var grid = ui.DataGrid("rows", Columns, 4, State, new GridOpts { Height = 200 });
            if (grid)
            {
                SortDuringRows = State.Sort;
                CursorDuringRows = State.Cursor;
                for (int row = State.First; row < State.Last; row++)
                {
                    ui.DataGridRow(row);
                    ui.DataGridCellText($"row {row}");
                }
            }
        }
    }
}
