// The window shell: the frame loop, the sidebar and the overlays.
// Part of the CuiSharp.Gallery sample: shell and overlays
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    public void Frame(Ui ui)
    {
        if (!ui.WantsKeyboard)
        {
            if (ui.Shortcut(Mods.Primary, Key.K)) _paletteOpen = !_paletteOpen;
            if (ui.Shortcut(Mods.Primary, Key.J)) ui.ToggleTheme();
        }

        bool narrow = ui.ViewportSize().X < 760f;
        if (!narrow) _drawer = false;

        using (ui.Row(new Box
        {
            Width = Size.Fill,
            Height = Size.Fill,
            Align = Align.Stretch,
        }))
        {
            if (!narrow)
            {
                Sidebar(ui, drawer: false);
                ui.Separator();
            }

            using (ui.Scroll(new ScrollOpts { Id = "main", Width = Size.Fill, FadeEdges = true }))
            using (ui.Row(new Box { Justify = Justify.Center, Align = Align.Start }))
            using (ui.Column(new Box
            {
                Width = Size.Fill,
                MaxWidth = 920,
                PadX = narrow ? 16 : 40,
                PadTop = narrow ? 12 : 36,
                PadBottom = 64,
                Gap = 20,
            }))
            {
                if (narrow)
                {
                    using (ui.Row(new Box { Gap = 8, Width = Size.Fill }))
                    {
                        if (ui.IconButton(Icons.Menu, new ButtonOpts { Tooltip = "Components" })) _drawer = true;
                        ui.Text("CUI", new TextOpts { Font = Font.Medium, Size = 15 });
                    }
                }

                if (Page != 0)
                {
                    using (ui.Column(new Box { Gap = 6, PadBottom = 8 }))
                    {
                        ui.Heading(Page < PageCount ? PageNames[Page] : string.Empty, 2);
                    }
                }

                // The extended pages share the C gallery's habit of living in their own id
                // scope, so "name" on two pages stays two widgets. The thirteen original
                // pages carry global ids, as they always did.
                if (Page >= FirstExtendedPage)
                {
                    using (ui.Id(Page))
                    {
                        DrawPage(ui);
                    }
                }
                else
                {
                    DrawPage(ui);
                }
            }
        }

        // The scene textures belong to the render context but only the media page
        // shows them; off that page they are released, and made again on the way back.
        if (Page != MediaPage) ReleaseSceneTextures(ui);

        using (var sheet = ui.Sheet("CUI", ref _drawer, new SheetOpts { Side = Side.Left, Size = 280 }))
        {
            if (sheet.Open) Sidebar(ui, drawer: true);
        }

        Dialogs(ui);
    }

    private void DrawPage(Ui ui)
    {
        switch (Page)
        {
            case 0: PageOverview(ui); break;
            case 1: PageButtons(ui); break;
            case 2: PageInputs(ui); break;
            case 3: PageSelection(ui); break;
            case 4: PageNavigation(ui); break;
            case 5: PageOverlays(ui); break;
            case 6: PageFeedback(ui); break;
            case 7: PageData(ui); break;
            case 8: PageCharts(ui); break;
            case 9: PageStudio(ui); break;
            case 10: PageTools(ui); break;
            case 11: PageDeveloperTools(ui); break;
            case 12: PageMoreDeveloperTools(ui); break;
            case 13: PageActions(ui); break;
            case 14: PageSmartInputs(ui); break;
            case 15: PagePickers(ui); break;
            case 16: PageCapture(ui); break;
            case 17: PageDisclosure(ui); break;
            case 18: PageReader(ui); break;
            case 19: PageGrid(ui); break;
            case 20: PageCode(ui); break;
            case 21: PageSocial(ui); break;
            case 22: PagePlanning(ui); break;
            case 23: PageViz(ui); break;
            case 24: PageMotion(ui); break;
            case 25: PageAi(ui); break;
            case 26: PageAudio(ui); break;
            default: PageFiles(ui); break;
        }
    }

    private void Sidebar(Ui ui, bool drawer)
    {
        using (ui.Column(new Box
        {
            Width = drawer ? Size.Fill : Size.Px(240),
            Height = Size.Fill,
            Bg = drawer ? Color.Transparent : Color.SurfaceMuted,
            Pad = drawer ? 0 : 12,
            Gap = 2,
        }))
        {
            if (!drawer)
            {
                using (ui.Row(new Box { Gap = 10, PadX = 8, PadTop = 6, PadBottom = 18 }))
                {
                    using (ui.Box(new Box
                    {
                        Width = Size.Px(28),
                        Height = Size.Px(28),
                        Radius = 9,
                        Bg = Color.Foreground,
                        Align = Align.Center,
                        Justify = Justify.Center,
                    }))
                    {
                        ui.Text("C", new TextOpts
                        {
                            Font = Font.Display,
                            Size = 16,
                            Color = Color.Surface,
                            LineHeight = 1f,
                        });
                    }
                    ui.Text("CUI", new TextOpts { Font = Font.Medium, Size = 15, NoWrap = true });
                    ui.Spacer();
                    ui.Badge("v1.0", new BadgeOpts { Size = ControlSize.Sm });
                }
            }

            using (ui.Scroll(new ScrollOpts { Id = "nav", Height = Size.Fill, Gap = 2 }))
            {
                ui.NavSection("Components");
                for (int i = 0; i < PageCount; i++)
                {
                    if (ui.NavItem(PageNames[i], new NavOpts
                    {
                        Icon = PageIcons[i],
                        Selected = Page == i,
                        Group = "pages",
                    }))
                    {
                        Page = i;
                        _drawer = false;
                        ui.ScrollTo("main", 0);
                    }
                }
            }

            using (ui.Column(new Box { Gap = 10, Pad = 8 }))
            {
                // The theme is the app's state only in the sense that this control decides it:
                // CUI holds the current value, so assigning it is the whole update.
                int mode = ui.Theme == Theme.Dark ? 1 : 0;
                if (ui.Segmented("theme", Modes, ref mode, new SegmentedOpts
                {
                    FullWidth = true,
                    Icons = ModeIcons,
                    Size = ControlSize.Sm,
                }))
                {
                    ui.Theme = mode == 1 ? Theme.Dark : Theme.Light;
                }

                ui.Muted("Accent");
                using (ui.Row(new Box { Gap = 4, Wrap = true }))
                {
                    for (int a = 0; a < AccentNames.Length; a++)
                    {
                        if (ui.Button(AccentNames[a], new ButtonOpts
                        {
                            Variant = ButtonVariant.Ghost,
                            Size = ControlSize.Sm,
                            Selected = (int)ui.Accent == a,
                        }))
                        {
                            ui.Accent = (Accent)a;
                        }
                    }
                }
            }
        }
    }

    private static void Section(Ui ui, string title, string? desc)
    {
        using (ui.Column(new Box { Gap = 4, PadTop = 18 }))
        {
            ui.Heading(title, 4);
            if (desc != null) ui.Secondary(desc);
        }
    }

    private void Dialogs(Ui ui)
    {
        using (var dlg = ui.Dialog("Edit profile", ref _dialogOpen,
                   new DialogOpts { Description = "Changes are visible to your team." }))
        {
            if (dlg.Open)
            {
                ui.Input("dlg-name", ref _name, new InputOpts { Label = "Name" });
                ui.Input("dlg-email", ref _email, new InputOpts { Label = "Email", Icon = Icons.Mail });
                ui.Switch("Public profile", ref _wifi);
                using (ui.DialogFooter())
                {
                    if (ui.Button("Cancel")) _dialogOpen = false;
                    if (ui.Button("Save changes", new ButtonOpts { Variant = ButtonVariant.Primary }))
                    {
                        _dialogOpen = false;
                        ui.Toast("Profile updated", new ToastOpts { Tone = Tone.Success });
                    }
                }
            }
        }

        using (var dlg = ui.Dialog("Delete this project?", ref _confirmOpen, new DialogOpts
        {
            Description = "This removes the project and its history for everyone. You cannot undo this.",
            Width = 400,
        }))
        {
            if (dlg.Open)
            {
                using (ui.DialogFooter())
                {
                    if (ui.Button("Cancel##confirm")) _confirmOpen = false;
                    if (ui.Button("Delete project", new ButtonOpts { Variant = ButtonVariant.Danger }))
                    {
                        _confirmOpen = false;
                        ui.Toast("Project deleted", new ToastOpts { Tone = Tone.Danger, Action = "Undo" });
                    }
                }
            }
        }

        using (var sheet = ui.Sheet("Notifications", ref _sheetOpen,
                   new SheetOpts { Description = "Choose what we tell you about." }))
        {
            if (sheet.Open)
            {
                ui.Switch("Comments", ref _newsletter, new CheckOpts { Description = "When someone replies to you." });
                ui.Switch("Mentions", ref _wifi, new CheckOpts { Description = "When someone mentions you." });
                ui.Switch("Weekly digest", ref _bluetooth, new CheckOpts { Description = "A summary every Monday." });
                ui.Separator();
                ui.Label("Recent");
                ui.ListItem("Jonas commented on Release 1.0", new ListItemOpts
                {
                    Avatar = "Jonas Berg",
                    Subtitle = "2 minutes ago",
                });
                ui.ListItem("Priya invited you to Engines", new ListItemOpts
                {
                    Avatar = "Priya Nair",
                    Subtitle = "1 hour ago",
                });
            }
        }

        int chosen = ui.CommandPalette(ref _paletteOpen, Commands);
        switch (chosen)
        {
            case 0: Page = 0; break;
            case 1: Page = 1; break;
            case 2: Page = 2; break;
            case 3: Page = 8; break;
            case 4: ui.ToggleTheme(); break;
            case 5: ui.Accent = (Accent)(((int)ui.Accent + 1) % AccentNames.Length); break;
            case 6: Page = 6; break;
            case 7: _dialogOpen = true; break;
            case 8: _sheetOpen = true; break;
        }
    }
}
