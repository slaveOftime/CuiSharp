#:package CuiSharp@*

// One file, one native window. Needs the .NET 10 SDK.
//   dotnet run MiniApp.cs
//   dotnet publish MiniApp.cs -c Release -o .
//
// The publish leaves one executable that needs no .NET runtime on the target machine.
// It compiles the two driving channels out to save space: add -p:CuiSharpHeadless=true and
// -p:CuiSharpLive=true when a tool has to drive that executable.
//
// Install `dotnet cui` once (dotnet tool install -g CuiSharp.Tools), then drive this app with no
// window of your own — the tool builds it, runs it headless and prints the frame:
//   dotnet cui tree -p MiniApp.cs
//   dotnet cui docs
//
// Create steps.txt with these two lines:
//   click Increment
//   waittext 10 Clicked 1 times
// Run both steps in the same app:
//   dotnet cui session -p MiniApp.cs --script steps.txt
// The click starts a 3-second delay: the button shows a spinner and is disabled, and the count
// stays at 0. When the delay ends, the count reaches 1 and the button is enabled again.
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
using CuiSharp.Hosting;

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
// Wait without blocking Draw, then update the local counter on the UI thread.
using var increment = new Mutation(
    static token => Task.Delay(TimeSpan.FromSeconds(3), token),
    onSuccess: () => clicks++);

return CuiApp.Run(options, ui =>
{
    using (ui.Column(new Box { Pad = 24, Gap = 12 }))
    {
        ui.Heading("MiniApp", 1);

        using (ui.Disabled(increment.IsRunning))
        {
            ui.ActionButton("Increment", increment, new() { Variant = ButtonVariant.Primary, Icon = Icons.Plus });
        }

        ui.Text($"Clicked {clicks} times");

        if (ui.Button("Toggle theme"))
        {
            ui.ToggleTheme();
        }
    }
});
