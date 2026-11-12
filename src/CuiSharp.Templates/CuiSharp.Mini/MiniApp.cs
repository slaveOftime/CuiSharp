#:package CuiSharp@__CUISHARP_VERSION__

// This file and its .ico are the whole app: no project, no solution, nothing to set up.
//
// Start it
//   dotnet run MiniApp.cs
//   A JIT build of this one file, so the window is up in a second or two, and in a quarter of a
//   second once it has been built. Nothing is compiled ahead of time, and there is no project or
//   solution to keep in step.
//
// Ship it
//   dotnet publish MiniApp.cs -c Release -o .
//   One native executable, MiniApp.exe, beside this file — about 1.2 MB, just the exe beside
//   the source, and no .NET runtime on the machine that runs it. Without -o it lands in
//   artifacts/MiniApp/ instead, which keeps the source folder clean.
//   A publish also trims the binary, and that includes the channel `dotnet cui` drives an app
//   with: add -p:CuiSharpHeadless=true if a tool should inspect the published exe.
//
// The properties below: OutputType=WinExe makes the exe a GUI-subsystem binary, so the
// published app opens its window without a console behind it; ApplicationIcon embeds the .ico
// beside this file, so replacing MiniApp.ico with your own and rebuilding changes the exe and
// window icon. The rest make that binary small: no ICU tables (this app formats the invariant
// way), size rather than speed in the generated code, and no debug symbols — that last pair is
// the difference between one file and an exe next to a six-megabyte .pdb.
// 'dotnet new cui-app' sets the same ones in its project file, for the same reasons.
#:property PublishAot=true
#:property OutputType=WinExe
#:property ApplicationIcon=MiniApp.ico
#:property OptimizationPreference=Size
#:property InvariantGlobalization=true
#:property StripSymbols=true
#:property DebugType=none

using CuiSharp;

int clicks = 0;
return CuiApp.Run(new CuiAppOptions { Title = "MiniApp", Width = 480, Height = 320 }, ui =>
{
    using (ui.Column(new Box { Pad = 24, Gap = 12 }))
    {
        ui.Heading("MiniApp", 1);
        if (ui.Button("Increment", new ButtonOpts { Variant = ButtonVariant.Primary })) clicks++;
        ui.Text($"Clicked {clicks} times");
        if (ui.Button("Toggle theme")) ui.ToggleTheme();
    }
});
