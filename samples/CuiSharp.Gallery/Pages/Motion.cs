// Page 25 of 28: text effects, gradient text and spotlight cards, loaders,
// a dock, control tiles, a now-playing card, a waveform and a tab bar.
// Part of the CuiSharp.Gallery sample: Motion
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    private static readonly string[] RotateWords = ["staging.", "eu-west.", "us-east.", "all regions."];

    private static readonly string[] SpotlightTitles = ["Software", "OpenGL 3.3", "Your own"];

    private static readonly string[] SpotlightBlurbs =
    [
        "Plain C into a pixel buffer. No GPU needed.",
        "The default on desktop. Deploying now.",
        "The core emits triangles; bring any GPU API.",
    ];

    private static readonly TabBarItem[] PhoneTabs =
    [
        new(Icons.House, "Home"), new(Icons.Search, "Search"), new(Icons.Inbox, "Inbox", 4), new(Icons.User, "Profile"),
    ];

    private static readonly float[] WavePeaks = BuildPeaks();

    private int _replay;
    private bool _tileWifi = true, _tileHotspot, _tileFocus = true, _tileAirDrop, _tileDark = true;
    private int _phoneTab;
    private float _waveProgress = 0.36f;
    private NowPlayingState _nowPlaying = new(
        "Low Tide", "Marlow Avenue", ArtColor: Color.Rgb(0xc8553d), Position: 76, Duration: 222, Playing: true);

    private void PageMotion(Ui ui)
    {
        Section(ui, "Text effects", "A shimmer for pending work, a typewriter for streamed text, words that settle into place.");
        using (ui.Card(new Card { Gap = 18, Pad = 20, Width = Size.Fill }))
        {
            using (ui.Row(new Box { Gap = 10, Align = Align.Center }))
            {
                ui.Loader(new LoaderOpts { Style = LoaderStyle.Dots, Size = 22 });
                ui.ShimmerText("Thinking about your question…", new TextFxOpts { Size = 15 });
            }

            // The replay button moves the id scope, so the effects run again from zero.
            using (ui.Id(_replay))
            {
                ui.Typewriter("tw",
                    "CUI draws a full frame in a fraction of a millisecond, so text can stream in as fast as it arrives.",
                    new TextFxOpts { Caret = true, Size = 15 });
                ui.TextReveal("rv", "The release build finished in 3 minutes 42 seconds.", new TextFxOpts
                {
                    Size = 20,
                    Font = Font.Display,
                });
            }
            using (ui.Row(new Box { Gap = 8, Align = Align.Center }))
            {
                ui.Text("Deploying to", new TextOpts { Font = Font.Display, Size = 26 });
                ui.WordRotate("rot", RotateWords, 2.2f, new TextFxOpts { Font = Font.Display, Size = 26 });
            }
            if (ui.Button("Replay", new ButtonOpts { Size = ControlSize.Sm, Icon = Icons.RotateCcw, Variant = ButtonVariant.Outline })) _replay++;
        }

        Section(ui, "Gradient text and spotlight cards", "A light follows the pointer across a card and along its edge. A beam marks the one card that is live.");
        ui.GradientText("Kiln 2.4", new GradientTextOpts { Font = Font.Display, Size = 34, Animate = true });
        using (ui.Row(new Box { Gap = 14, Align = Align.Stretch, Width = Size.Fill }))
        {
            for (int k = 0; k < 3; k++)
            {
                using (ui.Id(k))
                using (ui.Spotlight("spot", new SpotlightOpts { Width = Size.Grow(1), Beam = k == 1 }))
                {
                    ui.Text(SpotlightTitles[k], new TextOpts { Font = Font.Medium, Size = 15 });
                    ui.Text(SpotlightBlurbs[k], new TextOpts { Size = 13, Color = Color.TextSecondary });
                }
            }
        }

        Section(ui, "Loaders", "Dots, bars, pulse, orbit and grid.");
        using (ui.Row(new Box { Gap = 36, Align = Align.Center }))
        {
            for (int k = 0; k < 5; k++)
            {
                ui.Loader(new LoaderOpts { Style = (LoaderStyle)k, Size = 28 });
            }
        }

        Section(ui, "Dock", "Icons grow as the pointer passes.");
        using (ui.Row(new Box
        {
            Width = Size.Fill,
            Justify = Justify.Center,
            Height = Size.Px(110),
            Align = Align.End,
        }))
        {
            using (ui.Dock("dock"))
            {
                if (ui.DockItem(Icons.Folder, "Files", new DockItemOpts { Color = Color.Rgb(0x3b82f6), Running = true })) ui.Toast("Files");
                ui.DockItem(Icons.Mail, "Mail", new DockItemOpts { Color = Color.Rgb(0x0ea5e9), Badge = 3, Running = true });
                ui.DockItem(Icons.Calendar, "Calendar", new DockItemOpts { Color = Color.Rgb(0xef4444) });
                ui.DockItem(Icons.Music, "Music", new DockItemOpts { Color = Color.Rgb(0xec4899), Running = true });
                ui.DockItem(Icons.SquareTerminal, "Terminal", new DockItemOpts { Color = Color.Rgb(0x27272a) });
                ui.DockItem(Icons.Palette, "Design", new DockItemOpts { Color = Color.Rgb(0x8b5cf6) });
                ui.DockSeparator();
                ui.DockItem(Icons.Settings, "Settings");
                ui.DockItem(Icons.Trash2, "Trash");
            }
        }

        using (ui.Row(new Box { Gap = 28, Align = Align.Start, Width = Size.Fill }))
        {
            using (ui.Column(new Box { Width = Size.Grow(1), Gap = 10 }))
            {
                Section(ui, "Control tiles", "Big toggles for quick settings.");
                using (ui.Box(new Box { Dir = Dir.Grid, Columns = 2, Gap = 10, Width = Size.Px(300) }))
                {
                    ui.ControlTile("Wi-Fi", ref _tileWifi, new TileOpts { Icon = Icons.Wifi, Detail = _tileWifi ? "Home" : "Off" });
                    ui.ControlTile("Hotspot", ref _tileHotspot, new TileOpts { Icon = Icons.Radio });
                    ui.ControlTile("Focus", ref _tileFocus, new TileOpts { Icon = Icons.Moon, Detail = _tileFocus ? "Until 6 pm" : "Off" });
                    ui.ControlTile("AirShare", ref _tileAirDrop, new TileOpts { Icon = Icons.Share2, Detail = _tileAirDrop ? "Everyone" : "Contacts only" });
                }
                ui.ControlTile("Dark appearance", ref _tileDark, new TileOpts { Icon = Icons.Sun, Wide = true, Width = Size.Px(300) });
            }

            using (ui.Column(new Box { Width = Size.Grow(1), Gap = 10 }))
            {
                Section(ui, "Now playing", "Artwork, a scrubber and transport.");
                PlayerEvents ev = ui.NowPlaying("np", ref _nowPlaying);
                if ((ev & PlayerEvents.Next) != 0) ui.Toast("Next track");
                if (_nowPlaying.Playing)
                {
                    // The scrubber only moves if the page keeps asking for frames.
                    _nowPlaying.Position += ui.DeltaTime;
                    if (_nowPlaying.Position > _nowPlaying.Duration) _nowPlaying.Position = 0;
                    ui.RequestRedraw();
                }

                Section(ui, "Waveform", "Click or drag to seek.");
                using (ui.Card(new Card { Pad = 14, Gap = 8, Width = Size.Px(360) }))
                {
                    using (ui.Row(new Box { Gap = 12, Align = Align.Center, Width = Size.Fill }))
                    {
                        ui.IconButton(Icons.Play, new ButtonOpts { Size = ControlSize.Sm, Variant = ButtonVariant.Secondary });
                        using (ui.Box(new Box { Width = Size.Fill }))
                        {
                            ui.Waveform("wave", WavePeaks, ref _waveProgress, new WaveformOpts { Height = 34 });
                        }
                        ui.Text("0:42", new TextOpts { Size = 12, Tabular = true, Color = Color.TextMuted });
                    }
                }

                Section(ui, "Tab bar", "For compact layouts.");
                ui.TabBar("tabs", PhoneTabs, ref _phoneTab, new TabBarOpts { Width = Size.Px(360) });
            }
        }
    }

    private static float[] BuildPeaks()
    {
        var peaks = new float[96];
        for (int k = 0; k < 96; k++) peaks[k] = 0.18f + (0.8f * MathF.Abs(MathF.Sin(k * 0.37f) * MathF.Sin((k * 0.11f) + 1.3f)));
        return peaks;
    }
}
