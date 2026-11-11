// Page 22 of 28: chat, comments, presence, a counter, a ticker, a carousel,
// an image viewer, a cropper, a before/after compare and a card stack.
// Part of the CuiSharp.Gallery sample: Social and media
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    private static readonly PresenceUser[] PresenceWho =
    [
        new("Maya Okafor", Color.Rgb(0x7c5cff)), new("Jonas Berg", Color.Rgb(0x22c58b)),
        new("Priya Nair", Color.Rgb(0xf59e0b)), new("Lena Fischer", Color.Rgb(0x38bd24), Idle: true),
        new("Aiko Tanaka", Color.Rgb(0xec4899)), new("Daniel Mensah", Color.Rgb(0x10b981)),
        new("Rosa Delgado", Color.Rgb(0xa855f7)),
    ];

    private static readonly TickerItem[] TickerItems =
    [
        new("Revenue", "$48.2k", 6.9f), new("Active users", "12,408", 3.1f),
        new("Churn", "1.7%", -0.6f, true), new("Latency", "182 ms", -8.4f, true),
        new("Builds", "1,204", 12.0f), new("Uptime", "99.98%"),
    ];

    private static readonly string[] CarouselTitles =
        ["Faster exports", "ARM64 runners", "Shortcuts everywhere", "Smaller downloads"];

    private static readonly string[] CarouselDescs =
    [
        "A 900 file project now exports in 40 seconds, down from four minutes.",
        "Linux and macOS jobs can run on ARM64 machines in every plan.",
        "Every action in the editor has a key, listed under Help, Shortcuts.",
        "The desktop app is 38 MB lighter after dropping the bundled browser.",
    ];

    private static readonly uint[] CarouselTops = [0x1f2a37, 0x0f3d3e, 0x3a2e22, 0x2b2f3a];

    private static readonly string[] DeckNames = ["Kyoto", "Lisbon", "Reykjavik", "Oaxaca", "Hanoi"];
    private static readonly uint[] DeckColors = [0xc8553d, 0x2d3047, 0x5b8e7d, 0xb7791f, 0x1f3a5f];

    private readonly int[] _commentLikes = [12, 3, 0];
    private readonly bool[] _commentLiked = [true, false, false];
    private int _slide = 1;
    private double _sceneRevenue = 48210;
    private int _deck;

    // The two scene textures. They belong to the render context, so the page owns
    // them only while it is on screen: the shell hands the pair back when the page
    // leaves (that is where C uses cui_state with an on-free), and they are made
    // again on the way back.
    private ulong _sceneBefore, _sceneAfter;
    private ImageView _viewerState;
    private RectF _cropRect;
    private float _compareSplit = 0.45f;
    private DrawFn? _cardStackBody;

    private void PageSocial(Ui ui)
    {
        EnsureScene(ui);

        using (ui.Row(new Box { Gap = 24, Align = Align.Start, Width = Size.Fill }))
        {
            using (ui.Column(new Box { Width = Size.Grow(1), Gap = 10 }))
            {
                Section(ui, "Chat", "Grouped bubbles, read receipts and a typing indicator.");
                using (ui.Card(new Card { Gap = 0, Pad = 16 }))
                {
                    ui.ChatDivider("Today");
                    ui.ChatMessage("Did the data grid make it into 1.1?", new ChatOpts
                    {
                        Author = "Jonas Berg",
                        Time = "9:41",
                    });
                    ui.ChatMessage("I need it for the billing page.", new ChatOpts
                    {
                        Author = "Jonas Berg",
                        Grouped = true,
                        LastInGroup = true,
                    });
                    ui.ChatMessage("It did. 2,000 rows scroll at 60 fps on my laptop.", new ChatOpts
                    {
                        Mine = true,
                    });
                    ui.ChatMessage("Shift and Ctrl clicks select like a spreadsheet.", new ChatOpts
                    {
                        Mine = true,
                        Grouped = true,
                        LastInGroup = true,
                        Status = "Read",
                        Time = "9:43",
                    });
                    ui.TypingIndicator("Jonas");
                }
            }

            using (ui.Column(new Box { Width = Size.Grow(1), Gap = 10 }))
            {
                Section(ui, "Comments", "Threads with replies, likes and badges.");
                using (ui.Card(new Card { Gap = 4, Pad = 14 }))
                {
                    ui.Comment("c1", "Tomas Rivera",
                        "The new numbers match the staging run. I would still test the import on a large file before Friday.",
                        ref _commentLikes[0], ref _commentLiked[0],
                        new CommentOpts { Time = "2h", Badge = "Author" });
                    ui.Comment("c2", "Priya Nair", "Agreed. I have a 2 GB export we can use.",
                        ref _commentLikes[1], ref _commentLiked[1],
                        new CommentOpts { Time = "1h", Depth = 1 });
                    ui.Comment("c3", "Maya Okafor", "Scheduled it for 3 pm.",
                        ref _commentLikes[2], ref _commentLiked[2],
                        new CommentOpts { Time = "40m", Depth = 1, Edited = true });
                }
            }
        }

        Section(ui, "Presence", "Who is here now. Hover to spread them out.");
        using (ui.Row(new Box { Gap = 24, Align = Align.Center }))
        {
            ui.Presence("here", PresenceWho, 5, 30);
            ui.Counter("rev", _sceneRevenue, new CounterOpts { Format = "$%.0f", Size = 34, GroupThousands = true });
            if (ui.Button("Add a sale", new ButtonOpts { Size = ControlSize.Sm, Icon = Icons.Plus })) _sceneRevenue += 1287;
        }

        Section(ui, "Ticker", "A continuous strip of figures. It pauses under the pointer.");
        ui.Ticker("tick", TickerItems);

        Section(ui, "Carousel", "Drag, flick, scroll sideways or use the arrows and dots.");
        using (var tour = ui.Carousel("tour", 4, ref _slide, new CarouselOpts { Height = 200, AutoAdvance = 6 }))
        {
            if (tour.Open)
            {
                for (int k = 0; k < 4; k++)
                {
                    using (ui.CarouselSlide())
                    {
                        using (ui.Box(new Box
                        {
                            Width = Size.Fill,
                            Height = Size.Fill,
                            Bg = Color.Rgb(CarouselTops[k]),
                            Radius = 16,
                            Pad = 28,
                            Gap = 8,
                            Justify = Justify.End,
                        }))
                        {
                            ui.Text(CarouselTitles[k], new TextOpts
                            {
                                Font = Font.Display,
                                Size = 24,
                                Color = Color.Rgb(0xffffff),
                            });
                            ui.Text(CarouselDescs[k], new TextOpts { Color = Color.Rgb(0xffffff).WithAlpha(0.75f) });
                        }
                    }
                }
            }
        }

        using (ui.Row(new Box { Gap = 24, Align = Align.Start, Width = Size.Fill, Wrap = true }))
        {
            using (ui.Column(new Box { Width = Size.Grow(3), MinWidth = 280, Gap = 10 }))
            {
                Section(ui, "Image viewer", "Wheel to zoom around the pointer, drag to pan, double-click for 100%.");
                ui.ImageViewer("viewer", _sceneAfter, 640, 400, ref _viewerState, new ImageViewerOpts
                {
                    Height = Size.Px(260),
                });
            }
            using (ui.Column(new Box { Width = Size.Grow(2), MinWidth = 240, Gap = 10 }))
            {
                Section(ui, "Cropper", "Drag the corners; the square keeps its shape.");
                ui.Cropper("cropper", _sceneAfter, 640, 400, ref _cropRect, new CropperOpts
                {
                    Aspect = 1,
                    Height = Size.Px(260),
                });
            }
        }
        using (ui.Row(new Box { Gap = 24, Align = Align.Start, Width = Size.Fill }))
        {
            using (ui.Column(new Box { Width = Size.Grow(1), Gap = 10 }))
            {
                Section(ui, "Before and after", "Drag the divider.");
                ui.ImageCompare("cmp", _sceneBefore, _sceneAfter, ref _compareSplit, new CompareOpts
                {
                    Height = 230,
                    Aspect = 640.0f / 400.0f,
                });
            }
            using (ui.Column(new Box { Width = Size.Grow(1), Gap = 10, Align = Align.Center }))
            {
                Section(ui, "Card stack", "Flick the top card away, left or right.");
                int dir = ui.CardStack("deck", 3, _cardStackBody ??= CardStackBody, new CardStackOpts
                {
                    Width = Size.Px(260),
                    Height = 200,
                });
                if (dir != 0)
                {
                    _deck = (_deck + 1) % 5;
                    ui.Toast(dir > 0 ? "Saved" : "Skipped");
                }
            }
        }
    }

    // The deck reads one page field, so the DrawFn is a fixed method cached in a
    // field rather than a closure per frame.
    private void CardStackBody(Ui ui)
    {
        for (int k = 0; k < 3; k++)
        {
            int idx = (_deck + k) % 5;
            using (ui.CardStackItem(k))
            {
                using (ui.Box(new Box
                {
                    Width = Size.Fill,
                    Height = Size.Fill,
                    Bg = Color.Rgb(DeckColors[idx]),
                    Radius = 25,
                    Pad = 20,
                    Justify = Justify.End,
                    Gap = 4,
                }))
                {
                    ui.Text(DeckNames[idx], new TextOpts
                    {
                        Font = Font.Display,
                        Size = 22,
                        Color = Color.Rgb(0xffffff),
                    });
                    ui.Text("Swipe right to save", new TextOpts
                    {
                        Size = 13,
                        Font = Font.Medium,
                        Color = Color.Rgb(0xffffff).WithAlpha(0.94f),
                    });
                }
            }
        }
    }

    private void EnsureScene(Ui ui)
    {
        if (_sceneAfter != 0) return;
        var before = new byte[640 * 400 * 4];
        var after = new byte[640 * 400 * 4];
        MakeScene(before, 640, 400, color: false);
        MakeScene(after, 640, 400, color: true);
        _sceneBefore = ui.CreateImage(640, 400, before);
        _sceneAfter = ui.CreateImage(640, 400, after);
    }

    /// <summary>
    /// Releases the scene pair; the shell calls it while the page is off screen, so
    /// the 2 MB of textures does not outlive the page that shows it.
    /// </summary>
    private void ReleaseSceneTextures(Ui ui)
    {
        if (_sceneBefore == 0 && _sceneAfter == 0) return;
        ui.DestroyImage(_sceneBefore);
        ui.DestroyImage(_sceneAfter);
        _sceneBefore = _sceneAfter = 0;
    }

    /// <summary>The painted sky the compare and viewer widgets show.</summary>
    private static void MakeScene(byte[] px, int w, int h, bool color)
    {
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float fx = (float)x / w, fy = (float)y / h;
                float r = 0.56f + fy * 0.34f, g = 0.72f + fy * 0.2f, b = 0.88f + fy * 0.06f; // clear daytime sky
                float sun = (fx - 0.72f) * (fx - 0.72f) * 15.4f + (fy - 0.24f) * (fy - 0.24f) * 6.0f; // round at a 1.6 aspect
                if (sun < 0.024f)
                {
                    r = 1.0f; g = 0.97f; b = 0.86f;
                }

                float m1 = 0.5f + 0.1f * MathF.Sin(fx * 6.0f + 0.6f) + 0.03f * MathF.Sin(fx * 23.0f);
                float m2 = 0.68f + 0.06f * MathF.Sin(fx * 4.0f + 2.1f) + 0.02f * MathF.Sin(fx * 31.0f);
                if (fy > m1)
                {
                    r = 0.5f; g = 0.6f; b = 0.62f - fy * 0.08f; // far hills in haze
                }

                if (fy > m2)
                {
                    r = 0.3f - (fy - m2) * 0.2f; g = 0.45f - (fy - m2) * 0.25f; b = 0.3f; // near fields
                }

                if (!color)
                {
                    float l = r * 0.3f + g * 0.59f + b * 0.11f;
                    l = (l * 0.85f) + 0.05f;
                    r = g = b = l;
                }

                int p = (y * w + x) * 4;
                px[p] = (byte)(Math.Clamp(r, 0f, 1f) * 255f);
                px[p + 1] = (byte)(Math.Clamp(g, 0f, 1f) * 255f);
                px[p + 2] = (byte)(Math.Clamp(b, 0f, 1f) * 255f);
                px[p + 3] = 255;
            }
        }
    }
}
