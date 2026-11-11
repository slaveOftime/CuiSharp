// The app shapes tools/size-report.ps1 measures. One block per shape, chosen at build time with
// -p:Shape=...: adding the widget is the only difference between two builds, so the difference in
// output size is what that widget costs.
//
// Every shape starts from the same frame: a column, a heading, a button and a line of text. That is
// the smallest app that is still a CUI app, and MIN measures it.

using CuiSharp;

int clicks = 0;
return CuiApp.Run(new CuiAppOptions { Title = "Shape", Width = 900, Height = 600 }, ui =>
{
    using (ui.Column(new Box { Pad = 24, Gap = 12 }))
    {
        ui.Heading("Shape", 1);
        if (ui.Button("Increment", new ButtonOpts { Variant = ButtonVariant.Primary })) clicks++;
        ui.Text($"Clicked {clicks} times");

#if TABLE
        using (ui.Table("t", [new TableColumn("Name"), new TableColumn("Value")], new TableOpts { Height = 300 }))
        {
            for (int r = 0; r < 8; r++)
            {
                ui.TableRow(false);
                ui.Text($"row {r}");
                ui.Text($"{r * 3}");
            }
        }
#endif

#if CHART
        ui.LineChart("c", [new Series { Values = Sine(200) }], new ChartOpts { Height = 200 });
#endif

#if FORM
        ui.InlineEdit("name", ref _name, 256);
        ui.Checkbox("I agree", ref _agree);
        ui.Slider("level", ref _level, 0, 100);
#endif

#if RICH
        ui.Markdown("md", "## Heading\n\nSome **bold** text and a [link](https://example.com).\n\n- one\n- two");
        ui.Code("let x = 1;");
#endif
    }
});

#if CHART
static float[] Sine(int n)
{
    var v = new float[n];
    for (int i = 0; i < n; i++) v[i] = MathF.Sin(i * 0.1f) * 50 + 50;
    return v;
}
#endif

#if FORM
partial class Program
{
    static string _name = "";
    static bool _agree;
    static float _level = 50;
}
#endif
