#:package CuiSharp@*

// One file, one native window. Needs the .NET 10 SDK.
//   dotnet run MiniApp.cs
//   dotnet publish MiniApp.cs -c Release -o .
//
// The publish leaves one ~1.2 MB exe that needs no .NET runtime on the target machine, and it
// compiles the two driving channels out to keep it that size: add -p:CuiSharpHeadless=true and
// -p:CuiSharpLive=true when a tool has to drive that exe.
//
// Install `dotnet cui` once (dotnet tool install -g CuiSharp.Tools), then drive this app with no
// window of your own — the tool builds it, runs it headless and prints the frame:
//   dotnet cui tree -p MiniApp.cs
//   dotnet cui click Increment -p MiniApp.cs
//   dotnet cui session -p MiniApp.cs < steps.txt
//   dotnet cui docs
//
// One command is one process, starting at frame 1; a session keeps its lines in one process, so
// state carries from one to the next. `-a` drives the window you are watching instead, left open —
// `dotnet cui quit -a` closes it — which is what LiveAttach below allows. Headless needs no code:
// `CUISHARP_HEADLESS=1 dotnet run MiniApp.cs`, or `-- --headless`.
//
// Replace MiniApp.ico to change the executable and the window icon.
#:property PublishAot=true
#:property OutputType=WinExe
#:property ApplicationIcon=MiniApp.ico
#:property OptimizationPreference=Size
#:property InvariantGlobalization=true
#:property StripSymbols=true
#:property DebugType=none

using CuiSharp;

var options = new CuiAppOptions
{
    Title = "MiniApp",
    Width = 480,
    Height = 320,
    // Lets `dotnet cui … -a` drive this window while you look at it. File-based apps run in
    // Release too; published apps still omit the listener unless CuiSharpLive=true.
    LiveAttach = true,
};

var clicks = 0;

return CuiApp.Run(options, ui =>
{
    using (ui.Column(new Box { Pad = 24, Gap = 12 }))
    {
        ui.Heading("MiniApp", 1);

        if (ui.Button("Increment", new ButtonOpts { Variant = ButtonVariant.Primary }))
        {
            clicks++;
        }

        ui.Text($"Clicked {clicks} times");

        if (ui.Button("Toggle theme"))
        {
            ui.ToggleTheme();
        }
    }
});
