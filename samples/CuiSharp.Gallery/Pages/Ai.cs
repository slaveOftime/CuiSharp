// Page 26 of 28: a conversation with reasoning, tool calls and sources, the
// composer, an agent plan, a voice orb and prompt suggestions.
// Part of the CuiSharp.Gallery sample: AI
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    private static readonly Model[] AiModels =
    [
        new("Atlas", "Longer answers, reads files and code", "New", Icons.Layers),
        new("Atlas Fast", "Short answers in about a second", Icon: Icons.Zap),
        new("Atlas Mini", "Runs on your own machine", Icon: Icons.Box),
    ];

    private static readonly Source[] AiSources =
    [
        new("Immediate mode GUIs and why they keep coming back", "https://fieldnotes.dev/immediate-mode",
            "A look at retained and immediate mode toolkits and the trade offs between them."),
        new("Notes on subpixel text positioning", "https://glyphlab.io/notes/subpixel",
            "Shaping, hinting and subpixel positioning explained with examples."),
        new("Spring physics for interface motion", "https://motionbook.dev/springs",
            "Why spring physics feel more natural than cubic beziers for interface motion."),
    ];

    private static readonly VizItem[] ContextParts =
    [
        new("System", 3200), new("Files", 41800), new("Messages", 68400),
    ];

    private static readonly PromptSuggestion[] AiSuggestions =
    [
        new(Icons.Code, "Explain this code", "Walk through what it does"),
        new(Icons.ChartBar, "Analyze a dataset", "Find trends and outliers"),
        new(Icons.Pencil, "Draft an email", "Clear and friendly"),
        new(Icons.Bug, "Debug an error", "Paste the stack trace"),
    ];

    private static readonly string[] VoiceStates = ["Idle", "Listening", "Thinking", "Speaking"];

    private string _aiDraft = "";
    private int _aiModel, _aiVote, _orbState = 1, _aiRemoved = -1;
    private bool _aiBusy;
    private double _aiStarted = -1;
    private string[] _aiAttachments = ["quarterly-report.pdf", "metrics.csv"];

    // The plan is written into one retained array each frame: only two entries
    // depend on the busy flag, and a fresh array per frame is allocation.
    private readonly PlanStep[] _aiSteps = new PlanStep[5];

    private void PageAi(Ui ui)
    {
        double now = ui.Time;
        if (_aiBusy && _aiStarted < 0) _aiStarted = now;
        if (!_aiBusy) _aiStarted = -1;
        float elapsed = _aiBusy ? (float)(now - _aiStarted) : 0;
        if (_aiBusy && elapsed > 9) _aiBusy = false;

        using (ui.Row(new Box { Gap = 28, Align = Align.Start, Width = Size.Fill }))
        {
            using (ui.Column(new Box { Width = Size.Grow(1.6f), Gap = 14 }))
            {
                Section(ui, "Conversation", "Reasoning, tool calls, an answer with sources, and the composer.");
                ui.ChatMessage("Why do immediate mode toolkits feel so fast? Cite a few sources.", new ChatOpts { Mine = true });
                using (var think = ui.Reasoning("think", new ReasoningOpts { Thinking = _aiBusy, Seconds = _aiBusy ? elapsed : 8 }))
                {
                    if (think.Open)
                    {
                        ui.Text("The question is about perceived speed. Two parts: how little work a frame does, and how quickly input shows up on screen.", new TextOpts { Size = 13.5f, Color = Color.TextSecondary });
                        ui.Text("Look for sources on retained versus immediate mode, text rendering cost and motion.", new TextOpts { Size = 13.5f, Color = Color.TextSecondary });
                    }
                }
                ui.ToolCall("search", "web_search", new ToolCallOpts
                {
                    Icon = Icons.Globe,
                    Detail = "immediate mode gui performance",
                    Status = TaskStatus.Done,
                    Seconds = 1.4f,
                    Input = """{"query": "immediate mode gui performance", "limit": 5}""",
                    Output = """{"results": [{"title": "Immediate mode GUIs", "url": "https://fieldnotes.dev/immediate-mode"}], "took_ms": 1380}""",
                });
                ui.ToolCall("read", "read_file", new ToolCallOpts
                {
                    Icon = Icons.FileText,
                    Detail = "quarterly-report.pdf",
                    Status = _aiBusy ? TaskStatus.Running : TaskStatus.Done,
                    Seconds = _aiBusy ? elapsed : 2.1f,
                });
                if (_aiBusy) ui.AgentStatus("Reading quarterly-report.pdf", new AgentStatusOpts { Status = TaskStatus.Running, Seconds = elapsed });
                ui.Typewriter("answer",
                    "They do very little work per frame: the interface is rebuilt from your data, laid out once and drawn in a " +
                    "handful of batches, so there is no tree of objects to keep in sync. Text is the expensive part, which is " +
                    "why glyphs are cached in an atlas, and motion feels quick when it is driven by springs instead of fixed curves.",
                    new TextFxOpts { Size = 15 });
                using (ui.Row(new Box { Gap = 6, Align = Align.Center }))
                {
                    ui.Text("Sources", new TextOpts { Size = 12, Color = Color.TextMuted });
                    for (int k = 0; k < AiSources.Length; k++) ui.Citation(k + 1, AiSources[k]);
                }
                ui.SourceList("sources", AiSources);
                using (ui.Row(new Box { Gap = 8, Align = Align.Center, Width = Size.Fill }))
                {
                    ResponseEvents ra = ui.ResponseActions("actions", ref _aiVote, new ResponseActionsOpts
                    {
                        CopyText = "They do very little work per frame.",
                    });
                    if ((ra & ResponseEvents.Regenerate) != 0) _aiBusy = true;
                    ui.Spacer();
                    ui.ContextMeter("ctx", 113400, 200000, new ContextOpts
                    {
                        Parts = ContextParts,
                        PartCount = ContextParts.Length,
                    });
                }
                ComposerEvents ce = ui.ChatComposer("composer", ref _aiDraft, 512, ref _aiRemoved, ref _aiModel, new ComposerOpts
                {
                    Placeholder = "Ask a follow up",
                    Busy = _aiBusy,
                    Attachments = _aiAttachments,
                    ModelCount = AiModels.Length,
                    Models = AiModels,
                });
                if ((ce & ComposerEvents.Send) != 0)
                {
                    _aiDraft = "";
                    _aiBusy = true;
                }
                if ((ce & ComposerEvents.Stop) != 0) _aiBusy = false;
                if ((ce & ComposerEvents.Remove) != 0 && _aiRemoved >= 0 && _aiRemoved < _aiAttachments.Length)
                {
                    // Dropping an attachment is rare; rebuilding the array then is honest.
                    var files = new string[_aiAttachments.Length - 1];
                    int j = 0;
                    for (int k = 0; k < _aiAttachments.Length; k++)
                    {
                        if (k != _aiRemoved) files[j++] = _aiAttachments[k];
                    }
                    _aiAttachments = files;
                }
                if ((ce & ComposerEvents.Attach) != 0) ui.Toast("Attach files", new ToastOpts { Description = "Drop files on the window, too." });
            }

            using (ui.Column(new Box { Width = Size.Grow(1), Gap = 14 }))
            {
                Section(ui, "Agent plan", "Steps and their status.");
                _aiSteps[0] = new PlanStep("Search for sources", "3 results", TaskStatus.Done);
                _aiSteps[1] = new PlanStep("Read the attached report", _aiBusy ? "Page 12 of 40" : "40 pages", _aiBusy ? TaskStatus.Running : TaskStatus.Done);
                _aiSteps[2] = new PlanStep("Compare the numbers with last quarter", null, _aiBusy ? TaskStatus.Pending : TaskStatus.Done);
                _aiSteps[3] = new PlanStep("Draft the summary", null, _aiBusy ? TaskStatus.Pending : TaskStatus.Running);
                _aiSteps[4] = new PlanStep("Check the citations", null, TaskStatus.Pending);
                ui.AgentPlan("plan", _aiSteps, new AgentPlanOpts { Title = "Research plan" });

                Section(ui, "Voice", "Idle, listening, thinking and speaking.");
                using (ui.Column(new Box { Align = Align.Center, Gap = 12, Width = Size.Fill }))
                {
                    float level = 0.35f + (0.35f * MathF.Sin((float)now * 5.1f) * MathF.Sin((float)now * 1.7f));
                    if (ui.VoiceOrb("orb", (VoiceState)_orbState, level, new VoiceOrbOpts { Size = 170 })) _orbState = _orbState == 1 ? 0 : 1;
                    ui.Segmented("orb-state", VoiceStates, ref _orbState, new SegmentedOpts { Size = ControlSize.Sm });
                }

                Section(ui, "Suggestions", "Ways to start.");
                int sg = ui.PromptSuggestions("sugg", AiSuggestions, new PromptSuggestionsOpts { Columns = 1 });
                if (sg >= 0) _aiDraft = $"{AiSuggestions[sg].Title}: ";
            }
        }
    }
}
