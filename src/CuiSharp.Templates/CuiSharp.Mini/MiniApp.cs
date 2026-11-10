#:package CuiSharp@__CUISHARP_VERSION__

// A published single file is the whole product here, so the two switches that decide its size are
// set where a file-based app can set them: smaller native code, and no ICU tables for a demo that
// formats numbers the invariant way. The app template ('dotnet new cui-app') does the same in its
// project file, with the same reasoning.
#:property OptimizationPreference=Size
#:property InvariantGlobalization=true

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
