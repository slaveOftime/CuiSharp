// Page 15 of 28: inline edit, scrub fields, money inputs, affixed text fields,
// a shortcut recorder and the emoji picker.
// Part of the CuiSharp.Gallery sample: Smart inputs
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    private string _issueTitle = "Q3 roadmap";
    private string _issueOwner = "Jonas Berg";
    private float _opacity = 80, _radius = 12, _blur = 24;
    private double _amount = 1249.5, _salary = 98000;
    private KeyCombo _paletteKey = new((int)(Mods.Primary | Mods.Shift), Key.P);
    private KeyCombo _searchKey;
    private string _affixUrl = "cui.dev", _weight = "72";
    private string _emojiMessage = "Release notes are up ";
    private string _emojiRecent = "";
    private int _emojiSkin;

    private void PageSmartInputs(Ui ui)
    {
        Section(ui, "Inline edit", "Click the text to edit it in place. Enter saves, Esc cancels.");
        using (ui.Column(new Box { Gap = 6 }))
        {
            ui.InlineEdit("title", ref _issueTitle, o: new InlineEditOpts
            {
                Font = Font.Display,
                Size = 26,
            });
            ui.InlineEdit("owner", ref _issueOwner, o: new InlineEditOpts { Placeholder = "Add an owner" });
        }

        Section(ui, "Scrub fields", "Drag sideways to change the value, Shift for fine steps, click to type.");
        using (ui.Row(new Box { Gap = 12, Wrap = true }))
        {
            ui.Scrub("Opacity", ref _opacity, 0, 100, new ScrubOpts { Suffix = "%" });
            ui.Scrub("Radius", ref _radius, 0, 48, new ScrubOpts { Step = 0.5f, Format = "%.1f", Suffix = "px" });
            ui.Scrub("Blur", ref _blur, 0, 80, new ScrubOpts { Suffix = "px", Width = Size.Px(140) });
        }

        Section(ui, "Money", "Grouped digits at rest, plain digits while typing.");
        using (ui.Row(new Box { Gap = 12, Align = Align.Start }))
        {
            using (ui.Box(new Box { Width = Size.Px(240) }))
            {
                ui.MoneyInput("amount", ref _amount, new MoneyOpts { Label = "Amount", Code = "USD" });
            }
            using (ui.Box(new Box { Width = Size.Px(240) }))
            {
                ui.MoneyInput("salary", ref _salary, new MoneyOpts
                {
                    Label = "Salary",
                    Symbol = "€",
                    Code = "EUR",
                    Decimals = -1,
                    Hint = "Gross per year",
                });
            }
        }

        Section(ui, "Prefix and suffix", "Any text field can carry muted text before and after the value.");
        using (ui.Row(new Box { Gap = 12, Wrap = true }))
        {
            ui.Input("url", ref _affixUrl, new InputOpts { Prefix = "https://", Width = Size.Px(260) });
            ui.Input("weight", ref _weight, new InputOpts { Suffix = "kg", Width = Size.Px(120) });
        }

        Section(ui, "Shortcut recorder", "Click, then press a combination. Backspace clears.");
        using (ui.Row(new Box { Gap = 12, Wrap = true }))
        {
            ui.ShortcutRecorder("palette-key", ref _paletteKey);
            ui.ShortcutRecorder("search-key", ref _searchKey, new ShortcutRecorderOpts { Placeholder = "Record shortcut" });
        }

        Section(ui, "Emoji picker", "Search by name, jump by group, pick a skin tone. Recent emoji come first.");
        using (ui.Row(new Box
        {
            Gap = 8,
            Align = Align.Center,
            Width = Size.Fill,
            MaxWidth = 480,
        }))
        {
            ui.Input("message", ref _emojiMessage, new InputOpts { Width = Size.Fill });
            if (ui.IconButton(Icons.Smile, new ButtonOpts { Tooltip = "Emoji" })) ui.PopoverToggle("emoji-pop");
            using (var pop = ui.Popover("emoji-pop", new PopoverOpts
            {
                Placement = Placement.BottomEnd,
                Width = 340,
                Pad = 0,
            }))
            {
                if (pop.Open)
                {
                    string? emoji = ui.EmojiPicker("emoji", ref _emojiRecent, ref _emojiSkin, new EmojiPickerOpts
                    {
                        Height = 260,
                    });
                    if (emoji is not null && _emojiMessage.Length + emoji.Length < 160)
                    {
                        _emojiMessage += emoji;
                        ui.PopoverClose("emoji-pop");
                    }
                }
            }
        }
    }
}
