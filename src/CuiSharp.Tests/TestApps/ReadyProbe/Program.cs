using CuiSharp;

// A benign headless app for the tool's real-process tests. It draws "loading" for the first
// N frames and then "data <N>", marking those frames ready with ui.MarkReady. N comes from
// `--ready-after`; 0 means never ready, which is how a test proves an unsatisfied wait stops
// the command that asked for it instead of letting it through.
string[] argv = Environment.GetCommandLineArgs();
int readyAfter = 0;
string? labelEnv = null;
for (int i = 0; i < argv.Length - 1; i++)
{
    if (argv[i] == "--ready-after") readyAfter = int.Parse(argv[i + 1]);
    if (argv[i] == "--label-env") labelEnv = argv[i + 1];
}

string loading = labelEnv is null ? "loading" : Environment.GetEnvironmentVariable(labelEnv) ?? "missing";
int drawn = 0;
return CuiApp.Run(new CuiAppOptions { Title = "ReadyProbe", Width = 320, Height = 200 }, ui =>
{
    drawn++;
    bool ready = readyAfter > 0 && drawn >= readyAfter;
    ui.Label(ready ? $"data {readyAfter}" : loading);
    if (ready) ui.MarkReady();
});
