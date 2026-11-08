// Explicit, not inherited from the enclosing namespace: `dotnet new` renames
// CuiSharp.App to the chosen name, and then `Ui` no longer resolves through nesting.
using CuiSharp;
using CuiSharp.App.Pages;
using CuiSharp.Hosting;

namespace CuiSharp.App;

/// <summary>
/// The window: a title, a page switch, the visible page, and the app's own numbers.
/// <para>
/// The shell owns navigation and nothing else. Each page is a component holding its own state,
/// so a screen is a class, a registration in <c>Program.cs</c>, and one more entry in this
/// constructor — no plumbing in between.
/// </para>
/// </summary>
public sealed class App(TodoPage todos, MarketsPage markets) : Component
{
    private static readonly string[] Modes = ["Light", "Dark"];
    private static readonly string[] ModeIcons = [Icons.Sun, Icons.Moon];

    private readonly Component[] _pages = [todos, markets];
    private readonly string[] _titles = [todos.Title, markets.Title];
    private int _index;

    protected override void Draw(Ui ui)
    {
        // Shortcuts are asked for, not handled: no key state, no handlers, no registration.
        if (ui.Shortcut(Mods.Ctrl, Key.Tab)) _index = (_index + 1) % _pages.Length;
        if (ui.Shortcut(Mods.Ctrl, Key.J)) ui.ToggleTheme();

        using (ui.Column(new() { Width = Size.Fill, Height = Size.Fill, Pad = 20, Gap = 14 }))
        {
            Header(ui);

            using (ui.Column(new() { Width = Size.Fill, Height = Size.Fill, Gap = 10 }))
            {
                ui.Component(_pages[_index]);
            }

            Footer(ui);
        }
    }

    private void Header(Ui ui)
    {
        using (ui.Row(new() { Gap = 12, Align = Align.Center }))
        {
            ui.Heading("CuiSharp starter", 3);
            ui.Spacer();

            ui.Tabs("pages", _titles, ref _index, new() { Variant = TabsVariant.Pill });
            ThemeSwitch(ui);
        }

        ui.Separator();
    }

    /// <summary>
    /// The whole theme switch. CUI holds the current theme, so the frame reads it for the selected
    /// segment and assigns it back when the segment changes; the switch itself is animated.
    /// </summary>
    private static void ThemeSwitch(Ui ui)
    {
        int mode = ui.Theme == Theme.Dark ? 1 : 0;
        if (ui.Segmented("theme", Modes, ref mode, new() { Icons = ModeIcons, Size = ControlSize.Sm }))
        {
            ui.Theme = mode == 1 ? Theme.Dark : Theme.Light;
        }
    }

    private static void Footer(Ui ui)
    {
        // The host fills FrameStats in around every frame, so the app shows its own numbers
        // without owning a clock. `alloc` is the one to watch: it reads 0 B per frame, and a
        // number there is something in Draw that allocates.
        // For the same numbers in a file (a WinExe has no console), run with CUI_TRACE=trace.txt.
        using (ui.Row(new() { Gap = 12, Align = Align.Center }))
        {
            // A window sleeps while nothing changes, so a frame rate only means something while
            // something is asking for frames. Two branches, not one conditional: a conditional
            // with a string in one arm makes the other arm a string too, and formatting a string
            // per frame is the allocation this footer exists to catch.
            if (FrameStats.IsLive) ui.Badge($"{FrameStats.Fps:0} fps", Status(accent: true));
            else ui.Badge("idle", Status(accent: false));

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
