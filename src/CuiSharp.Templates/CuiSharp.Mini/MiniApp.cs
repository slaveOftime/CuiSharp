#:package CuiSharp@__CUISHARP_VERSION__

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
