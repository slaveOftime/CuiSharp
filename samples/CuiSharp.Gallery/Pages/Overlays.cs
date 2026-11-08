// Page 6 of 9: dialogs, sheets, popovers, toasts, tooltips.
// Part of the CuiSharp.Gallery sample: Overlays
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    private void PageOverlays(Ui ui)
    {
        Section(ui, "Dialogs and sheets", "Esc or a click outside closes them. Focus stays inside.");
        using (ui.Card(new Card()))
        using (ui.Row(new Box { Gap = 8, Wrap = true }))
        {
            if (ui.Button("Edit profile", new ButtonOpts { Icon = Icons.Pencil })) _dialogOpen = true;
            if (ui.Button("Open sheet", new ButtonOpts { Icon = Icons.PanelLeft })) _sheetOpen = true;
            if (ui.Button("Delete project", new ButtonOpts
            {
                Variant = ButtonVariant.Danger,
                Icon = Icons.Trash2,
            })) _confirmOpen = true;
            if (ui.Button("Command palette", new ButtonOpts
            {
                Variant = ButtonVariant.Outline,
                Icon = Icons.Command,
                Tooltip = "Search everything",
                Shortcut = "Ctrl K",
            })) _paletteOpen = true;
        }

        Section(ui, "Popover and context menu", null);
        using (ui.Card(new Card { Gap = 16 }))
        {
            using (ui.Row(new Box { Gap = 8, Wrap = true }))
            {
                if (ui.Button("Filters", new ButtonOpts { Icon = Icons.Filter })) ui.PopoverToggle("filters");

                using (var pop = ui.Popover("filters", new PopoverOpts { Width = 280 }))
                {
                    if (pop.Open)
                    {
                        ui.Label("Filter results");
                        ui.Checkbox("Only active", ref _newsletter);
                        ui.Checkbox("Include archived", ref _marketing);
                        ui.Slider("Minimum score", ref _brightness, 0, 100, new SliderOpts { ShowValue = true });
                        using (ui.Row(new Box { Gap = 8, Justify = Justify.End }))
                        {
                            if (ui.Button("Apply", new ButtonOpts
                            {
                                Variant = ButtonVariant.Primary,
                                Size = ControlSize.Sm,
                            })) ui.PopoverClose("filters");
                        }
                    }
                }
            }

            using (ui.Card(new Card
            {
                Muted = true,
                Id = "ctx-area",
                Pad = 28,
                Align = Align.Center,
                Hoverable = true,
            }))
            {
                ui.Icon(Icons.Ellipsis, 18, Color.TextMuted);
                ui.Text("Right click this area", new TextOpts
                {
                    Color = Color.TextSecondary,
                    Align = TextAlign.Center,
                });
            }

            using (var ctx = ui.ContextMenu("ctx"))
            {
                if (ctx.Open)
                {
                    ui.MenuItem("Cut", new MenuItemOpts { Icon = Icons.Scissors, Shortcut = "Ctrl X" });
                    ui.MenuItem("Copy", new MenuItemOpts { Icon = Icons.Copy, Shortcut = "Ctrl C" });
                    ui.MenuItem("Paste", new MenuItemOpts { Icon = Icons.Clipboard, Shortcut = "Ctrl V" });
                    ui.MenuSeparator();
                    ui.MenuItem("Delete", new MenuItemOpts { Icon = Icons.Trash2, Danger = true });
                }
            }
        }

        Section(ui, "Toasts", "They stack, expand on hover and pause while you read.");
        using (ui.Card(new Card()))
        using (ui.Row(new Box { Gap = 8, Wrap = true }))
        {
            if (ui.Button("Save profile"))
            {
                ui.Toast("Changes saved", new ToastOpts
                {
                    Description = "Your profile is up to date.",
                    Tone = Tone.Success,
                });
            }
            if (ui.Button("Upload a 30 MB file"))
            {
                ui.Toast("Upload failed", new ToastOpts
                {
                    Description = "The file is larger than 25 MB.",
                    Tone = Tone.Danger,
                });
            }
            if (ui.Button("Archive a message"))
            {
                ui.Toast("Message archived", new ToastOpts { Action = "Undo" });
            }
            if (ui.Button("Check for updates"))
            {
                ui.Toast("Update available", new ToastOpts
                {
                    Description = "Version 1.1 is ready to install.",
                    Tone = Tone.Info,
                });
            }
        }

        Section(ui, "Tooltips", null);
        using (ui.Card(new Card()))
        using (ui.Row(new Box { Gap = 4, Wrap = true }))
        {
            ui.IconButton(Icons.Bold, new ButtonOpts { Tooltip = "Bold", Shortcut = "Ctrl B" });
            ui.IconButton(Icons.Italic, new ButtonOpts { Tooltip = "Italic", Shortcut = "Ctrl I" });
            ui.IconButton(Icons.Underline, new ButtonOpts { Tooltip = "Underline", Shortcut = "Ctrl U" });
            ui.Separator();
            ui.IconButton(Icons.AlignLeft, new ButtonOpts { Tooltip = "Align left" });
            ui.IconButton(Icons.List, new ButtonOpts { Tooltip = "Bulleted list" });
            ui.IconButton(Icons.Link, new ButtonOpts { Tooltip = "Insert link" });
        }
    }
}
