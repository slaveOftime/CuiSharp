// Page 12 of 28: the observability tools a developer app puts on screen: a trace, a
// network waterfall, a dependency graph, the commit history, a CI run, compiler
// diagnostics, editable key = value pairs, a filter builder, a cron schedule and a
// keyboard map.
// Part of the CuiSharp.Gallery sample: DeveloperTools
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    private TraceViewState _traceView;
    private int _traceSelected = -1;
    private int _waterfallSelected = -1;
    private int _graphSelected = -1;
    private int _commitSelected = 0;
    private int _pipelineSelected = -1;
    private Mods _keymapMods = Mods.Ctrl;

    private readonly KeyValue[] _headers =
    [
        new("Accept", "application/json", Enabled: true),
        new("Authorization", "Bearer …", Enabled: true, Secret: true),
        new("X-Trace", "4f1c", Enabled: false),
    ];
    private int _headerCount = 3;

    // The builder edits these in place: node 0 is the outer group, the rest sit under it.
    private readonly QueryNode[] _queryNodes =
    [
        new(0, Group: true, Any: false),
        new(1, Field: 0, Op: 2, Value: "500"),
        new(1, Field: 1, Op: 0, Value: "open"),
        new(2, Group: true, Any: true),
    ];
    private int _queryCount = 4;
    private string _queryText = "";

    private string _cron = "30 9 * * 1-5";

    private static readonly QueryField[] QueryFields =
    [
        new("Status code", QueryType.Number),
        new("State", QueryType.Choice, Choices: ["open", "closed", "merged"]),
        new("Service", QueryType.Choice, Choices: ["api", "web", "worker"]),
    ];

    private static readonly TraceTrack[] Tracks =
    [
        new("main"), new("worker-1"), new("gc"),
    ];

    private static readonly TraceSpan[] Spans =
    [
        new("request", 0.00, 0.62, 0, 0),
        new("router", 0.02, 0.10, 0, 1),
        new("handler", 0.10, 0.58, 0, 1),
        new("query", 0.14, 0.31, 0, 2),
        new("render", 0.34, 0.58, 0, 2),
        new("encode", 0.06, 0.30, 1, 0),
        new("flush", 0.31, 0.42, 1, 0),
        new("pause", 0.22, 0.24, 2, 0),
    ];

    private static readonly TraceMarker[] Markers =
    [
        new("deploy", 0.5, Tone.Info),
    ];

    private static readonly NetRequest[] Requests =
    [
        new("/", "GET", 200, "document", 18_400, 0.00, Timing(0.004f, 0.012f, 0.020f, 0.041f, 0.086f, 0.020f)),
        new("/app.css", "GET", 200, "css", 9_120, 0.01, Timing(0.002f, 0.011f, 0.018f, 0.030f, 0.018f, 0.004f)),
        new("/app.js", "GET", 200, "script", 84_500, 0.02, Timing(0.003f, 0.014f, 0.022f, 0.038f, 0.061f, 0.014f)),
        new("/api/session", "GET", 200, "fetch", 1_240, 0.09, Timing(0.001f, 0.008f, 0.014f, 0.022f, 0.041f, 0.006f)),
        new("/api/frames", "GET", 500, "fetch", 512, 0.14, Timing(0.001f, 0.009f, 0.016f, 0.024f, 0.088f, 0.002f)),
    ];

    private static readonly GraphNode[] GraphNodes =
    [
        new("app", "binary", Tone.Accent),
        new("ui", "static lib", Tone.Info),
        new("render", "static lib", Tone.Info),
        new("core", "static lib", Tone.Success),
        new("platform", "static lib", Tone.Neutral),
    ];

    private static readonly GraphEdge[] GraphEdges =
    [
        new(0, 1), new(0, 2), new(1, 3), new(2, 3), new(3, 4),
    ];

    private static readonly Commit[] Commits =
    [
        new("b46ba48", "Ship the pieces an app writes for itself", "Ada", "2 hours ago", "main, HEAD", [-1, -1]),
        new("d9ec1de", "Publish the changelog section as the release page", "Ada", "yesterday", null, [0, -1]),
        new("7e3290c", "Make the skill folder self-contained", "Grace", "2 days ago", "origin/main", [1, -1]),
        new("289893e", "Keep one copy of every page", "Grace", "3 days ago", "tag: v1.1.0", [2, 3]),
        new("96da9e2", "Gather everything a consumer reads", "Linus", "4 days ago", null, [4, -1]),
    ];

    private static readonly string[] Stages = ["Build", "Test", "Package", "Deploy"];

    private static readonly PipelineJob[] Jobs =
    [
        new("windows-x64", 0, JobStatus.Passed, 184.2f),
        new("linux-x64", 0, JobStatus.Passed, 96.4f),
        new("unit", 1, JobStatus.Passed, 42.0f),
        new("frame budget", 1, JobStatus.Failed, 12.8f),
        new("aot publish", 2, JobStatus.Running, 61.3f),
        new("nuget push", 3, JobStatus.Pending, -1),
    ];

    private static readonly ProblemItem[] Problems =
    [
        new(Severity.Error, "use of undeclared identifier 'cui_size'", "src/layout.c", 41, 9, "clang", "error"),
        new(Severity.Warning, "unused variable 'pad'", "src/layout.c", 88, 12, "clang", "-Wunused"),
        new(Severity.Hint, "consider inlining", "src/layout.c", 120, 5, "clang-tidy", "perf"),
        new(Severity.Warning, "comparison of integers of different signs", "src/draw.c", 17, 24, "gcc", "-Wsign-compare"),
        new(Severity.Error, "no member named 'radius'", "src/draw.c", 63, 7, "gcc", "error"),
    ];

    private static readonly string[] KeymapCategories = ["File", "Edit", "View"];

    private static readonly KeymapBinding[] Bindings =
    [
        new(new KeyCombo((int)Mods.Ctrl, Key.N), "New file", 0),
        new(new KeyCombo((int)Mods.Ctrl, Key.S), "Save", 0),
        new(new KeyCombo((int)Mods.Ctrl, Key.Z), "Undo", 1),
        new(new KeyCombo((int)Mods.Ctrl, Key.Y), "Redo", 1),
        new(new KeyCombo((int)Mods.Ctrl, Key.F), "Find", 1),
        new(new KeyCombo((int)Mods.Ctrl, Key.B), "Toggle sidebar", 2),
        new(new KeyCombo((int)Mods.Ctrl, Key.J), "Toggle theme", 2),
        new(new KeyCombo((int)Mods.Ctrl, Key.S), "Save as", 0),
    ];

    private void PageDeveloperTools(Ui ui)
    {
        if (_queryText.Length == 0) _queryText = CuiQuery.Text(QueryFields, _queryNodes);

        Section(ui, "Trace viewer", "Wheel zooms, drag pans, Shift+drag measures, a lane's name folds it.");
        using (ui.Card(new Card()))
        {
            ui.TraceView("trace", Tracks, Spans, ref _traceView, ref _traceSelected, new TraceOpts { Height = 220, Markers = Markers });
        }

        Section(ui, "Network waterfall", "Hover a bar for its timing; Up and Down move the selection.");
        using (ui.Card(new Card()))
        {
            var result = ui.Waterfall("waterfall", Requests, ref _waterfallSelected,
                new WaterfallOpts { Height = 200, DomLoaded = 0.12, Loaded = 0.24 });
            if (result.Clicked >= 0) _log.WriteLine($"$ open {Requests[result.Clicked].Name}");
        }

        Section(ui, "Dependency graph", "Hover a node to light up its neighbours; wheel zooms, F fits.");
        using (ui.Card(new Card()))
        {
            ui.GraphView("deps", GraphNodes, GraphEdges, ref _graphSelected, new GraphOpts { Height = 260 });
        }

        Section(ui, "Commit graph", "Branch lanes, merges and refs; only the rows in view are built.");
        using (ui.Card(new Card()))
        {
            ui.CommitGraph("commits", Commits, ref _commitSelected, new CommitGraphOpts { Height = 220 });
        }

        Section(ui, "Pipeline", "Stages of jobs; a stage sums up its jobs.");
        using (ui.Card(new Card()))
        {
            var result = ui.Pipeline("ci", Stages, Jobs, ref _pipelineSelected);
            if (result.Clicked >= 0) _log.WriteLine($"$ show log: {Jobs[result.Clicked].Name}");
        }

        Section(ui, "Problems", "Grouped by file; filter by severity or text, Enter opens.");
        using (ui.Card(new Card()))
        {
            var result = ui.Problems("problems", Problems, new ProblemsOpts { Height = 220 });
            if (result.Activated >= 0) _log.WriteLine($"open {Problems[result.Activated].File}");
        }

        Section(ui, "Key-value editor", "The checkbox turns a pair on or off, typing in the empty row adds one, the x removes one.");
        using (ui.Card(new Card()))
        {
            ui.KvEditor("headers", _headers, ref _headerCount, _headers.Length,
                new KvEditorOpts { KeyPlaceholder = "Header", ValuePlaceholder = "Value" });
        }

        Section(ui, "Query builder", "Conditions joined by all or any, in groups you can nest.");
        using (ui.Card(new Card()))
        {
            if (ui.QueryBuilder("query", QueryFields, _queryNodes, ref _queryCount, _queryNodes.Length))
            {
                // Rendering the sentence builds a string, so it happens on a change and not every frame.
                _queryText = CuiQuery.Text(QueryFields, _queryNodes.AsSpan(0, _queryCount));
            }

            ui.Secondary(_queryText);
        }

        Section(ui, "Schedule picker", "Every N minutes, hourly, daily, weekly, monthly, or a custom expression.");
        using (ui.Card(new Card()))
        {
            var result = ui.SchedulePicker("schedule", ref _cron, o: new ScheduleOpts { NextCount = 3 });
            if (CuiCron.TryNext(_cron, DateTimeOffset.UtcNow, out var next))
            {
                ui.Secondary($"Next run: {next:yyyy-MM-dd HH:mm} ({(result.Valid ? "valid" : "invalid")})");
            }
        }

        Section(ui, "Keymap", "Each bound key shows its action and the colour of its category; keys bound twice are marked.");
        using (ui.Card(new Card()))
        {
            var result = ui.Keymap("keymap", Bindings, ref _keymapMods,
                new KeymapOpts { Categories = KeymapCategories });
            if (result.Conflicts > 0) ui.Secondary($"{result.Conflicts} keys are bound twice.");
        }
    }

    /// <summary>The six phases of a request, in the order CUI draws them.</summary>
    private static float[] Timing(float queued, float dns, float connect, float tls, float waiting, float download) =>
        [queued, dns, connect, tls, waiting, download];
}
