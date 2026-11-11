using System.Threading.Tasks;

namespace CuiSharp.Tests;

/// <summary>
/// The developer tools CUI 1.3 added, and the pure helpers behind them. Each one draws a
/// real headless frame, so a wrapper that passes the wrong pointer, count or option field
/// fails here rather than in an app; the helpers are checked against what the widgets show.
/// </summary>
public class DeveloperToolTests
{
    private const int Width = 900;
    private const int Height = 700;

    private static byte[] Frame(CuiSurface surface, DrawFn draw)
    {
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        surface.Draw(draw, pixels);
        Assert.Empty(surface.Faults);
        return pixels;
    }

    /// <summary>A trace in the order CUI needs: by lane, then depth, then start.</summary>
    private static TraceSpan[] Trace() =>
    [
        new("request", 0.0, 0.6, 0, 0),
        new("query", 0.1, 0.2, 0, 1),
        new("encode", 0.05, 0.3, 1, 0),
    ];

    [Fact]
    public void TheAnalysisToolsDrawAndReportNothingWhenNothingWasClicked()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(Width, Height);
        var traceView = default(TraceViewState);
        var traceSelected = -1;
        var waterfallSelected = -1;
        var graphSelected = -1;
        var commitSelected = 0;
        var pipelineSelected = -1;

        var pixels = Frame(surface, ui =>
        {
            var trace = ui.TraceView("trace", [new TraceTrack("main")], Trace(), ref traceView, ref traceSelected);
            Assert.Equal(-1, trace.Hovered);

            var waterfall = ui.Waterfall("waterfall",
            [
                new NetRequest("/api", "GET", 200, "fetch", 1024, 0, [0.01f, 0.02f, 0.02f, 0.03f, 0.06f, 0.01f]),
            ], ref waterfallSelected);
            Assert.Equal(-1, waterfall.Clicked);

            var graph = ui.GraphView("graph",
                [new GraphNode("app", "binary", Tone.Accent), new GraphNode("core", "static lib", Tone.Info)],
                [new GraphEdge(0, 1)], ref graphSelected);
            Assert.Equal(-1, graph.Hovered);

            var commits = ui.CommitGraph("commits",
            [
                new Commit("aaaaaaa", "Second", "Ada", "now", "main", [-1, -1]),
                new Commit("bbbbbbb", "First", "Ada", "then", null, [0, -1]),
            ], ref commitSelected);
            Assert.Equal(-1, commits.Activated);

            // A stage's own status is ignored: it sums up the jobs it holds.
            var pipeline = ui.Pipeline("ci", ["Build", "Deploy"],
            [
                new PipelineJob("windows", 0, JobStatus.Passed, 12.5f),
                new PipelineJob("linux", 0, JobStatus.Failed, 3.5f, AllowFailure: true),
            ], ref pipelineSelected);
            Assert.Equal(-1, pipeline.Clicked);

            var problems = ui.Problems("problems",
            [
                new ProblemItem(Severity.Error, "boom", "a.c", 1, 2, "gcc", "error"),
                new ProblemItem(Severity.Hint, "meh", "a.c", 9, 4, "clang-tidy", "perf"),
                new ProblemItem(Severity.Warning, "hmm", "b.c", 3, 1, "clang", "-Wall"),
            ], new ProblemsOpts { Height = 200 });

            // Every diagnostic arrives: the panel counts the rows that pass its filters.
            Assert.Equal(3, problems.Shown);
            Assert.Equal(-1, problems.Selected);
        });

