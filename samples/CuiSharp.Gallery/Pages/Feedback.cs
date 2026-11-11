// Page 7 of 28: alerts, progress, loading states, badges, avatars.
// Part of the CuiSharp.Gallery sample: Feedback
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    private void PageFeedback(Ui ui)
    {
        Section(ui, "Alerts", "Color only when something succeeded, needs attention or failed.");
        using (ui.Column(new Box { Gap = 10 }))
        {
            ui.Alert("A new version is available", new AlertOpts { Description = "Restart to update to 1.1." });
            ui.Alert("Payment received", new AlertOpts
            {
                Description = "Your invoice was paid on November 5.",
                Tone = Tone.Success,
            });
            ui.Alert("Storage almost full", new AlertOpts
            {
                Description = "You have used 92% of your plan.",
                Tone = Tone.Warning,
                Dismissible = true,
            });
            ui.Alert("Build failed", new AlertOpts
            {
                Description = "Missing symbol cui_frame in main.c line 42.",
                Tone = Tone.Danger,
            });
        }

        using (ui.Box(new Box { Dir = Dir.Grid, Columns = 2, MinColumnWidth = 260, Gap = 16 }))
        {
            using (ui.Card(new Card { Gap = 18 }))
            {
                ui.Heading("Progress", 4);
                ui.Progress(0.68f, new ProgressOpts { Label = "Uploading assets", ShowValue = true });
                ui.Progress(0f, new ProgressOpts { Label = "Indexing", Indeterminate = true });
                ui.Meter("Storage", 18.4f, 20f, "GB");
                using (ui.Row(new Box { Gap = 16, Wrap = true }))
                {
                    ui.Spinner(16);
                    ui.Spinner(22);
                    ui.Spinner(28);
                }
            }

            using (ui.Card(new Card { Gap = 14 }))
            {
                ui.Heading("Loading", 4);
                using (ui.Row(new Box { Gap = 12, Wrap = true }))
                {
                    ui.Skeleton(Size.Px(40), 40);
                    using (ui.Column(new Box { Gap = 8, Width = Size.Fill }))
                    {
                        ui.Skeleton(Size.Percent(70), 12);
                        ui.Skeleton(Size.Percent(45), 12);
                    }
                }
                ui.Skeleton(Size.Fill, 88);
                ui.Skeleton(Size.Percent(90), 12);
                ui.Skeleton(Size.Percent(60), 12);
            }
        }

        using (ui.Box(new Box { Dir = Dir.Grid, Columns = 2, MinColumnWidth = 260, Gap = 16 }))
        {
            using (ui.Card(new Card { Gap = 14 }))
            {
                ui.Heading("Activity", 4);
                ui.TimelineItem("Deployed to production", new TimelineOpts
                {
                    Time = "2m ago",
                    Description = "Release 1.0.3 is live in all regions.",
                    Icon = Icons.Rocket,
                    Tone = Tone.Success,
                });
                ui.TimelineItem("Review requested", new TimelineOpts
                {
                    Time = "1h ago",
                    Description = "Jonas asked you to review the theming changes.",
                    Icon = Icons.MessageSquare,
                    Tone = Tone.Info,
                });
                ui.TimelineItem("Build failed", new TimelineOpts
                {
                    Time = "3h ago",
                    Icon = Icons.CircleAlert,
                    Tone = Tone.Danger,
                    Last = true,
                });
            }

            using (ui.Card(new Card { Gap = 14 }))
            {
                ui.Heading("Goals", 4);
                using (ui.Row(new Box { Gap = 24, Justify = Justify.Around }))
                {
                    ui.ProgressRing("Onboarding", 0.72f, 104);
                    ui.ProgressRing("Quota", 0.35f, 104);
                }
            }
        }

        Section(ui, "Badges and avatars", null);
        using (ui.Card(new Card { Gap = 16 }))
        {
            using (ui.Row(new Box { Gap = 8, Wrap = true }))
            {
                ui.Badge("Draft");
                ui.Badge("Recommended", new BadgeOpts { Tone = Tone.Accent });
                ui.Badge("Active", new BadgeOpts { Tone = Tone.Success, Dot = true });
                ui.Badge("Pending", new BadgeOpts { Tone = Tone.Warning, Dot = true });
                ui.Badge("Overdue", new BadgeOpts { Tone = Tone.Danger, Dot = true });
                ui.Badge("Beta", new BadgeOpts { Tone = Tone.Violet });
                ui.Badge("New", new BadgeOpts { Tone = Tone.Info, Solid = true });
            }
            using (ui.Row(new Box { Gap = 16, Wrap = true }))
            {
                ui.Avatar("Sam Whitfield", new AvatarOpts { Size = 40, Status = "online" });
                ui.Avatar("Daniel Mensah", new AvatarOpts { Size = 40, Status = "busy" });
                ui.Avatar("Rosa Delgado", new AvatarOpts { Size = 40, Square = true });
                ui.AvatarGroup(AvatarTeam, 4, 32);
            }
        }

        using (ui.Card(new Card { Pad = 0 }))
        {
            if (ui.EmptyState("No projects yet", new EmptyOpts
            {
                Icon = Icons.FolderOpen,
                Description = "A project holds your screens, their builds and who can see them.",
                Action = "New project",
            }))
            {
                ui.Toast("Project created", new ToastOpts { Tone = Tone.Success });
            }
        }
    }
}
