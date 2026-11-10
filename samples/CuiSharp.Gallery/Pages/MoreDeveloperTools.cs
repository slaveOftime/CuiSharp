// Page 13 of 13: the rest of a developer app: test results, a stack trace, blame,
// coverage, a regex tester, an HTTP client, an API reference, feature flags, uptime and
// an event histogram.
// Part of the CuiSharp.Gallery sample: MoreDeveloperTools
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    private string _regexPattern = @"\b(\w+)@(\w+)\.com\b";
    private string _regexText = "Write to ada@example.com or grace@navy.mil, not to root@localhost.";
    private RegexFlags _regexFlags = RegexFlags.IgnoreCase;
    private HttpMethod _httpMethod = HttpMethod.Get;
    private string _url = "https://api.example.com/v1/frames?state=open&limit=20";
    private int _apiSelected = -1;
    private string _regexSummary = "";
    private string? _regexShownFor;
    private bool _regexValid;
    private KeyValue[]? _query;
    private (int Status, double Seconds, double Bytes)? _response;
    // The live state: FeatureFlags edits these records in place, so the page passes the field.
    private readonly FeatureFlag[] _flags =
    [
        new("new-renderer", "Draw with the batched software rasterizer", [true, true, false], [100, 25, 0]),
        new("frame-budget", "Fail CI when a frame regresses", [true, true, true], [100, 100, 100]),
        new("headless-protocol", "Answer dotnet cui over the pipe", [true, false, false], [100, 10, 0]),
    ];

    private static readonly string[] Environments = ["Development", "Staging", "Production"];

    // One series per log level, built once: a fresh array per frame is the page's own
    // allocation, and the selection has to outlive the frame or a drag is thrown away.
    private static readonly HistogramSeries[] HistogramSeriesData = BuildHistogram();
    private double _histogramStart;
    private double _histogramEnd;

    private static readonly TestItem[] Tests =
    [
        new("CuiSharp.Tests", 0, JobStatus.Failed, 0.42f),
        new("FrameBudget", 1, JobStatus.Passed, 0.08f),
        new("AGalleryPageDrawsInZeroBytes", 2, JobStatus.Passed, 0.01f),
        new("AnIdleFrameSleeps", 2, JobStatus.Passed, 0.02f),
        new("Bindings", 1, JobStatus.Failed, 0.31f),
        new("EveryStructMatchesC", 2, JobStatus.Passed, 0.04f),
        new("EveryOptionMapsEveryField", 2, JobStatus.Failed, 0.02f, "Expected 24, got 8 for MaxPoints"),
        new("Hosting", 1, JobStatus.Skipped, -1),
    ];

    private static readonly string[] SourceLines =
    [
        "        var __opts = o.ToNative();",
        "        var __result = Native.Graph(ui.Handle, ...);",
        "        WidgetRecorder.Note(ui.Handle, \"Graph\", id);",
        "        return GraphResult.FromNative(__result);",
    ];

    private static readonly StackFrame[] Frames =
    [
        new("Layout.Arrange", "src/layout.c", 412, 9, false, SourceLines, SourceLines.Length, 409),
        new("Draw.Paint", "src/draw.c", 288, 5, false),
        new("memcpy", null, 0, 0, true),
        new("memcpy", null, 0, 0, true),
        new("memcpy", null, 0, 0, true),
        new("Platform.Run", "src/platform/cui_app_win32.c", 731, 13, true),
    ];

    private static readonly string[] SourceText =
    [
        "#include \"cui.h\"",
        "",
        "void cui_layout(cui_ctx *ui) {",
        "    for (int i = 0; i < ui->count; i++) {",
        "        cui_node *n = &ui->nodes[i];   /* the null deref */",
        "        n->w = n->child->w;",
        "    }",
        "}",
    ];

    private static readonly int[] SourceCommit = [0, 0, 1, 1, 2, 2, 2, 2];

    private static readonly BlameCommit[] BlameCommits =
    [
        new("b46ba48", "Ada", "Ship the pieces an app writes for itself", 0),
        new("d9ec1de", "Grace", "Publish the changelog section", 0),
        new("7e3290c", "Linus", "Make the skill folder self-contained", 0),
    ];

    private static readonly CoverageFile[] CoverageFiles =
    [
        new("src/CuiSharp/Ui/Ui.cs", 320, 298, 44, 31, 62, 58),
        new("src/CuiSharp/Interop/Utf8.cs", 180, 176, 12, 12, 24, 24),
        new("src/CuiSharp/Hosting/Components.cs", 240, 120, 30, 6, 40, 14),
        new("src/CuiSharp/Semantics/WidgetRecorder.cs", 150, 141, 22, 18, 18, 17),
    ];

    private static readonly string[] RequestStages = ["Build", "Test", "Package"];

    private static readonly ApiParam[] EndpointParams =
    [
        new("state", "query", "string", false, "open, closed or merged"),
        new("limit", "query", "integer", false, "How many to return, at most 100"),
        new("id", "path", "integer", true, "The frame to fetch"),
    ];

    private static readonly ApiEndpoint[] Endpoints =
    [
        new("GET", "/v1/frames", "List frames, newest first", "Frames", false, EndpointParams[..2], 2),
        new("GET", "/v1/frames/{id}", "One frame with its spans", "Frames", false, EndpointParams, 3),
        new("POST", "/v1/frames", "Record a frame", "Frames", false),
        new("DELETE", "/v0/frames/{id}", "Remove a frame", "Frames", true),
        new("GET", "/v1/orgs", "List organisations", "Orgs", false),
    ];

    private static readonly UptimeService[] Services =
    [
        new("api", Days(0.999f, 1.0f, 0.998f, 0.0f, 1.0f, 0.941f, 1.0f, 1.0f, 1.0f, 0.0f, 1.0f, 1.0f), 12),
        new("web", Days(1.0f, 1.0f, 1.0f, 1.0f, 0.999f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f), 12),
        new("worker", Days(0.98f, 0.99f, 1.0f, 1.0f, 1.0f, 0.87f, 0.95f, 1.0f, 1.0f, 1.0f, 1.0f, 0.0f), 12),
    ];

    private void PageMoreDeveloperTools(Ui ui)
    {
        Section(ui, "Test explorer", "Suites fold; hover a row to run it; filter by status or name.");
        using (ui.Card(new Card()))
        {
            var result = ui.TestExplorer("tests", Tests, new TestExplorerOpts { Height = 240 });
            if (result.Run >= 0) _log.WriteLine($"$ run {Tests[result.Run].Name}");
            if (result.RunAll) _log.WriteLine("$ run all");
        }

        Section(ui, "Stack trace", "Library frames fold; a frame with source opens; copy the trace.");
        using (ui.Card(new Card()))
        {
            ui.StackTrace("trace", Frames, new StackTraceOpts
            {
                Title = "NullReferenceException",
                Message = "Cannot read properties of undefined (reading 'w')",
                Lang = "c",
            });
        }

        Section(ui, "Blame", "Hover the gutter to mark a commit's lines; click it to open the commit.");
        using (ui.Card(new Card()))
        {
            var result = ui.Blame("blame", SourceText, SourceCommit, BlameCommits, new BlameOpts { Height = 220, Lang = "c" });
            if (result.Clicked >= 0) _log.WriteLine($"$ git show {BlameCommits[result.Clicked].Hash}");
        }

        Section(ui, "Coverage", "Folders sum up their files; click a column to sort.");
        using (ui.Card(new Card()))
        {
            var result = ui.Coverage("coverage", CoverageFiles, new CoverageOpts { Height = 200 });
            if (result.Activated >= 0) _log.WriteLine($"$ open {CoverageFiles[result.Activated].Path}");
            ui.Secondary($"{result.Lines:0.0}% of all lines are covered.");
        }

        Section(ui, "Regex tester", "Matches and groups are marked as you type.");
        using (ui.Card(new Card()))
        {
            var result = ui.RegexTester("regex", ref _regexPattern, 256, ref _regexText, 1024, ref _regexFlags);
            if (result.Valid != _regexValid || !string.Equals(_regexShownFor, _regexPattern, StringComparison.Ordinal))
            {
                // Reading a match copies strings, so it happens when the pattern changes.
                _regexValid = result.Valid;
                _regexShownFor = _regexPattern;
                _regexSummary = !result.Valid
                    ? result.Error ?? "invalid pattern"
                    : CuiRegex.TrySearch(_regexPattern, _regexText, out var match, out _, flags: _regexFlags)
                        ? $"{match.Groups.Length - 1} groups, first match \"{CuiRegex.Slice(_regexText, match)}\""
                        : "no match";
            }

            ui.Secondary(_regexSummary);
        }

        Section(ui, "HTTP request bar", "The parameters table and the URL stay in step.");
        using (ui.Card(new Card()))
        {
            var result = ui.RequestBar("request", ref _httpMethod, ref _url, o: new RequestBarOpts { Placeholder = "https://" });
            if (result.Send)
            {
                _log.WriteLine($"$ {CuiHttp.MethodName(_httpMethod)} {_url}");
                _response = (200, 0.184, 4912);
            }

            if (result.Send || result.Changed || _query is null)
            {
                // Parsing the query allocates the pairs, so it happens when the URL changed.
                _query = CuiHttp.ParseQuery(_url);
            }

            if (_response is { } response) ui.ResponseStatus(response.Status, response.Seconds, response.Bytes);
            foreach (var pair in _query)
            {
                ui.Secondary($"{pair.Key} = {pair.Value}");
            }
        }

        Section(ui, "API explorer", "Endpoints grouped by tag; a click opens its parameters and Try it.");
        using (ui.Card(new Card()))
        {
            var result = ui.ApiExplorer("api", Endpoints, ref _apiSelected, new ApiExplorerOpts { Height = 240 });
            if (result.TryIt >= 0) _log.WriteLine($"$ try {Endpoints[result.TryIt].Method} {Endpoints[result.TryIt].Path}");
        }

        Section(ui, "Feature flags", "A switch per environment, with the share of users a flag reaches there.");
        using (ui.Card(new Card()))
        {
            var result = ui.FeatureFlags("flags", _flags, Environments);
            if (result.Flag >= 0)
            {
                // The widget edited the record it was handed: the field is already the new state.
                bool on = _flags[result.Flag].On![result.Env];
                _log.WriteLine($"$ {_flags[result.Flag].Key} in {Environments[result.Env]}: {(on ? "on" : "off")}");
            }
        }

        Section(ui, "Uptime", "One bar per day, coloured by availability; grey means no data.");
        using (ui.Card(new Card()))
        {
            var result = ui.Uptime("uptime", Services, new UptimeOpts { LastDay = new Date(2026, 11, 14) });
            if (result.Service >= 0) ui.Secondary($"{Services[result.Service].Name}, day {result.Day}");
        }

        Section(ui, "Time histogram", "Drag across it to select a time range, click to clear.");
        using (ui.Card(new Card()))
        {
            var result = ui.TimeHistogram("histogram", 0, 60, 24, HistogramSeriesData, ref _histogramStart, ref _histogramEnd,
                new TimeHistogramOpts { Height = 140 });
            if (result.SelectionChanged) ui.Secondary($"selected {_histogramStart:0}m to {_histogramEnd:0}m");
        }
    }

    private static HistogramSeries[] BuildHistogram()
    {
        // One bar per minute of the last 24, split by log level.
        var info = new float[24];
        var warn = new float[24];
        var error = new float[24];
        for (int i = 0; i < 24; i++)
        {
            info[i] = 40 + ((i * 37) % 90);
            warn[i] = (i % 5) * 4;
            error[i] = i is 14 or 15 ? 12 : i % 7;
        }

        return
        [
            new("info", info, Tone.Info),
            new("warn", warn, Tone.Warning),
            new("error", error, Tone.Danger),
        ];
    }

    private static float[] Days(params float[] values) => values;
}