        Assert.Contains(pixels, b => b != 0);
        Assert.Equal(-1, traceSelected);
        Assert.Equal(-1, graphSelected);
        Assert.Equal(0, commitSelected);
        Assert.Equal(-1, pipelineSelected);
    }

    [Fact]
    public void AgentWidgetsRenderWithUnambiguousTaskStatusTypes()
    {
        // The plan/status widgets take CuiTaskStatus by name: the rename from TaskStatus
        // removed the collision with System.Threading.Tasks.TaskStatus, so this file uses
        // both types unqualified (the BCL one below, the CUI one in the steps) and draws a
        // real frame. EnumContractTests pins the old ambiguity itself; reverting the rename
        // would stop either file from compiling.
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(Width, Height);
        var steps = new PlanStep[]
        {
            new("Fetch sources", "3 results", CuiTaskStatus.Done),
            new("Draft the summary", null, CuiTaskStatus.Running),
            new("Check the citations", null, CuiTaskStatus.Pending),
        };

        var pixels = Frame(surface, ui =>
        {
            Assert.Equal(-1, ui.AgentPlan("plan", steps));
            ui.AgentStatus("Reading report.pdf", new AgentStatusOpts { Status = CuiTaskStatus.Running, Seconds = 3 });
            // The BCL enum still resolves unqualified beside the renamed CUI one.
            Assert.Equal(6, (int)TaskStatus.Canceled);
        });

        Assert.Contains(pixels, b => b != 0);
    }

    [Fact]
    public void TheEditorToolsKeepWhatTheCallerPassed()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(Width, Height);
        var headers = new KeyValue[]
        {
            new("Accept", "application/json", Enabled: true),
            new("Authorization", "Bearer x", Enabled: true, Secret: true),
        };
        var headerCount = headers.Length;
        var nodes = new QueryNode[]
        {
            new(0, Group: true),
            new(1, Field: 0, Op: 2, Value: "500"),
            new(1, Field: 1, Op: 0, Value: "open"),
        };
        var nodeCount = nodes.Length;
        var cron = "30 9 * * 1-5";
        var mods = Mods.Ctrl;

        var pixels = Frame(surface, ui =>
        {
            ui.KvEditor("headers", headers, ref headerCount, headers.Length);
            ui.QueryBuilder("query", QueryFields, nodes, ref nodeCount, nodes.Length);
            ui.SchedulePicker("schedule", ref cron, 128);
            ui.Keymap("keymap",
            [
                new KeymapBinding(new KeyCombo((int)Mods.Ctrl, Key.S), "Save", 0),
                new KeymapBinding(new KeyCombo((int)Mods.Ctrl, Key.S), "Save as", 0),
            ], ref mods, new KeymapOpts { Categories = ["File"] });
        });

        Assert.Contains(pixels, b => b != 0);
        // Nothing was typed, so the caller's own data comes back as it went in: the fixed
        // buffers inside these records are copied both ways, not pointed at.
        Assert.Equal(2, headerCount);
        Assert.Equal("Accept", headers[0].Key);
        Assert.Equal("application/json", headers[0].Value);
        Assert.True(headers[1].Secret);
        Assert.Equal(3, nodeCount);
        Assert.Equal("500", nodes[1].Value);
        Assert.Equal("30 9 * * 1-5", cron);
        Assert.Equal(Mods.Ctrl, mods);
    }

    private static readonly TraceTrack[] OneTrack = [new("main")];
    private static readonly NetRequest[] OneRequest =
        [new("/api", "GET", 200, "fetch", 1024, 0, [0.01f, 0.02f, 0.02f, 0.03f, 0.06f, 0.01f])];
    private static readonly GraphNode[] TwoNodes =
        [new("app", "binary", Tone.Accent), new("core", "static lib", Tone.Info)];
    private static readonly GraphEdge[] OneEdge = [new(0, 1)];
    private static readonly Commit[] TwoCommits =
    [
        new("aaaaaaa", "Second", "Ada", "now", "main", [-1, -1]),
        new("bbbbbbb", "First", "Ada", "then", null, [0, -1]),
    ];
    private static readonly string[] OneStage = ["Build"];
    private static readonly PipelineJob[] OneJob = [new("windows", 0, JobStatus.Passed, 12.5f)];
    private static readonly ProblemItem[] OneProblem =
        [new(Severity.Error, "boom", "a.c", 1, 2, "gcc", "error")];

    private static readonly QueryField[] QueryFields =
    [
        new("Status", QueryType.Number),
        new("State", QueryType.Choice, Choices: ["open", "closed"]),
    ];

    [Fact]
    public void TheReportingToolsDrawAndReportNothingWhenNothingWasClicked()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(Width, Height);
        var pattern = @"\b(\w+)@(\w+)\.com\b";
        var text = "ada@example.com and grace@navy.mil";
        var flags = RegexFlags.IgnoreCase;
        var method = HttpMethod.Get;
        var url = "https://api.example.com/frames?state=open";
        var apiSelected = -1;
        var lines = new[] { "int main(void) {", "    return 0;", "}" };
        var lineCommit = new[] { 0, 1, 1 };
        double selStart = 0, selEnd = 0;

        var pixels = Frame(surface, ui =>
        {
            var tests = ui.TestExplorer("tests",
            [
                new TestItem("Suite", 0, JobStatus.Failed, 0f),
                new TestItem("Passes", 1, JobStatus.Passed, 0.1f),
                new TestItem("Fails", 1, JobStatus.Failed, 0.2f, "expected 1, got 2"),
                new TestItem("Skipped", 1, JobStatus.Skipped, -1),
            ], new TestExplorerOpts { Height = 200 });

            // The counts are the proof the items arrived: they come from CUI walking them.
            Assert.Equal(3, tests.Total);
            Assert.Equal(1, tests.Passed);
            Assert.Equal(1, tests.Failed);
            Assert.Equal(1, tests.Skipped);

            ui.StackTrace("stack",
            [
                new StackFrame("main", "src/main.c", 3, 5, false, ["int main(void) {", "    return 0;", "}"], 1),
                new StackFrame("memcpy", null, 0, 0, true),
            ], new StackTraceOpts { Title = "Segmentation fault", Message = "SIGSEGV", Lang = "c" });

            var blame = ui.Blame("blame", lines, lineCommit,
            [
                new BlameCommit("aaaaaaa", "Ada", "Start it", 0),
                new BlameCommit("bbbbbbb", "Grace", "Finish it", 0),
            ], new BlameOpts { Height = 160, Lang = "c" });
            Assert.Equal(-1, blame.Clicked);

            var coverage = ui.Coverage("coverage",
            [
                new CoverageFile("src/main.c", 100, 90, 10, 8, 5, 5),
                new CoverageFile("src/main.h", 20, 10, 0, 0, 0, 0),
            ], new CoverageOpts { Height = 160 });
            Assert.Equal(-1, coverage.Activated);
            Assert.Equal(83.333f, coverage.Lines, 0.01f);   // 100 of 120 lines

            // The pattern and the text it searches are two buffers, not one: a wrapper that
            // rents both under the widget's id hands CUI the same bytes twice, and the tester
            // then searches the text in itself and reports no matches.
            var tested = ui.RegexTester("regex", ref pattern, 256, ref text, 512, ref flags);
            Assert.True(tested.Valid);
            Assert.Equal(1, tested.Matches);   // ada@example.com; the other address is not a .com
            ui.RequestBar("request", ref method, ref url);
            ui.ResponseStatus(200, 0.12, 4096);

            var api = ui.ApiExplorer("api",
            [
                new ApiEndpoint("GET", "/v1/frames", "List frames", "Frames", false),
                new ApiEndpoint("DELETE", "/v0/frames", "Remove", "Frames", true),
            ], ref apiSelected, new ApiExplorerOpts { Height = 160 });
            Assert.Equal(-1, api.TryIt);

            var flags2 = new FeatureFlag[]
            {
                new("new-renderer", "Batched rasterizer", [true, false], [100, 25]),
            };
            var changed = ui.FeatureFlags("flags", flags2, ["Development", "Production"]);
            Assert.Equal(-1, changed.Flag);
            // Nothing changed, so nothing was copied back: the record the caller passed is
            // untouched, and the frame allocated no arrays converting it.
            Assert.Equal(new[] { true, false }, flags2[0].On);
            Assert.Equal(new[] { 100, 25 }, flags2[0].Rollout);

            ui.Uptime("uptime",
            [
                new UptimeService("api", [1f, 0.99f, 0f, 1f]),
            ], new UptimeOpts { LastDay = new Date(2026, 11, 14) });

            ui.TimeHistogram("histogram", 0, 60, 12,
            [
                new HistogramSeries("info", [4f, 8f, 2f, 6f, 9f, 3f, 5f, 7f, 1f, 2f, 8f, 4f], Tone.Info),
            ], ref selStart, ref selEnd, new TimeHistogramOpts { Height = 120 });
            Assert.Equal(0, selStart);
        });

        Assert.Contains(pixels, b => b != 0);
        // The two rented text buffers come back with what they went in with.
        Assert.Equal(@"\b(\w+)@(\w+)\.com\b", pattern);
        Assert.Equal("https://api.example.com/frames?state=open", url);
        Assert.Equal(HttpMethod.Get, method);
        Assert.Equal(RegexFlags.IgnoreCase, flags);
    }

    [Fact]
    public void TheAnalysisPagesCostNothingPerFrame()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var surface = CuiSurface.Create(Width, Height);
        var traceView = default(TraceViewState);
        var traceSelected = -1;
        var waterfallSelected = -1;
        var graphSelected = -1;
        var commitSelected = 0;
        var pipelineSelected = -1;
        var spans = Trace();

        // Everything the other tools need, held across frames: a fresh collection expression
        // per frame would be the caller's allocation, and this test is about the frame.
        var headers = new KeyValue[] { new("Accept", "*/*", Enabled: true) };
        var headerCount = headers.Length;
        var nodes = new QueryNode[] { new(0, Group: true), new(1, Field: 0, Op: 2, Value: "500") };
        var nodeCount = nodes.Length;
        var cron = "30 9 * * 1-5";
        var mods = Mods.Ctrl;
        var bindings = new KeymapBinding[] { new(new KeyCombo((int)Mods.Ctrl, Key.S), "Save", 0) };
        // Categories is an array: a collection expression here would be one allocation per frame.
        var keymapOpts = new KeymapOpts { Categories = ["File"] };
        var testItems = new TestItem[] { new("Suite", 0, JobStatus.Passed, 0.1f) };
        var stackFrames = new StackFrame[] { new("main", "a.c", 3, 5, false) };
        var blameLines = new[] { "int main(void) {", "}" };
        var blameCommitsFor = new[] { 0, 0 };
        var blameCommits = new BlameCommit[] { new("aaaaaaa", "Ada", "Start it", 0) };
        var coverageFiles = new CoverageFile[] { new("a.c", 10, 9, 2, 2, 1, 1) };
        var pattern = @"(\w+)";
        var regexText = "abc def";
        var regexFlags = RegexFlags.None;
        var method = HttpMethod.Get;
        var url = "https://example.com?a=1";
        var apiSelected = -1;
        var endpoints = new ApiEndpoint[] { new("GET", "/v1", "List", "Tag", false) };
        var twoEnvs = new[] { "Dev", "Prod" };
        var flags = new FeatureFlag[] { new("flag", "A flag", [true, false], [100, 0]) };
        var uptimeServices = new UptimeService[] { new("api", [1f, 0.5f, 0f]) };
        var histogram = new HistogramSeries[] { new("info", [1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 1f, 2f, 3f], Tone.Info) };
        double selStart = 0, selEnd = 0;

        DrawFn draw = ui =>
        {
            ui.TraceView("trace", OneTrack, spans, ref traceView, ref traceSelected);
            ui.Waterfall("waterfall", OneRequest, ref waterfallSelected);
            ui.GraphView("graph", TwoNodes, OneEdge, ref graphSelected);
            ui.CommitGraph("commits", TwoCommits, ref commitSelected);
            ui.Pipeline("ci", OneStage, OneJob, ref pipelineSelected);
            ui.Problems("problems", OneProblem);

            // The editing and reporting tools, drawn with the same state every frame.
            ui.KvEditor("headers", headers, ref headerCount, headers.Length);
            ui.QueryBuilder("query", QueryFields, nodes, ref nodeCount, nodes.Length);
            ui.SchedulePicker("schedule", ref cron, 128);
            ui.Keymap("keymap", bindings, ref mods, keymapOpts);
            ui.TestExplorer("tests", testItems, new TestExplorerOpts { Height = 180 });
            ui.StackTrace("stack", stackFrames, new StackTraceOpts { Title = "Boom", Message = "Why" });
            ui.Blame("blame", blameLines, blameCommitsFor, blameCommits, new BlameOpts { Height = 140 });
            ui.Coverage("coverage", coverageFiles, new CoverageOpts { Height = 140 });
            ui.RegexTester("regex", ref pattern, 128, ref regexText, 256, ref regexFlags);
            ui.RequestBar("request", ref method, ref url);
            ui.ApiExplorer("api", endpoints, ref apiSelected, new ApiExplorerOpts { Height = 160 });
            ui.FeatureFlags("flags", flags, twoEnvs);
            ui.Uptime("uptime", uptimeServices, new UptimeOpts { LastDay = new Date(2026, 11, 14) });
            ui.TimeHistogram("histogram", 0, 60, 12, histogram, ref selStart, ref selEnd,
                new TimeHistogramOpts { Height = 120 });
        };

        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        for (var i = 0; i < 5; i++) surface.Draw(draw, pixels);

        // Batches with a median: one batch that straddles a one-off says nothing about
        // the frame, a real per-frame allocation moves every batch.
        var perFrame = new long[5];
        for (var batch = 0; batch < perFrame.Length; batch++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10; i++) surface.Draw(draw, pixels);
            perFrame[batch] = (GC.GetAllocatedBytesForCurrentThread() - before) / 10;
        }

        Assert.True(perFrame.Median() == 0,
            $"a steady frame allocated {perFrame.Median()} B (recorder on: {WidgetRecorder.Enabled})");
    }

    [Fact]
    public void RegexSearchReportsGroupsAndWhyAPatternFailed()
    {
        Assert.True(CuiRegex.TrySearch(@"(\w+)@(\w+)\.com", "ada@example.com", out var match, out var error));
        Assert.Null(error);
        Assert.Equal(2, match.Groups.Length - 1);
        Assert.Equal("ada", CuiRegex.Slice("ada@example.com", match with { Start = match.Groups[1].Start, End = match.Groups[1].End }));
        Assert.Equal("example", CuiRegex.Slice("ada@example.com", match with { Start = match.Groups[2].Start, End = match.Groups[2].End }));

        // A named group carries its name; a positional one does not.
        Assert.True(CuiRegex.TrySearch(@"(?<user>\w+)@", "ada@example.com", out var named, out _));
        Assert.Equal("user", named.Groups[1].Name);
        Assert.Null(match.Groups[1].Name);

        // Case sensitivity is the flags, not the pattern.
        Assert.False(CuiRegex.TrySearch("ADA", "ada@example.com", out _, out var none));
        Assert.Null(none);
        Assert.True(CuiRegex.TrySearch("ADA", "ada@example.com", out _, out _, flags: RegexFlags.IgnoreCase));

        // An invalid pattern is a failure with a reason, not silence.
        Assert.False(CuiRegex.TrySearch("a(", "ada", out _, out var bad));
        Assert.False(string.IsNullOrEmpty(bad));
    }

    [Fact]
    public void AHelperCalledWithoutAFrameGivesItsBytesBack()
    {
        // Nothing resets the arena outside a frame, so a helper documented as frameless must
        // return it: a page that searches on a click would otherwise grow it on every click.
        var fields = new QueryField[] { new("Status", QueryType.Number) };
        var nodes = new QueryNode[] { new(0, Group: true), new(1, Field: 0, Op: 2, Value: "500") };
        var spans = new TraceSpan[] { new("outer", 1, 4, 0, 0, 0), new("inner", 1.5, 2, 0, 1, 0) };
        var flames = new FlameSpan[] { new("main", 0.5, 1, 0), new("draw", 0.5, 2, 1) };

        // A directory small enough to list a hundred times.
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cui-frameless-arena");
        Directory.CreateDirectory(directory);
        File.WriteAllText(System.IO.Path.Combine(directory, "a.txt"), "a");

        Utf8.BeginFrame();
        for (var i = 0; i < 100; i++)
        {
            Assert.True(CuiRegex.TrySearch(@"(\w+)@", "ada@example.com", out var match, out _));
            Assert.Equal("ada", CuiRegex.Slice("ada@example.com", match.Groups[1]));
            CuiQuery.Text(fields, nodes);
            CuiQuery.Match(fields, nodes, ["503"]);
            CuiFiles.SortTraceSpans(spans);
            CuiFiles.SortFlameSpans(flames);
            CuiHttp.ParseQuery("https://example.com?a=1");
            Assert.Single(CuiFiles.ListDirectory(directory));
        }

        Assert.Equal(0, Utf8.FrameBytes);
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void AGroupSliceIsTheTextTheGroupCoveredInBytes()
    {
        // The engine reports byte offsets; an accented letter is two bytes and the sign three,
        // so a group after them cannot be found by counting characters.
        const string text = "café €5 and more";
        Assert.True(CuiRegex.TrySearch(@"(\d+)", text, out var match, out var error));
        Assert.Null(error);
        Assert.Equal("5", CuiRegex.Slice(text, match));

        // Every group of a match reads back without the caller rebuilding a match to hold it.
        Assert.True(CuiRegex.TrySearch(@"(\w+)@(\w+)\.com", "ada@example.com", out var mail, out _));
        Assert.Equal("ada", CuiRegex.Slice("ada@example.com", mail.Groups[1]));
        Assert.Equal("example", CuiRegex.Slice("ada@example.com", mail.Groups[2]));
        Assert.Equal("ada@example.com", CuiRegex.Slice("ada@example.com", mail));

        // A group that took no part in the match has no text rather than a wrong range.
        Assert.Equal("", CuiRegex.Slice(text, new RegexGroup(-1, -1, null)));
    }

    [Fact]
    public void CronNextFindsTheNextWeekdayRun()
    {
        // A Saturday noon: "09:30 on weekdays" first runs on the Monday after it.
        var saturday = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(DayOfWeek.Saturday, saturday.DayOfWeek);

        Assert.True(CuiCron.TryNext("30 9 * * 1-5", saturday, out var next, utcOffsetMinutes: 0));
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 9, 30, 0, TimeSpan.Zero), next);

        // Every fifteen minutes: the next quarter hour, on the same day.
        Assert.True(CuiCron.TryNext("*/15 * * * *", saturday, out var quarter, utcOffsetMinutes: 0));
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 12, 15, 0, TimeSpan.Zero), quarter);

        // The offset decides which wall clock the expression is read in: 12:00 UTC is already
        // 14:00 at UTC+2, so the next noon is the following day.
        Assert.True(CuiCron.TryNext("0 12 * * *", saturday, out var noon, utcOffsetMinutes: 120));
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero), noon);

        Assert.False(CuiCron.TryNext("not a schedule", saturday, out _));
    }

    [Fact]
    public void UrlHelpersDecodeAndReencodeQueryParameters()
    {
        var pairs = CuiHttp.ParseQuery("https://api.example.com/frames?state=open&q=two%20words#top");
        Assert.Equal(2, pairs.Length);
        Assert.Equal("state", pairs[0].Key);
        Assert.Equal("open", pairs[0].Value);
        Assert.Equal("q", pairs[1].Key);
        Assert.Equal("two words", pairs[1].Value);

        // Encoding replaces the query, keeps the path and the fragment, and leaves out a
        // pair the table has switched off.
        var url = CuiHttp.SetQuery("https://api.example.com/frames?a=1#top",
        [
            new KeyValue("q", "two words", Enabled: true),
            new KeyValue("off", "1", Enabled: false),
        ]);
        Assert.Equal("https://api.example.com/frames?q=two%20words#top", url);

        Assert.Equal("Not Found", CuiHttp.Reason(404));
        Assert.Equal("", CuiHttp.Reason(999));
        Assert.Equal("PATCH", CuiHttp.MethodName(HttpMethod.Patch));
    }

    [Fact]
    public void AQueryReadsAsASentenceAndFiltersRecords()
    {
        var fields = new QueryField[]
        {
            new("Status", QueryType.Number),
            new("State", QueryType.Choice, Choices: ["open", "closed"]),
        };
        var nodes = new QueryNode[]
        {
            new(0, Group: true),
            new(1, Field: 0, Op: 2, Value: "500"),
            new(1, Field: 1, Op: 0, Value: "open"),
        };

        string sentence = CuiQuery.Text(fields, nodes);
        Assert.Contains("Status", sentence, StringComparison.Ordinal);
        Assert.Contains("500", sentence, StringComparison.Ordinal);
        Assert.Contains("State", sentence, StringComparison.Ordinal);

        // A record matches when its values satisfy every rule of an "all" group.
        Assert.True(CuiQuery.Match(fields, nodes, ["503", "open"]));
        Assert.False(CuiQuery.Match(fields, nodes, ["200", "open"]));
        Assert.False(CuiQuery.Match(fields, nodes, ["503", "closed"]));

        Assert.Equal("contains", CuiQuery.OperatorName(QueryType.Text, 2));
        Assert.Equal(6, CuiQuery.OperatorCount(QueryType.Number));
        Assert.Equal(2, CuiQuery.OperatorCount(QueryType.Choice));
        Assert.Equal(2, CuiQuery.OperatorCount(QueryType.Bool));
    }

    [Fact]
    public void TraceSpansSortIntoTheOrderTheViewerDraws()
    {
        var spans = new TraceSpan[]
        {
            new("late", 5, 6, 1, 0, 0),
            new("nested", 1.5, 1.8, 0, 1, 0),
            new("outer", 1, 4, 0, 0, 0),
            new("deeper", 1.2, 1.3, 0, 1, 0),
        };

        CuiFiles.SortTraceSpans(spans);

        Assert.Equal(["outer", "deeper", "nested", "late"], spans.Select(s => s.Name));
        Assert.Equal(1.2, spans[1].Start);
    }
}
