// Page 1 of 9: metrics, a form card and a team card.
// Part of the CuiSharp.Gallery sample: Overview
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    private void PageOverview(Ui ui)
    {
        using (ui.Column(new Box { Gap = 14, PadBottom = 8 }))
        {
            ui.Heading("Components for dotnet", 1);
            ui.Text(
                "Forms, tables, charts, planning boards and chat pieces that draw themselves the same way on " +
                "Windows, macOS and Linux. Every one works from the keyboard.",
                new TextOpts { Color = Color.TextSecondary, Size = 15 });
            using (ui.Row(new Box { Gap = 8, PadTop = 6, Wrap = true }))
            {
                if (ui.Button("Browse components", new ButtonOpts
                {
                    Variant = ButtonVariant.Primary,
                    IconRight = Icons.ArrowRight,
                })) Page = 1;
                if (ui.Button("Open command palette", new ButtonOpts
                {
                    Variant = ButtonVariant.Outline,
                    Icon = Icons.Command,
                })) _paletteOpen = true;
            }
        }

        using (ui.Box(new Box { Dir = Dir.Grid, Columns = 3, MinColumnWidth = 200, Gap = 16 }))
        {
            ui.Metric("Components", "150", new MetricOpts { Icon = Icons.Box });
            ui.Metric("Frame time", "0.4 ms", new MetricOpts
            {
                Delta = -18f,
                DeltaLabel = "-18% vs v0.9",
                LowerIsBetter = true,
                Spark = Months.Spark,
                Icon = Icons.Zap,
            });
            ui.Metric("Dependencies", "0", new MetricOpts { Icon = Icons.Package });
        }

        using (ui.Box(new Box { Dir = Dir.Grid, Columns = 2, MinColumnWidth = 260, Gap = 16 }))
        {
            using (ui.Card(new Card { Gap = 16 }))
            {
                using (ui.Column(new Box { Gap = 4 }))
                {
                    ui.Heading("Create a workspace", 4);
                    ui.Secondary("You can invite people once it exists.");
                }
                ui.Input("ov-name", ref _name, new InputOpts { Label = "Name", Placeholder = "Your name" });
                ui.Input("ov-email", ref _email, new InputOpts
                {
                    Label = "Email",
                    Icon = Icons.Mail,
                    Placeholder = "you@company.com",
                });
                ui.Checkbox("Email me a weekly summary", ref _terms);
                if (ui.Button("Create workspace", new ButtonOpts
                {
                    Variant = ButtonVariant.Primary,
                    FullWidth = true,
                }))
                {
                    ui.Toast("Workspace created", new ToastOpts
                    {
                        Description = "It is empty for now. Start with a project.",
                        Tone = Tone.Success,
                    });
                }
            }

            using (ui.Card(new Card { Gap = 12 }))
            {
                using (ui.Row())
                {
                    using (ui.Column(new Box { Gap = 4, Width = Size.Fill }))
                    {
                        ui.Heading("Team", 4);
                        ui.Secondary("Four people, one owner.");
                    }
                    ui.Button("Invite", new ButtonOpts { Icon = Icons.UserPlus, Size = ControlSize.Sm });
                }

                for (int i = 0; i < 4; i++)
                {
                    using (ui.Row(new Box { Gap = 12, PadY = 4 }))
                    {
                        ui.Avatar(TeamNames[i], new AvatarOpts
                        {
                            Size = 34,
                            Status = i == 0 ? "online" : i == 1 ? "away" : null,
                        });
                        using (ui.Column(new Box { Width = Size.Fill, Gap = 1 }))
                        {
                            ui.Text(TeamNames[i], new TextOpts { Font = Font.Medium, NoWrap = true });
                            ui.Text(TeamMails[i], new TextOpts
                            {
                                Size = 12,
                                Color = Color.TextSecondary,
                                NoWrap = true,
                            });
                        }
                        ui.Badge(TeamRoles[i], new BadgeOpts { Tone = i == 0 ? Tone.Accent : Tone.Neutral });
                    }
                }
            }
        }
    }
}
