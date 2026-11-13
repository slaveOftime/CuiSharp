// Explicit, not inherited from the enclosing namespace: `dotnet new` renames
// CuiSharp.App to the chosen name, and then `Ui` no longer resolves through nesting.
using CuiSharp;
using CuiSharp.App.Pages;
using CuiSharp.Hosting;

namespace CuiSharp.App;

/// <summary>
/// Application shell: top-level page tabs, theme switch, active page, and frame stats footer.
/// </summary>
/// <remarks>
/// The three region containers are named — <c>header</c>, <c>page</c>, <c>footer</c> — so a frame
/// reads as three regions: <c>dotnet cui tree</c> nests each control under the one it belongs to,
/// and the layout state behind each region has an id that survives a resize.
/// </remarks>
public sealed class App(TodoPage todos, MarketsPage markets) : Component
{
    private static readonly string[] Modes = ["Light", "Dark"];
    private static readonly string[] ModeIcons = [Icons.Sun, Icons.Moon];

    private readonly Component[] _pages = [todos, markets];
    private readonly string[] _titles = [todos.Title, markets.Title];
    private int _index;

    protected override void Draw(Ui ui)
    {
        if (!ui.WantsKeyboard && ui.Shortcut(Mods.Ctrl, Key.Tab))
        {
            _index = (_index + 1) % _pages.Length;
        }

        if (!ui.WantsKeyboard && ui.Shortcut(Mods.Ctrl, Key.J))
        {
            ui.ToggleTheme();
        }

        using (ui.Column(new() { Width = Size.Fill, Height = Size.Fill, Pad = 20, Gap = 14 }))
        {
            Header(ui);

            using (ui.Column(new() { Id = "page", Width = Size.Fill, Height = Size.Fill, Gap = 10 }))
            {
                ui.Component(_pages[_index]);
            }

            Footer(ui);
        }
    }

    private void Header(Ui ui)
    {
        using (ui.Row(new() { Id = "header", Gap = 12, Align = Align.Center }))
        {
            ui.Heading("CuiSharp starter", 3);
            ui.Spacer();

            ui.Tabs("pages", _titles, ref _index, new() { Variant = TabsVariant.Pill });
            ThemeSwitch(ui);
        }

        ui.Separator();
    }

    private static void ThemeSwitch(Ui ui)
    {
        var mode = ui.Theme == Theme.Dark ? 1 : 0;
        if (ui.Segmented("theme", Modes, ref mode, new() { Icons = ModeIcons, Size = ControlSize.Sm }))
        {
            ui.Theme = mode == 1 ? Theme.Dark : Theme.Light;
        }
    }

    private static void Footer(Ui ui)
    {
        // FrameStats is updated around every Draw call; CUI_TRACE=trace.txt logs the same numbers.
        using (ui.Row(new() { Id = "footer", Gap = 12, Align = Align.Center }))
        {
            // Two branches rather than a ternary string: a ternary evaluates to a managed string
            // before UiText sees it, which would allocate on every live frame.
            if (FrameStats.IsLive)
            {
                ui.Badge($"{FrameStats.Fps:0} fps", Status(accent: true));
            }
            else
            {
                ui.Badge("idle", Status(accent: false));
            }

            ui.Secondary($"draw {FrameStats.DrawMs:0.00} ms");
            ui.Secondary($"frame {FrameStats.FrameMs:0.0} ms");
            ui.Secondary($"alloc {FrameStats.AllocatedBytes} B");
            ui.Spacer();
            ui.Secondary("Ctrl+Tab next page · Ctrl+J theme");
        }
    }

    private static BadgeOpts Status(bool accent) => new()
    {
        Tone = accent ? Tone.Accent : Tone.Neutral,
        Size = ControlSize.Sm,
        Dot = accent,
    };
}
