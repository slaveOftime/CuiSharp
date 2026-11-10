using CuiSharp;
using CuiSharp.App;
using CuiSharp.App.Pages;
using CuiSharp.App.Services;
using CuiSharp.Hosting;
using Microsoft.Extensions.DependencyInjection;

// Composition root, generic-host style: register services and pages, describe the
// window, then run the root component on this thread. No view models, no binding.
var builder = CuiApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TodoService>();
builder.Services.AddSingleton<PriceFeed>();
builder.Services.AddSingleton<MarketFeed>();
builder.Services.AddSingleton<TodoPage>();
builder.Services.AddSingleton<MarketsPage>();
builder.Services.AddSingleton<App>();

// The accent drives accent-coloured buttons, badges and chart series, the theme decides light or
// dark, and the app switches the theme later (Ctrl+J). Every option has a default, so an app only
// sets what it cares about.
builder.Window.Title = "CuiSharp.App";
builder.Window.Width = 980;
builder.Window.Height = 780;
builder.Window.MinWidth = 560;
builder.Window.MinHeight = 480;
builder.Window.Accent = Accent.Violet;
builder.Window.Theme = Theme.Light;

// `--check` prints what this machine is actually running — renderer, backend, display, the native
// library's ABI floor — and exits without opening a window. It is the first thing to ask for when a
// station behaves differently from the machine the app was written on.
if (args.Contains("--check"))
{
    Console.WriteLine(CuiDiagnostics.Report());
    return 0;
}

using var app = builder.Build();

// Once the window exists, report the renderer it really got: `Auto` may have fallen back.
builder.Window.WindowCreated = window => Console.WriteLine(CuiDiagnostics.Report(window));

return app.Run<App>();
