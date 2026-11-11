// Page 2 of 28: button variants, sizes, states, menus.
// Part of the CuiSharp.Gallery sample: Buttons
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    private void PageButtons(Ui ui)
    {
        Section(ui, "Variants", "One primary action per view. Secondary for everything else.");
        using (ui.Card(new Card()))
        using (ui.Row(new Box { Gap = 8, Wrap = true }))
        {
            ui.Button("Primary", new ButtonOpts { Variant = ButtonVariant.Primary });
            ui.Button("Secondary");
            ui.Button("Outline", new ButtonOpts { Variant = ButtonVariant.Outline });
            ui.Button("Ghost", new ButtonOpts { Variant = ButtonVariant.Ghost });
            ui.Button("Danger", new ButtonOpts { Variant = ButtonVariant.Danger });
            ui.Button("Link", new ButtonOpts { Variant = ButtonVariant.Link });
        }

        Section(ui, "Sizes and icons", null);
        using (ui.Card(new Card { Gap = 16 }))
        {
            using (ui.Row(new Box { Gap = 8, Wrap = true }))
            {
                ui.Button("Small", new ButtonOpts { Size = ControlSize.Sm });
                ui.Button("Medium");
                ui.Button("Large", new ButtonOpts { Size = ControlSize.Lg });
            }
            using (ui.Row(new Box { Gap = 8, Wrap = true }))
            {
                ui.Button("New project", new ButtonOpts { Variant = ButtonVariant.Primary, Icon = Icons.Plus });
                ui.Button("Download", new ButtonOpts { Icon = Icons.Download });
                ui.Button("Continue", new ButtonOpts { Variant = ButtonVariant.Outline, IconRight = Icons.ArrowRight });
                ui.IconButton(Icons.Copy, new ButtonOpts { Tooltip = "Copy", Shortcut = "Ctrl C" });
                ui.IconButton(Icons.Share2, new ButtonOpts { Tooltip = "Share" });
                ui.IconButton(Icons.Heart, new ButtonOpts { Tooltip = "Like", Variant = ButtonVariant.Outline });
                ui.IconButton(Icons.Trash2, new ButtonOpts { Tooltip = "Delete", Variant = ButtonVariant.Secondary });
            }
        }

        Section(ui, "States", "Loading keeps the label so the layout does not jump.");
        using (ui.Card(new Card()))
        using (ui.Row(new Box { Gap = 8, Wrap = true }))
        {
            if (ui.Button(_loading ? "Saving" : "Save changes", new ButtonOpts
            {
                Variant = ButtonVariant.Primary,
                Loading = _loading,
            })) _loading = true;
            if (ui.Button("Reset##loading")) _loading = false;
            ui.Button("Disabled", new ButtonOpts { Disabled = true });
            ui.Button("Selected", new ButtonOpts
            {
                Variant = ButtonVariant.Ghost,
                Selected = true,
                Icon = Icons.Check,
            });
        }

        Section(ui, "Groups, copy and hold to confirm",
            "Hold the button until it fills to confirm a destructive action.");
        using (ui.Card(new Card()))
        using (ui.Row(new Box { Gap = 12, Wrap = true }))
        {
            using (ui.ButtonGroup())
            {
                ui.Button("Bold##group", new ButtonOpts { Icon = Icons.Bold });
                ui.Button("Italic##group", new ButtonOpts { Icon = Icons.Italic });
                ui.Button("Underline##group", new ButtonOpts { Icon = Icons.Underline });
            }

            using (ui.Row(new Box
            {
                Gap = 6,
                Bg = Color.SurfaceMuted,
                Radius = 18,
                PadLeft = 14,
                PadRight = 4,
                PadY = 2,
            }))
            {
                ui.Text("npm i cui", new TextOpts { Font = Font.Mono, NoWrap = true });
                ui.CopyButton("install", "npm i cui");
            }

            if (ui.HoldButton("Hold to delete", new HoldOpts { Icon = Icons.Trash2 }))
            {
                ui.Toast("Deleted", new ToastOpts { Tone = Tone.Danger });
            }
        }

        Section(ui, "Segmented control", "A highlight glides to the selected option.");
        using (ui.Card(new Card { Gap = 16 }))
        {
            ui.Segmented("view", ViewNames, ref _segView, new SegmentedOpts { Icons = ViewIcons });
            ui.Segmented("range", RangeNames, ref _segSize, new SegmentedOpts { Size = ControlSize.Sm });
        }

        Section(ui, "Dropdown menu", null);
        using (ui.Card(new Card()))
        using (ui.Row(new Box { Gap = 8, Wrap = true }))
        {
            using (var menu = ui.Menu("Options", new MenuOpts { Icon = Icons.Settings }))
            {
                if (menu.Open)
                {
                    ui.MenuLabel("Project");
                    ui.MenuItem("Rename", new MenuItemOpts { Icon = Icons.Pencil, Shortcut = "F2" });
                    ui.MenuItem("Duplicate", new MenuItemOpts { Icon = Icons.Copy, Shortcut = "Ctrl D" });
                    ui.MenuItem("Share", new MenuItemOpts { Icon = Icons.Share2 });
                    ui.MenuSeparator();
                    if (ui.MenuItem("Delete", new MenuItemOpts
                    {
                        Icon = Icons.Trash2,
                        Danger = true,
                    })) _confirmOpen = true;
                }
            }

            using (var menu = ui.Menu("More actions", new MenuOpts { IconOnly = true, Icon = Icons.Ellipsis }))
            {
                if (menu.Open)
                {
                    ui.MenuItem("Archive", new MenuItemOpts { Icon = Icons.Archive });
                    ui.MenuItem("Export", new MenuItemOpts { Icon = Icons.Download });
                }
            }
        }
    }
}
