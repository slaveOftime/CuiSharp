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
