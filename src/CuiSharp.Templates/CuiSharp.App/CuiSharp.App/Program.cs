using CuiSharp;
using CuiSharp.App;
using CuiSharp.App.Pages;
using CuiSharp.App.Services;
using CuiSharp.Hosting;
using Microsoft.Extensions.DependencyInjection;

// `--check` reports the active renderer, backend, display, and native ABI floor without opening a window.
if (args.Contains("--check"))
{
    Console.WriteLine(CuiDiagnostics.Report());
    return 0;
}

var builder = CuiApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TodoService>();
builder.Services.AddSingleton<PriceFeed>();
builder.Services.AddSingleton<MarketFeed>();
builder.Services.AddSingleton<TodoPage>();
builder.Services.AddSingleton<MarketsPage>();
builder.Services.AddSingleton<App>();

builder.Window.Title = "CuiSharp.App";
builder.Window.Width = 980;
builder.Window.Height = 780;
builder.Window.MinWidth = 560;
builder.Window.MinHeight = 480;
builder.Window.Accent = Accent.Violet;
builder.Window.Theme = Theme.Light;
builder.Window.WindowCreated = window => Console.WriteLine(CuiDiagnostics.Report(window));

// `dotnet cui tree -p CuiSharp.App` builds and runs a fresh headless app for each invocation;
// `dotnet cui session -p CuiSharp.App < cmds.txt` keeps a multi-step flow in one app.
// `dotnet cui tree -a` instead drives the existing window, preserving its state between commands.
// LiveAttach enables that listener (Windows-only); it does not open another window.
builder.Window.LiveAttach = OperatingSystem.IsWindows();

using var app = builder.Build();
return app.Run<App>();
