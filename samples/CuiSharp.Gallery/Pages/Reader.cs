// Page 19 of 28: a docs page with a scroll-spy table of contents.
// Part of the CuiSharp.Gallery sample: Reader
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    private static readonly TocItem[] ReaderToc =
    [
        new("Introduction", "intro"), new("Install", "install"),
        new("With CMake", "cmake", 1), new("Without CMake", "nocmake", 1),
        new("First window", "window"), new("Immediate mode", "imm"),
        new("Layout", "layout"), new("Next steps", "next"),
    ];

    private const string ReaderPara =
        "Components are functions. They draw themselves, handle input and return what happened. Your variables are the " +
        "source of truth, so there is nothing to keep in sync, and conditional interface is just an if statement. ";

    private void PageReader(Ui ui)
    {
        using (ui.Row(new Box { Gap = 28, Align = Align.Start, Width = Size.Fill }))
        {
            using (ui.Scroll(new ScrollOpts
            {
                Id = "reader",
                Height = Size.Px(560),
                Width = Size.Fill,
                Gap = 14,
                FadeEdges = true,
            }))
            {
                for (int k = 0; k < 8; k++)
                {
                    ui.TocAnchor(ReaderToc[k].Anchor);
                    ui.Heading(ReaderToc[k].Title, ReaderToc[k].Level != 0 ? 4 : 3);
                    for (int p = 0; p < 3; p++)
                    {
                        // Three identical paragraphs are three widgets: give each its own id.
                        using (ui.Id(k * 10 + p))
                        {
                            ui.Text(ReaderPara, new TextOpts { Color = Color.TextSecondary });
                        }
                    }
                }
                ui.Gap(200);
            }

            using (ui.Column(new Box { Width = Size.Px(200), Gap = 10 }))
            {
                ui.Text("On this page", new TextOpts
                {
                    Size = 12,
                    Font = Font.Medium,
                    Color = Color.TextMuted,
                });
                ui.Toc("reader", ReaderToc);
            }
        }
    }
}
