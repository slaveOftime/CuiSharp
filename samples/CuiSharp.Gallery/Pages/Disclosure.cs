// Page 18 of 28: banner, hover card, confirm popover, expandable text and
// cards, countdown, onboarding checklist and an undo toast.
// Part of the CuiSharp.Gallery sample: Disclosure
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    private const string AboutCui =
        "CUI is a component library for C. It draws everything itself, so an app looks the same on Windows, macOS and Linux, " +
        "and it ships the parts most apps need: inputs, pickers, menus, dialogs, tables, charts and a command palette. Every " +
        "component supports the keyboard, light and dark themes and reduced motion. The core is plain C99 with no " +
        "dependencies, and it renders through OpenGL or a built-in software rasterizer, so it also runs in virtual machines " +
        "and over remote desktop.";

    private bool _bannerOpen = true;
    private bool _billingOpen = true, _securityOpen;
    private int _branchesDeleted;
    private double _launchTarget;
    private CheckStep[] _onboarding =
    [
        new("Create your workspace", "Pick a name and invite your team.", "Create", true),
        new("Connect a repository", "Link GitHub or GitLab to sync issues and pull requests.", "Connect", false),
        new("Import your data", "Bring tasks over from a CSV or another tool.", "Import", false),
        new("Turn on notifications", "Get pinged when something needs you.", "Enable", false),
    ];

    private void PageDisclosure(Ui ui)
    {
        if (ui.Banner("CUI 1.1 is out: data grid, kanban and forty new components.", ref _bannerOpen, new BannerOpts
        {
            Action = "Read the notes",
            Dismissible = true,
        }))
        {
            ui.Toast("Opening release notes");
        }
        if (!_bannerOpen && ui.Button("Show the banner again", new ButtonOpts { Size = ControlSize.Sm })) _bannerOpen = true;

        Section(ui, "Hover card", "Rest the pointer on a name.");
        using (ui.Row(new Box { Gap = 6, Wrap = true }))
        {
            ui.Text("Assigned to");
            ui.Link("@jonas");
            using (var card = ui.HoverCard("grace-card", new HoverCardOpts { Width = 300 }))
            {
                if (card.Open)
                {
                    using (ui.Row(new Box { Gap = 12, Align = Align.Start }))
                    {
                        ui.Avatar("Jonas Berg", new AvatarOpts { Size = 44, Status = "online" });
                        using (ui.Column(new Box { Gap = 4, Width = Size.Fill }))
                        {
                            ui.Text("Jonas Berg", new TextOpts { Font = Font.Medium });
                            ui.Text("Engineer on the rendering team. Joined March 2024.", new TextOpts
                            {
                                Size = 13,
                                Color = Color.TextSecondary,
                            });
                            using (ui.Row(new Box { Gap = 6, PadTop = 4, Wrap = true }))
                            {
                                ui.Icon(Icons.Calendar, 12, Color.TextMuted);
                                ui.Text("Usually replies within an hour", new TextOpts
                                {
                                    Size = 12,
                                    Color = Color.TextMuted,
                                });
                            }
                        }
                    }
                }
            }
            ui.Text("and due Friday.");
        }

        Section(ui, "Confirm popover", "Ask right where the action is.");
        using (ui.Row(new Box { Gap = 12, Wrap = true }))
        {
            if (ui.Button("Delete branch", new ButtonOpts { Icon = Icons.Trash2 })) ui.PopoverOpen("confirm-delete");
            if (ui.ConfirmPopover("confirm-delete", new ConfirmPopoverOpts
            {
                Title = "Delete feature/grid?",
                Description = "The branch and its 12 commits are removed from the remote.",
                ConfirmLabel = "Delete branch",
                Danger = true,
            }))
            {
                _branchesDeleted++;
            }
            if (_branchesDeleted > 0) ui.Muted("Branch deleted");
        }

        Section(ui, "Expandable text", "Long text clamps to a few lines with a fade.");
        ui.ExpandableText("about", AboutCui, new ExpandableTextOpts { Lines = 2 });

        Section(ui, "Expandable cards", "A header that opens a body with a spring.");
        using (ui.Column(new Box { Gap = 10 }))
        {
            using (var card = ui.ExpandableCard("Billing", ref _billingOpen, new ExpandableCardOpts
            {
                Description = "Pro plan, renews on December 1",
                Icon = Icons.CreditCard,
                Badge = "Active",
                BadgeTone = Tone.Success,
            }))
            {
                if (card.Open)
                {
                    // Plain label and value pairs: a card inside the card would only add edges.
                    using (ui.Row(new Box { Gap = 40, Wrap = true }))
                    {
                        using (ui.Column(new Box { Gap = 2 }))
                        {
                            ui.Muted("This month");
                            ui.Text("$84.00", new TextOpts { Font = Font.Display, Size = 22, Tabular = true });
                        }
                        using (ui.Column(new Box { Gap = 2 }))
                        {
                            ui.Muted("Seats");
                            ui.Text("7 of 10", new TextOpts { Font = Font.Display, Size = 22, Tabular = true });
                        }
                    }
                    using (ui.Row(new Box { Gap = 8, Wrap = true }))
                    {
                        ui.Button("Change plan", new ButtonOpts { Size = ControlSize.Sm });
                        ui.Button("Invoices", new ButtonOpts { Size = ControlSize.Sm, Variant = ButtonVariant.Ghost });
                    }
                }
            }
            using (var card = ui.ExpandableCard("Security", ref _securityOpen, new ExpandableCardOpts
            {
                Description = "Two-factor authentication is on",
                Icon = Icons.Shield,
            }))
            {
                if (card.Open)
                {
                    ui.Text("Recovery codes were generated 3 months ago.", new TextOpts
                    {
                        Color = Color.TextSecondary,
                    });
                }
            }
        }

        Section(ui, "Countdown", "Digits roll as the time passes.");
        if (_launchTarget == 0) _launchTarget = ui.Time + 2 * 86400 + 5 * 3600 + 42 * 60 + 18;
        using (ui.Row(new Box { Gap = 24, Align = Align.Center }))
        {
            ui.Countdown("launch", _launchTarget - ui.Time);
            ui.Countdown("sale", _launchTarget - ui.Time - 86400 * 2, new CountdownOpts
            {
                Compact = true,
                HideDays = true,
            });
        }

        Section(ui, "Onboarding checklist", "Progress, with the next step open.");
        using (ui.Box(new Box { Width = Size.Px(460) }))
        {
            int a = ui.Checklist("onboarding", _onboarding, new ChecklistOpts { Title = "Set up your workspace" });
            if (a >= 0)
            {
                var step = _onboarding[a];
                step.Done = true;
                _onboarding[a] = step;
                ui.Toast(step.Title, new ToastOpts { Tone = Tone.Success });
            }
        }

        Section(ui, "Undo toast", "A draining line shows how long Undo stays.");
        if (ui.Button("Archive 3 conversations", new ButtonOpts { Icon = Icons.Archive }))
        {
            ui.Toast("3 conversations archived", new ToastOpts { Action = "Undo", ShowTimer = true });
        }
    }
}
