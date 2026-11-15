#:package CuiSharp@*

// The properties below are what make `dotnet publish music.cs -c Release -o .`
// leave one small native executable beside this file, with no .NET on the target.
// The Windows target is what brings in the MediaPlayer this app plays through.
#:property TargetFramework=net10.0-windows10.0.19041.0
#:property PublishAot=true
#:property OptimizationPreference=Size
#:property InvariantGlobalization=true
#:property StripSymbols=true
#:property DebugType=none
#:property OutputType=WinExe
#:property ApplicationIcon=music.ico
#:property EventSourceSupport=false
#:property HttpActivityPropagationSupport=false
#:property MetadataUpdaterSupport=false
#:property UseSystemResourceKeys=true
#:property StackTraceSupport=false
#:property MetricsSupport=false

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CuiSharp;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

// ---------------------------------------------------------------------------
// Music — a native CuiSharp player over the folders you point it at.
//
//   run      dotnet run music.cs                        the window
//   ship     dotnet publish music.cs -c Release -o .    one music.exe, no runtime needed
//            (add -p:CuiSharpHeadless=true to publish a build `dotnet cui` can drive)
//
//   iterate  CUISHARP_HEADLESS=1 ./music.exe            no window; serves the tool's protocol
//            dotnet cui tree                            the controls the last frame drew
//            dotnet cui click Play                      click one, print the frame it produced
//            dotnet cui png shot.png                    that frame as a PNG
//            dotnet cui docs read live-debugging        the guide, offline
//            dotnet cui api MenuOpts                    one exact declaration
//            dotnet cui session < steps.txt             many clicks against one app instance
//
// Below: the window, its panels, the player, the scan, the spectrum behind the
// effects, and the state they share.
// ---------------------------------------------------------------------------

var preferences = Store.LoadPreferences();
var library = new Library(preferences.Folders);
using var player = new Player(preferences);
var app = new MusicApp(library, player, preferences);

var exitCode = CuiApp.Run(app.Options, app.Draw);
Store.Save(preferences, player, library);
return exitCode;

// ---------------------------------------------------------------------------
// The window. Three shapes: the library and the player side by side, the player
// above the library, or a single compact strip/stack once the window gets small.
// ---------------------------------------------------------------------------

internal sealed class MusicApp
{
    private const float SideBySideWidth = 860f;
    private const float MiniWidth = 680f;
    private const float MiniHeight = 280f;
    private const float MiniListHeight = 440f;

    // Below this the side-by-side card drops its tag line so the visualizer
    // keeps its room instead of being squeezed out of the layout.
    private const float TightHeight = 440f;
    private const float CompactPlayerShare = 0.28f;
    private const float CompactPlayerMin = 172f;
    private const float CompactPlayerMax = 216f;
    private const float LibraryMinimum = 180f;

    private readonly Library _library;
    private readonly Player _player;
    private readonly Preferences _preferences;

    private readonly NowPlayingPanel _nowPlaying;
    private readonly LibraryPanel _libraryPanel;
    private readonly FolderPicker _picker = new();

    private Track? _savedTrack;
    private int _libraryCount = -1;
    private bool _fonts;

    public MusicApp(Library library, Player player, Preferences preferences)
    {
        _library = library;
        _player = player;
        _preferences = preferences;

        _nowPlaying = new NowPlayingPanel(player, preferences.Visualizer, Save, new ArtCache());
        _libraryPanel = new LibraryPanel(library, player, AddFolder, RemoveFolder);

        Options = new CuiAppOptions
        {
            Title = "Music",
            Width = 1240,
            Height = 840,

            // Below this the strip has nothing left to give; the stack takes over
            // above it, so the window can still shrink to a small player.
            MinWidth = 380,
            MinHeight = 200,

            Theme = Theme.Dark,
            Accent = Accent.Violet,
            Renderer = Renderer.Software,
        };

        _player.Restore(library.Find(preferences.TrackPath), preferences.Position);
    }

    public CuiAppOptions Options { get; }

    public void Draw(Ui ui)
    {
        if (!_fonts)
        {
            // Half a music library is tagged in a script the bundled faces do not carry.
            ui.UseSystemFontFallback("中文");
            _fonts = true;
        }

        _player.Pump();
        _player.SetQueue(_library.Queue);
        _nowPlaying.Advance(ui.DeltaTime, _player.IsPlaying);
        Resolve();

        var (width, height) = ui.ViewportSize();

        // A narrow window still has room for the library when it is tall enough
        // to give the player a card and keep a list under it.
        if (height < MiniHeight || (width < MiniWidth && height < MiniListHeight))
            DrawMini(ui, width, height);
        else if (width < SideBySideWidth) DrawStacked(ui, width, height);
        else DrawSideBySide(ui, width, height);

        _picker.Draw(ui, _library);
        Shortcuts(ui);
        Persist();

        // Forty frames a second while the visualizer moves or a track is still
        // being decoded for the seek bar, one while it rests.
        ui.RequestWakeup(_player.IsPlaying || _nowPlaying.IsReading ? 1f / 40f : 1f);
    }

    private void DrawSideBySide(Ui ui, float width, float height)
    {
        var playerWidth = Math.Clamp(width * 0.38f, 340f, 520f);

        using (ui.Column(new Box { Pad = 16, Gap = 10, Width = Size.Fill, Height = Size.Fill }))
        {
            using (ui.Row(new Box { Gap = 12, Width = Size.Fill, Height = Size.Fill, Align = Align.Stretch }))
            {
                _libraryPanel.Draw(ui, Size.Fill, Size.Fill, width - playerWidth - 44f, compact: false);
                _nowPlaying.DrawTall(ui, playerWidth, Size.Fill, tight: height < TightHeight);
            }

            Footer(ui);
        }
    }

    private void DrawStacked(Ui ui, float width, float height)
    {
        // The player takes its share of the height, but never the library's last rows.
        var content = height - 34f;
        var playerHeight = MathF.Min(
            Math.Clamp(content * CompactPlayerShare, CompactPlayerMin, CompactPlayerMax),
            MathF.Max(CompactPlayerMin, content - LibraryMinimum));

        using (ui.Column(new Box { Pad = 12, Gap = 10, Width = Size.Fill, Height = Size.Fill }))
        {
            _nowPlaying.DrawWide(ui, playerHeight);
            _libraryPanel.Draw(ui, Size.Fill, Size.Fill, width - 24f, compact: true);
        }
    }

    private void DrawMini(Ui ui, float width, float height) =>
        _nowPlaying.DrawMini(ui, width, height);

    private void Footer(Ui ui)
    {
        var tracks = _library.Count;
        var folders = _library.Folders.Count;

        using (ui.Row(new Box { Align = Align.Center, Gap = 10 }))
        {
            ui.Muted(Summary(tracks, folders, _library.TotalDuration));
            ui.Spacer();
            ui.Muted(_library.Status);
        }
    }

    private static string Summary(int tracks, int folders, double duration)
    {
        var trackCount = $"{Format.Count(tracks)} {(tracks == 1 ? "track" : "tracks")}";
        var folderCount = $"{folders} {(folders == 1 ? "folder" : "folders")}";

        return $"{trackCount} · {folderCount} · {Format.Time(duration)}";
    }

    private void Shortcuts(Ui ui)
    {
        if (ui.WantsKeyboard) return;

        if (ui.Shortcut((Mods)0, Key.Space)) _player.Toggle();
        if (ui.Shortcut((Mods)0, Key.Right)) _player.Seek(_player.Position + 5);
        if (ui.Shortcut((Mods)0, Key.Left)) _player.Seek(_player.Position - 5);
    }

    /// <summary>
    /// On a cold start the cached library may not hold the saved track yet, so
    /// the saved position is tried again whenever the scan brings more in.
    /// </summary>
    private void Resolve()
    {
        if (_player.Current is not null || _preferences.TrackPath.Length == 0) return;
        if (_library.Count == _libraryCount) return;

        _libraryCount = _library.Count;

        if (_library.Find(_preferences.TrackPath) is { } track)
            _player.Restore(track, _preferences.Position);
    }

    private void Persist()
    {
        if (!ReferenceEquals(_player.Current, _savedTrack))
        {
            _savedTrack = _player.Current;
            Save();
        }

        if (_player.IsPlaying) Store.MaybeSave(_preferences, _player, _library);
    }

    private void AddFolder() => _picker.Request();

    private void RemoveFolder(string folder)
    {
        _library.RemoveFolder(folder);
        Save();
    }

    private void Save() => Store.Save(_preferences, _player, _library);
}

// ---------------------------------------------------------------------------
// The player card: the visualizer, what is playing, and the transport.
// ---------------------------------------------------------------------------

internal sealed class NowPlayingPanel(Player player, int visualizer, Action save, ArtCache art)
{
    private const float TimeWidth = 44f;

    private readonly Visualizer _visualizer = new(visualizer);
    private readonly SeekBar _seek = new(player);

    private Sound? _sound;
    private string _soundPath = "";
    private Task<Sound?>? _reading;

    private string _positionText = "0:00";
    private int _lastSecond = -1;

    public bool IsReading => _reading is not null;

    public void Advance(float delta, bool playing)
    {
        var sound = Listening();

        _visualizer.Advance(sound, player.Position, player.Duration, delta, playing);
        _seek.Sound = sound;
    }

    /// <summary>The decoded track, once the background read has one to hand.</summary>
    private Sound? Listening()
    {
        if (player.Current is not { } track)
        {
            _sound = null;
            _soundPath = "";
            _reading = null;
            return null;
        }

        if (track.Path != _soundPath)
        {
            _soundPath = track.Path;
            _sound = null;
            _reading = Task.Run(() => Sound.LoadAsync(track.Path, track.Duration));
        }

        if (_reading is { IsCompleted: true } reading)
        {
            _reading = null;
            _sound = reading.Result;
        }

        return _sound;
    }

    public void DrawTall(Ui ui, float width, Size height, bool tight)
    {
        Show(ui);

        using (ui.Card(new Card
        {
            Width = Size.Px(width),
            Height = height,
            Pad = 14,
            Gap = 10,
            Dir = Dir.Column,
        }))
        {
            // The visualizer takes every point left over, so a tall window never
            // leaves a dead band below the transport.
            DrawVisualizer(ui, Size.Fill, Size.Fill);

            Title(ui, size: 18);

            // A short window gives up the tag line before it gives up the visualizer.
            if (!tight) Meta(ui);

            Progress(ui, Size.Fill, 26f);
            Transport(ui, mini: false);
        }
    }

    /// <summary>A card wider than it is tall: the cover sits beside the text, not above it.</summary>
    public void DrawWide(Ui ui, float height)
    {
        Show(ui);

        var art = Math.Clamp(height - 62f, 96f, 168f);

        using (ui.Card(new Card
        {
            Width = Size.Fill,
            Height = Size.Px(height),
            Pad = 12,
            Gap = 10,
            Dir = Dir.Column,
        }))
        {
            using (ui.Row(new Box { Width = Size.Fill, Height = Size.Fill, Gap = 14, Align = Align.Stretch }))
            {
                DrawVisualizer(ui, Size.Px(art), Size.Px(art));

                // The tags sit at the top and the seek bar at the bottom of the
                // cover, so the column reads as one block instead of floating.
                using (ui.Column(new Box
                {
                    Width = Size.Fill,
                    Height = Size.Fill,
                    MinWidth = 0,
                    Gap = 4,
                    Justify = Justify.Between,
                }))
                {
                    using (ui.Column(new Box { Gap = 2 }))
                    {
                        Title(ui, size: 16);
                        if (height >= 178f) Meta(ui);
                    }

                    Progress(ui, Size.Fill, 22f);
                }
            }

            Transport(ui, mini: false);
        }
    }

    public void DrawMini(Ui ui, float width, float height)
    {
        Show(ui);

        var pad = width < 520f ? 12f : 16f;
        var panelWidth = MathF.Min(width - 18f, 520f);
        var gap = height > 360f ? 8f : 5f;

        using (ui.Box(new Box
        {
            Dir = Dir.Stack,
            Width = Size.Px(width),
            Height = Size.Px(height),
            Align = Align.Center,
            Justify = Justify.Center,
            Bg = Color.Background,
        }))
        {
            Background(ui, width, height);

            using (ui.Box(new Box
            {
                Width = Size.Px(panelWidth),
                Pad = pad,
                Gap = gap,
                Dir = Dir.Column,

                // Glass: the blurred cover reads through a cool tint, with a hairline
                // that catches the light and a shadow to lift it off the artwork.
                // The tint thickens with the music, so the card breathes with it.
                Bg = Color.Oklch(0.26f, 0.02f, _visualizer.Hue).WithAlpha(0.36f + _visualizer.Level * 0.10f),
                Border = 1,
                BorderColor = new Color(255, 255, 255, 44),
                Radius = 18,
                Elevation = Elevation.Raised,
            }))
            {
                ui.Text(player.Current?.Title ?? "Nothing playing", new TextOpts
                {
                    Font = Font.Medium,
                    Size = 16,
                    NoWrap = true,
                    Width = Size.Fill,
                    Align = TextAlign.Center,
                });

                ui.Text(Subtitle(), new TextOpts
                {
                    Size = 11,
                    NoWrap = true,
                    Width = Size.Fill,
                    Align = TextAlign.Center,
                    Color = player.Error is null ? Color.TextSecondary : Color.Danger,
                });

                if (height >= 260f) Meta(ui, center: true);

                Progress(ui, Size.Fill, 18f);

                Transport(ui, mini: true);
            }
        }
    }

    /// <summary>The effect at full size, dimmed under the glass panel.</summary>
    private void Background(Ui ui, float width, float height)
    {
        using (ui.Box(new Box
        {
            Dir = Dir.Stack,
            Width = Size.Fill,
            Height = Size.Fill,
            Opacity = 0.60f,
        }))
        {
            if (_visualizer.Mode == Visualizer.GridIndex)
                _visualizer.DrawHeat(ui, "viz-bg-heat", width, height);
            else
                _visualizer.Draw(ui, "viz-bg", Size.Fill, Size.Fill, background: true);
        }
    }

    /// <summary>Points the visualizer and the seek bar at the track being played.</summary>
    private void Show(Ui ui)
    {
        var track = player.Current;

        _visualizer.Hue = track?.Hue ?? 268f;
        _seek.Hue = _visualizer.Hue;

        var cover = art.Get(ui, track);
        _visualizer.Art = cover.Texture;
        _visualizer.ArtWidth = cover.Width;
        _visualizer.ArtHeight = cover.Height;
        _visualizer.Frost = cover.Frost;
    }

    private void DrawVisualizer(Ui ui, Size width, Size height) =>
        _visualizer.Draw(ui, "viz", width, height);

    private void Title(Ui ui, float size)
    {
        ui.Text(player.Current?.Title ?? "Nothing playing", new TextOpts
        {
            Font = Font.Medium,
            Size = size,
            NoWrap = true,
        });

        ui.Text(Subtitle(), new TextOpts
        {
            Size = 12,
            Color = player.Error is null ? Color.TextSecondary : Color.Danger,
            NoWrap = true,
        });
    }

    private void Meta(Ui ui, bool center = false)
    {
        if (player.Current?.Meta is not { Length: > 0 } meta) return;

        ui.Text(meta, new TextOpts
        {
            Size = 11,
            Color = Color.TextMuted,
            NoWrap = true,
            Width = center ? Size.Fill : default,
            Align = center ? TextAlign.Center : TextAlign.Start,
        });
    }

    private string Subtitle()
    {
        var track = player.Current;

        if (track is null) return player.Error ?? "Pick a folder, then a track.";

        // A failed open replaces the credit line rather than trailing it:
        // "A-Lin · 失恋无罪 · Can't play this file" reads like a tag.
        return player.Error ?? Format.Credit(track);
    }

    private void Progress(Ui ui, Size width, float height)
    {
        _seek.Draw(ui, "seek", width, height);

        var duration = Math.Max(1, player.Duration);
        var second = (int)player.Position;

        if (second != _lastSecond)
        {
            _lastSecond = second;
            _positionText = Format.Time(second);
        }

        using (ui.Row(new Box { Gap = 8, Align = Align.Center, Width = width }))
        {
            ui.Text(_positionText, new TextOpts
            {
                Size = 10,
                Tabular = true,
                Color = Color.TextMuted,
                Width = Size.Px(TimeWidth),
            });
            ui.Spacer();
            ui.Text(Format.Time(duration), new TextOpts
            {
                Size = 10,
                Tabular = true,
                Color = Color.TextMuted,
                Width = Size.Px(TimeWidth),
                Align = TextAlign.Right,
            });
        }
    }

    private void Transport(Ui ui, bool mini)
    {
        if (mini)
        {
            using (ui.Row(new Box
            {
                Align = Align.Center,
                Justify = Justify.Center,
                Gap = 4,
                Width = Size.Fill,
            }))
            {
                Buttons(ui);
                EffectMenu(ui);
                Mute(ui);
            }

            return;
        }

        using (ui.Row(new Box { Align = Align.Center, Gap = 4, Width = Size.Fill }))
        {
            Buttons(ui);
            ui.Spacer();
            EffectMenu(ui);
            Volume(ui);
        }
    }

    /// <summary>Shuffle, previous, play/pause, next, repeat — all icon buttons.</summary>
    private void Buttons(Ui ui)
    {
        if (ui.IconButton(Icons.Shuffle, new ButtonOpts
        {
            Tooltip = "Shuffle",
            Size = ControlSize.Sm,
            Selected = player.Shuffle,
        }))
        {
            player.Shuffle = !player.Shuffle;
            save();
        }

        if (ui.IconButton(Icons.SkipBack, new ButtonOpts { Tooltip = "Previous", Size = ControlSize.Sm }))
            player.Previous();

        if (ui.IconButton(player.IsPlaying ? Icons.Pause : Icons.Play, new ButtonOpts
        {
            Tooltip = player.IsPlaying ? "Pause" : "Play",
            Size = ControlSize.Sm,
            Variant = ButtonVariant.Primary,
        }))
        {
            player.Toggle();
        }

        if (ui.IconButton(Icons.SkipForward, new ButtonOpts { Tooltip = "Next", Size = ControlSize.Sm }))
            player.Next();

        if (ui.IconButton(Icons.Repeat, new ButtonOpts
        {
            Tooltip = player.Repeat switch { 1 => "Repeat all", 2 => "Repeat one", _ => "Repeat off" },
            Size = ControlSize.Sm,
            Selected = player.Repeat != 0,
        }))
        {
            player.Repeat = (player.Repeat + 1) % 3;
            save();
        }
    }

    /// <summary>The effect picker as an icon button: the active effect is its tooltip.</summary>
    private void EffectMenu(Ui ui)
    {
        using var menu = ui.Menu(Visualizer.Names[_visualizer.Mode], new MenuOpts
        {
            Icon = Icons.Sparkles,
            IconOnly = true,
            Variant = ButtonVariant.Ghost,
        });

        if (!menu) return;

        for (var i = 0; i < Visualizer.Names.Length; i++)
        {
            if (ui.MenuItem(Visualizer.Names[i], new MenuItemOpts { Checked = i == _visualizer.Mode }))
            {
                _visualizer.Mode = i;
                save();
            }
        }
    }

    private void Volume(Ui ui)
    {
        Mute(ui);

        var slider = player.Volume;
        if (ui.Slider("##volume", ref slider, 0f, 1f, new SliderOpts { Width = Size.Px(76) }))
        {
            player.Volume = slider;
            save();
        }
    }

    private void Mute(Ui ui)
    {
        var volume = player.Volume;

        if (ui.IconButton(volume <= 0.001f ? Icons.VolumeX : Icons.Volume2, new ButtonOpts
        {
            Tooltip = volume <= 0.001f ? "Unmute" : "Mute",
            Size = ControlSize.Sm,
        }))
        {
            player.Volume = volume <= 0.001f ? 0.7f : 0f;
            save();
        }
    }
}

// ---------------------------------------------------------------------------
// The library: search, the folders it reads, and the tracks themselves.
// ---------------------------------------------------------------------------

internal sealed class LibraryPanel(Library library, Player player, Action add, Action<string> remove)
{
    private const float RowHeight = 56f;
    private const float SearchMin = 136f;
    private const float SearchMax = 240f;
    private const float SearchCompactMax = 196f;

    private string _query = "";

    public void Draw(Ui ui, Size width, Size height, float panelWidth, bool compact)
    {
        var inlineFolder = library.Folders.Count == 1;

        using (ui.Card(new Card { Width = width, Height = height, Pad = 12, Gap = 8, Dir = Dir.Column }))
        {
            Header(ui, compact, panelWidth, inlineFolder);
            if (!inlineFolder) Chips(ui);
            ui.Separator();
            Tracks(ui);
        }
    }

    private void Header(Ui ui, bool compact, float panelWidth, bool inlineFolder)
    {
        // A single folder pill sits on the header row itself; reserve room for it
        // so the search box never forces a second line.
        var reserved = inlineFolder ? 220f : 140f;
        var searchWidth = Math.Clamp(panelWidth - reserved, SearchMin, compact ? SearchCompactMax : SearchMax);

        using (ui.Row(new Box { Align = Align.Center, Gap = 6 }))
        {
            ui.Icon(Icons.Music, 14, Color.Accent);

            var badge = library.IsScanning
                ? $"{library.Done}/{library.Total}"
                : panelWidth >= 480f
                    ? $"{Format.Count(library.Count)} tracks"
                    : Format.Count(library.Count);

            ui.Badge(badge, new BadgeOpts { Tone = Tone.Neutral, Size = ControlSize.Sm });

            if (inlineFolder)
                FolderPill(ui, library.Folders[0]);

            if (ui.IconButton(Icons.Plus, new ButtonOpts
            {
                Tooltip = "Add folder",
                Variant = ButtonVariant.Secondary,
                Size = ControlSize.Sm,
            }))
            {
                add();
            }

            ui.Spacer();

            ui.Search("search", ref _query, new InputOpts
            {
                Placeholder = "Title, artist, album",
                Icon = Icons.Search,
                Clearable = true,
                Size = ControlSize.Sm,
                Width = Size.Px(searchWidth),
            });
        }

        if (_query != library.Query) library.Filter(_query);
    }

    private void Chips(Ui ui)
    {
        if (library.Folders.Count == 0) return;

        using (ui.Row(new Box { Gap = 6, Wrap = true, Align = Align.Center }))
        {
            foreach (var folder in library.Folders)
                if (FolderPill(ui, folder))
                    return;
        }
    }

    private bool FolderPill(Ui ui, string folder)
    {
        using (ui.Box(new Box
        {
            Dir = Dir.Row,
            Align = Align.Center,
            PadLeft = 8,
            PadRight = 2,
            Gap = 2,
            Bg = Color.SurfaceMuted,
            Radius = 6,
            MinWidth = 0,
            MaxWidth = 220,
        }))
        {
            ui.Text(FolderLabel(folder), new TextOpts
            {
                Size = 11,
                Color = Color.TextSecondary,
                NoWrap = true,
            });

            if (ui.IconButton(Icons.X, new ButtonOpts
            {
                Tooltip = $"Remove {folder}",
                Size = ControlSize.Sm,
                Variant = ButtonVariant.Ghost,
            }))
            {
                remove(folder);
                return true;
            }
        }

        return false;
    }

    private string FolderLabel(string folder)
    {
        var leaf = Path.GetFileName(folder);
        if (leaf.Length == 0) return folder;

        foreach (var other in library.Folders)
        {
            if (!string.Equals(other, folder, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFileName(other), leaf, StringComparison.OrdinalIgnoreCase))
            {
                var parent = Path.GetFileName(Path.GetDirectoryName(folder) ?? "");
                return parent.Length > 0 ? $"{parent}\\{leaf}" : folder;
            }
        }

        return leaf;
    }

    private void Tracks(Ui ui)
    {
        var queue = library.Queue;

        if (queue.Length == 0)
        {
            Empty(ui);
            return;
        }

        using (ui.VirtualList("tracks", queue.Length, RowHeight, Size.Fill, out var first, out var last))
        {
            for (var i = first; i < last && i < queue.Length; i++)
                Row(ui, queue[i]);
        }
    }

    private void Row(Ui ui, Track track)
    {
        var current = player.Current is { } playing && playing.Path == track.Path;

        var clicked = ui.ListItem(track.Title, new ListItemOpts
        {
            Subtitle = Format.Credit(track),
            Trailing = Format.Length(track.Duration),
            Selected = current,
            Icon = current ? Icons.AudioLines : null,
        });

        if (clicked) player.Play(track);
    }

    private void Empty(Ui ui)
    {
        var hasFolders = library.Folders.Count > 0;

        using (ui.Box(new Box { Height = Size.Fill, Align = Align.Center, Justify = Justify.Center }))
        {
            var requested = ui.EmptyState(hasFolders ? "Nothing matches" : "No music yet", new EmptyOpts
            {
                Description = hasFolders
                    ? "No track here has that title, artist or album."
                    : "Point it at a folder: every audio file under it lands here, tags and all.",
                Icon = Icons.Music,
                Action = hasFolders ? null : "Add folder",
            });

            if (requested) add();
        }
    }
}

// ---------------------------------------------------------------------------
// The folder dialog, drawn by CUI inside the frame rather than as an OS window.
// ---------------------------------------------------------------------------

internal sealed class FolderPicker
{
    private bool _open;
    private string _path = "";

    public void Request() => _open = true;

    public void Draw(Ui ui, Library library)
    {
        if (!_open) return;

        var picked = ui.FilePicker("folder-picker", ref _open, ref _path, 1024, new FilePickerOpts
        {
            Folders = true,
            Title = "Add a music folder",
            Start = library.Folders.Count > 0 ? library.Folders[^1] : null,
        });

        if (picked && _path.Length > 0) library.AddFolder(_path);
    }
}

// ---------------------------------------------------------------------------
// Playback. MediaPlayer does the decoding; this owns the queue, the transport
// and the state the UI reads, and never blocks a frame.
// ---------------------------------------------------------------------------

internal sealed class Player : IDisposable
{
    private readonly MediaPlayer _media = new();
    private readonly Random _random = new();

    private Track[] _queue = [];
    private int _index = -1;
    private double _position;
    private double _duration;
    private double _pendingSeek;
    private bool _loaded;
    private bool _wasPlaying;
    private volatile bool _ended;
    private volatile string? _error;

    public Player(Preferences preferences)
    {
        _media.AutoPlay = false;
        _media.Volume = preferences.Volume;
        _media.MediaEnded += (_, _) => _ended = true;
        _media.MediaFailed += (_, args) => _error = Explain(args.Error);

        Shuffle = preferences.Shuffle;
        Repeat = preferences.Repeat;
    }

    /// <summary>Called once, after the frame loop has returned.</summary>
    public void Dispose() => _media.Dispose();

    public Track? Current { get; private set; }
    public bool IsPlaying => _media.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
    public double Position => _position;
    public double Duration => _duration > 0 ? _duration : Current?.Duration ?? 0;
    public string? Error => _error;
    public bool Shuffle { get; set; }
    public int Repeat { get; set; }

    public float Volume
    {
        get => (float)_media.Volume;
        set => _media.Volume = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>Reads the session once per frame, so drawing never touches COM.</summary>
    public void Pump()
    {
        // Nothing is open yet: the restored position is what the UI shows.
        if (!_loaded) return;

        var session = _media.PlaybackSession;

        _position = session.Position.TotalSeconds;

        var natural = session.NaturalDuration.TotalSeconds;
        if (natural > 0) _duration = natural;

        if (_pendingSeek > 0 && _duration > 0)
        {
            session.Position = TimeSpan.FromSeconds(Math.Clamp(_pendingSeek, 0, _duration - 0.25));
            _position = session.Position.TotalSeconds;
            _pendingSeek = 0;
        }

        var playing = session.PlaybackState == MediaPlaybackState.Playing;

        if (_ended || Finished(playing))
        {
            _ended = false;
            Advance();
        }

        _wasPlaying = playing;
    }

    public void SetQueue(Track[] queue)
    {
        if (ReferenceEquals(queue, _queue)) return;

        _queue = queue;
        _index = Current is null ? -1 : IndexOf(Current);
    }

    public void Play(Track track, double startAt = 0)
    {
        Current = track;
        _index = IndexOf(track);
        _position = startAt;
        _duration = track.Duration;
        _pendingSeek = startAt;
        _loaded = true;
        _error = null;

        _media.Source = MediaSource.CreateFromUri(new Uri(track.Path));
        _media.Play();
    }

    public void Toggle()
    {
        if (Current is null)
        {
            if (_queue.Length > 0) Play(_queue[0]);
            return;
        }

        if (!_loaded)
        {
            // Picking up where the last session stopped.
            Play(Current, _position);
            return;
        }

        if (IsPlaying) _media.Pause();
        else _media.Play();
    }

    private void Stop()
    {
        _media.Pause();
        Seek(0);
    }

    public void Next(bool manual = true)
    {
        var next = NextIndex(manual);
        if (next >= 0 && next < _queue.Length) Play(_queue[next]);
    }

    public void Previous()
    {
        if (_position > 3)
        {
            Seek(0);
            return;
        }

        if (_queue.Length == 0) return;

        var previous = _index - 1;
        Play(_queue[previous < 0 ? _queue.Length - 1 : previous]);
    }

    public void Seek(double seconds)
    {
        var limit = Duration;

        if (limit <= 0)
        {
            _position = Math.Max(0, seconds);
            _pendingSeek = _position;
            return;
        }

        _position = Math.Clamp(seconds, 0, limit - 0.25);

        if (!_loaded)
        {
            _pendingSeek = _position;
            return;
        }

        _media.PlaybackSession.Position = TimeSpan.FromSeconds(_position);
    }

    /// <summary>Shows the last session's track and position without opening a file yet.</summary>
    public void Restore(Track? track, double position)
    {
        Current = track;
        _position = position;
        _pendingSeek = position;
        _index = track is null ? -1 : IndexOf(track);
    }

    private bool Finished(bool playing) => _wasPlaying && !playing && _duration > 0 && _position >= _duration - 0.6;

    /// <summary>The enum names mean nothing in the UI; say what went wrong instead.</summary>
    private static string Explain(MediaPlayerError error) => error switch
    {
        MediaPlayerError.NetworkError => "Can't reach this file",
        MediaPlayerError.DecodingError => "Can't decode this file",
        MediaPlayerError.Aborted => "Playback was interrupted",
        _ => "Can't play this file",
    };

    private void Advance()
    {
        if (Current is null) return;

        if (Repeat == 2)
        {
            Seek(0);
            _media.Play();
            return;
        }

        var next = NextIndex(manual: false);
        if (next >= 0 && next < _queue.Length) Play(_queue[next]);
        else Stop();
    }

    private int NextIndex(bool manual)
    {
        if (_queue.Length == 0) return -1;

        if (Shuffle)
        {
            if (_queue.Length == 1) return 0;

            int pick;
            do pick = _random.Next(_queue.Length);
            while (pick == _index);

            return pick;
        }

        var next = _index + 1;
        if (next < _queue.Length) return next;

        return manual || Repeat != 0 ? 0 : -1;
    }

    private int IndexOf(Track track)
    {
        for (var i = 0; i < _queue.Length; i++)
            if (string.Equals(_queue[i].Path, track.Path, StringComparison.OrdinalIgnoreCase))
                return i;

        return -1;
    }
}

// ---------------------------------------------------------------------------
// The library: a folder list, a scan that keeps the last one's answers, and
// the filtered view the UI draws.
// ---------------------------------------------------------------------------

internal sealed class Library
{
    private readonly List<string> _folders = [];

    private Track[] _all = [];
    private Track[] _queue = [];
    private volatile string _query = "";
    private volatile string _status = "";
    private volatile bool _scanning;
    private int _done;
    private int _total;
    private int _generation;
    private CancellationTokenSource? _cancel;

    public Library(IEnumerable<string> folders)
    {
        foreach (var folder in folders)
            if (Normalize(folder) is { } path && !_folders.Contains(path, StringComparer.OrdinalIgnoreCase))
                _folders.Add(path);

        _all = Store.LoadLibrary();
        Rebuild();

        if (_folders.Count > 0) Scan();
    }

    public IReadOnlyList<string> Folders => _folders;
    public Track[] Queue => Volatile.Read(ref _queue);
    public int Count => Volatile.Read(ref _all).Length;
    public bool IsScanning => _scanning;
    public int Done => Volatile.Read(ref _done);
    public int Total => Volatile.Read(ref _total);
    public string Status => _status;
    public string Query => _query;

    public double TotalDuration
    {
        get
        {
            var total = 0d;
            foreach (var track in Volatile.Read(ref _all)) total += track.Duration;
            return total;
        }
    }

    public Track? Find(string path)
    {
        if (path.Length == 0) return null;

        foreach (var track in Volatile.Read(ref _all))
            if (string.Equals(track.Path, path, StringComparison.OrdinalIgnoreCase))
                return track;

        return null;
    }

    public void AddFolder(string folder)
    {
        if (Normalize(folder) is not { } path) return;
        if (_folders.Contains(path, StringComparer.OrdinalIgnoreCase)) return;

        _folders.Add(path);
        Scan();
    }

    /// <summary>One spelling per folder, so a picked path and a saved one match.</summary>
    private static string? Normalize(string folder)
    {
        try
        {
            var full = Path.GetFullPath(folder);
            var root = Path.GetPathRoot(full) ?? "";

            return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar) : full;
        }
        catch
        {
            return null;
        }
    }

    public void RemoveFolder(string folder)
    {
        if (!_folders.Remove(folder)) return;

        Scan();
    }

    public void Filter(string query)
    {
        query = query.Trim();
        if (query == _query) return;

        _query = query;
        Rebuild();
    }

    private void Rebuild()
    {
        var all = Volatile.Read(ref _all);
        var query = _query;

        var next = query.Length == 0
            ? all
            : Array.FindAll(all, track => track.Search.Contains(query, StringComparison.OrdinalIgnoreCase));

        Volatile.Write(ref _queue, next);
    }

    private void Scan()
    {
        var folders = _folders.ToArray();
        var cached = Volatile.Read(ref _all);

        _cancel?.Cancel();
        var cancel = new CancellationTokenSource();
        _cancel = cancel;

        var generation = ++_generation;
        _scanning = true;
        _status = "reading tags…";
        Volatile.Write(ref _done, 0);
        Volatile.Write(ref _total, 0);

        _ = Task.Run(async () =>
        {
            try
            {
                var tracks = await Scanner.ScanAsync(folders, cached, (done, total) =>
                {
                    Volatile.Write(ref _done, done);
                    Volatile.Write(ref _total, total);
                }, cancel.Token);

                if (generation != _generation) return;

                Array.Sort(tracks, TrackOrder.Compare);
                Volatile.Write(ref _all, tracks);
                Store.SaveLibrary(tracks);
                Rebuild();

                _status = $"{Format.Count(tracks.Length)} tracks · scanned {DateTime.Now:HH:mm}";
            }
            catch (OperationCanceledException)
            {
                // Folders changed under the scan; the new one owns the list.
            }
            catch (Exception error)
            {
                if (generation == _generation) _status = error.Message;
            }
            finally
            {
                if (generation == _generation) _scanning = false;
            }
        });
    }
}

// ---------------------------------------------------------------------------
// The scan: every audio file under the folders, tags read six at a time, and
// files the last scan already measured answered from the cache.
// ---------------------------------------------------------------------------

internal static class Scanner
{
    private static readonly string[] Extensions =
        [".mp3", ".flac", ".wav", ".m4a", ".aac", ".wma", ".ogg", ".opus", ".aiff", ".aif"];

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = System.IO.FileAttributes.ReparsePoint,
        RecurseSubdirectories = true,
    };

    public static async Task<Track[]> ScanAsync(
        string[] folders, Track[] cached, Action<int, int> progress, CancellationToken token)
    {
        var known = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in cached) known[track.Path] = track;

        var files = new List<FileInfo>(known.Count);

        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;

            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", Options))
                if (IsAudio(file.Extension))
                    files.Add(file);
        }

        var tracks = new Track[files.Count];
        var done = 0;
        progress(0, files.Count);

        await Parallel.ForEachAsync(Enumerable.Range(0, files.Count), new ParallelOptions
        {
            MaxDegreeOfParallelism = 6,
            CancellationToken = token,
        }, async (index, cancellation) =>
        {
            tracks[index] = await ReadAsync(files[index], known, cancellation);
            progress(Interlocked.Increment(ref done), files.Count);
        });

        return tracks;
    }

    private static bool IsAudio(string extension)
    {
        foreach (var known in Extensions)
            if (string.Equals(known, extension, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    private static async Task<Track> ReadAsync(FileInfo file, Dictionary<string, Track> known, CancellationToken token)
    {
        var modified = file.LastWriteTimeUtc.Ticks;

        if (known.TryGetValue(file.FullName, out Track? cached)
            && cached.Size == file.Length
            && cached.Modified == modified)
            return cached;

        (var fileArtist, var fileTitle) = Split(file.Name);

        var title = fileTitle;
        var artist = fileArtist;
        var album = file.Directory?.Name ?? "";
        var duration = 0d;
        var details = default(Details);

        try
        {
            var storage = await StorageFile.GetFileFromPathAsync(file.FullName);
            var tags = await storage.Properties.GetMusicPropertiesAsync();

            if (tags.Title is { Length: > 0 } taggedTitle) title = taggedTitle.Trim();
            if (tags.Artist is { Length: > 0 } taggedArtist) artist = taggedArtist.Trim();
            if (tags.Album is { Length: > 0 } taggedAlbum) album = taggedAlbum.Trim();

            duration = tags.Duration.TotalSeconds;
            details = new Details(
                tags.AlbumArtist?.Trim() ?? "",
                First(tags.Genre),
                tags.Year,
                tags.TrackNumber,
                tags.Bitrate);
        }
        catch
        {
            // A file the property system refuses still plays; its name says enough.
        }

        token.ThrowIfCancellationRequested();
        return Track.Create(file.FullName, title, artist, album, duration, file.Length, modified, details);
    }

    private static string First(IList<string> values)
    {
        foreach (var value in values)
            if (value is { Length: > 0 })
                return value.Trim();

        return "";
    }

    /// <summary>"Artist - Title.mp3" splits on the first dash; anything else is a title.</summary>
    private static (string Artist, string Title) Split(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var dash = name.IndexOf(" - ", StringComparison.Ordinal);

        return dash > 0
            ? (name[..dash].Trim(), name[(dash + 3)..].Trim())
            : ("", name.Trim());
    }
}

// ---------------------------------------------------------------------------
// Cover art. Windows' thumbnail service hands back a generic icon for these
// files, so the picture is read out of the file's own tags instead: an APIC
// frame in an MP3, a PICTURE block in a FLAC. One track's picture is on disk at
// a time, because the app only ever shows the one that is playing.
// ---------------------------------------------------------------------------

internal static class Artwork
{
    private const int Limit = 8 << 20;

    // A cover this small has no detail left; blown back up it is a soft wash of
    // the album's colours, which is the closest the renderer gets to a blur.
    private const int Frost = 48;

    private static readonly string Folder = Path.Combine(Store.Home, "art");

    private static readonly string Target = Path.Combine(Folder, "cover.img");

    /// <summary>Writes the track's embedded picture and returns its path, or null when it has none.</summary>
    public static string? Save(string trackPath)
    {
        var picture = Picture(trackPath);
        if (picture is null) return null;

        try
        {
            Directory.CreateDirectory(Folder);
            System.IO.File.WriteAllBytes(Target, picture);
            return Target;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The same picture at a few dozen pixels, for the glass behind the mini player.</summary>
    public static (int Width, int Height, byte[] Pixels)? Frosted(string path)
    {
        try
        {
            var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
            using var stream = file.OpenReadAsync().AsTask().GetAwaiter().GetResult();
            var decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();

            var transform = new BitmapTransform
            {
                ScaledWidth = Frost,
                ScaledHeight = Frost,
                InterpolationMode = BitmapInterpolationMode.Fant,
            };

            var pixels = decoder.GetPixelDataAsync(
                BitmapPixelFormat.Rgba8,
                BitmapAlphaMode.Ignore,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask().GetAwaiter().GetResult();

            return (Frost, Frost, pixels.DetachPixelData());
        }
        catch
        {
            // A picture CUI can load but WIC cannot still plays, just without glass.
            return null;
        }
    }

    private static byte[]? Picture(string path)
    {
        try
        {
            using var file = System.IO.File.OpenRead(path);

            Span<byte> head = stackalloc byte[10];
            if (file.Read(head) < 4) return null;

            if (head[0] == 'I' && head[1] == 'D' && head[2] == '3') return Id3(file, head);
            if (head[0] == 'f' && head[1] == 'L' && head[2] == 'a' && head[3] == 'C') return Flac(file);
        }
        catch
        {
            // An unreadable tag is the same as no picture.
        }

        return null;
    }

    private static byte[]? Id3(Stream file, ReadOnlySpan<byte> head)
    {
        var version = head[3];
        var size = SyncSafe(head[6..10]);

        if (size is <= 0 or > 64 << 20) return null;

        var tag = new byte[size];
        file.ReadExactly(tag);

        ReadOnlySpan<byte> span = tag;
        var at = 0;

        while (at + (version == 2 ? 6 : 10) <= span.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(span.Slice(at, version == 2 ? 3 : 4));
            if (id[0] == '\0') break;

            var length = version switch
            {
                2 => span[at + 3] << 16 | span[at + 4] << 8 | span[at + 5],
                4 => SyncSafe(span.Slice(at + 4, 4)),
                _ => span[at + 4] << 24 | span[at + 5] << 16 | span[at + 6] << 8 | span[at + 7],
            };

            var header = version == 2 ? 6 : 10;
            if (length <= 0 || at + header + length > span.Length) break;

            if (id is "APIC" or "PIC")
                return Frame(span.Slice(at + header, length), version);

            at += header + length;
        }

        return null;
    }

    /// <summary>encoding, mime, picture type, description, then the picture itself.</summary>
    private static byte[]? Frame(ReadOnlySpan<byte> frame, int version)
    {
        if (frame.Length < 5) return null;

        var at = 1;

        if (version == 2)
        {
            at += 3;
        }
        else
        {
            var mime = frame[at..].IndexOf((byte)0);
            if (mime < 0) return null;

            at += mime + 1;
        }

        at += 1;

        if (frame[0] is 1 or 2)
        {
            while (at + 1 < frame.Length && (frame[at] != 0 || frame[at + 1] != 0)) at += 2;

            at += 2;
        }
        else
        {
            var end = frame[at..].IndexOf((byte)0);
            if (end < 0) return null;

            at += end + 1;
        }

        if (at >= frame.Length || frame.Length - at > Limit) return null;

        return frame[at..].ToArray();
    }

    private static byte[]? Flac(Stream file)
    {
        Span<byte> header = stackalloc byte[4];

        while (file.Read(header) == 4)
        {
            var last = (header[0] & 0x80) != 0;
            var type = header[0] & 0x7F;
            var length = header[1] << 16 | header[2] << 8 | header[3];

            if (length < 0 || length > Limit) return null;

            if (type == 6)
            {
                var block = new byte[length];
                file.ReadExactly(block);

                return FlacPicture(block);
            }

            if (last) return null;

            file.Seek(length, SeekOrigin.Current);
        }

        return null;
    }

    /// <summary>type, mime, description, width, height, depth, colours, then the picture.</summary>
    private static byte[]? FlacPicture(ReadOnlySpan<byte> block)
    {
        var at = 4;

        if (at + 4 > block.Length) return null;

        var mime = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(block[at..]);
        at += 4 + mime;

        if (at + 4 > block.Length) return null;

        var description = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(block[at..]);
        at += 4 + description + 16;

        if (at + 4 > block.Length) return null;

        var length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(block[at..]);
        at += 4;

        if (length <= 0 || length > block.Length - at) return null;

        return block.Slice(at, length).ToArray();
    }

    private static int SyncSafe(ReadOnlySpan<byte> four) =>
        (four[0] & 0x7F) << 21 | (four[1] & 0x7F) << 14 | (four[2] & 0x7F) << 7 | (four[3] & 0x7F);
}

// ---------------------------------------------------------------------------
// The cover as a texture, kept for the handful of tracks played recently. A
// track with no picture is remembered as such, or every frame would re-read its
// tags looking for one.
// ---------------------------------------------------------------------------

internal sealed class ArtCache
{
    private const int Keep = 6;

    private readonly Dictionary<string, Cover> _covers = [with(StringComparer.OrdinalIgnoreCase)];
    private readonly HashSet<string> _empty = [with(StringComparer.OrdinalIgnoreCase)];
    private readonly Queue<string> _order = new();

    public Cover Get(Ui ui, Track? track)
    {
        if (track is null || _empty.Contains(track.Path)) return default;

        if (_covers.TryGetValue(track.Path, out var cached)) return cached;

        var file = Artwork.Save(track.Path);

        if (file is null || !ui.LoadImage(file, out var texture, out var width, out var height) || texture == 0)
        {
            _empty.Add(track.Path);
            return default;
        }

        _covers[track.Path] = new Cover(texture, width, height, Frost(ui, file));
        _order.Enqueue(track.Path);

        while (_order.Count > Keep)
        {
            var oldest = _order.Dequeue();

            if (_covers.Remove(oldest, out var cover))
            {
                ui.DestroyImage(cover.Texture);
                ui.DestroyImage(cover.Frost);
            }
        }

        return _covers[track.Path];
    }

    private static ulong Frost(Ui ui, string file) =>
        Artwork.Frosted(file) is { } frosted
            ? ui.CreateImage(frosted.Width, frosted.Height, frosted.Pixels)
            : 0;
}

internal readonly record struct Cover(ulong Texture, int Width, int Height, ulong Frost);

// ---------------------------------------------------------------------------
// Model.
// ---------------------------------------------------------------------------

/// <summary>What a file's tags add beyond the artist and album already shown.</summary>
internal readonly record struct Details(string AlbumArtist, string Genre, uint Year, uint TrackNumber, uint Bitrate);

/// <summary>
/// One playable file. The search text, the credit line and the hue all fall out
/// of the tags, so a track looks the same on every run.
/// </summary>
internal sealed record Track(
    string Path, string Title, string Artist, string Album,
    double Duration, long Size, long Modified, Details Details)
{
    public string Search { get; } =
        $"{Artist} {Title} {Album} {System.IO.Path.GetFileNameWithoutExtension(Path)}".ToLowerInvariant();

    public string Meta { get; } = Format.Meta(Details);

    public float Hue { get; } = Palette.HueOf(Path);

    public static Track Create(
        string path, string title, string artist, string album,
        double duration, long size, long modified, Details details = default) =>
        new(
            path,
            title.Length > 0 ? title : System.IO.Path.GetFileNameWithoutExtension(path),
            artist,
            album,
            duration,
            size,
            modified,
            details);
}

internal static class TrackOrder
{
    public static readonly Comparison<Track> Compare = static (a, b) =>
    {
        var byArtist = string.Compare(a.Artist, b.Artist, StringComparison.OrdinalIgnoreCase);
        return byArtist != 0 ? byArtist : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
    };
}

// ---------------------------------------------------------------------------
// What the last session left behind, on disk, so a restart continues.
// ---------------------------------------------------------------------------

internal sealed class Preferences
{
    public string[] Folders { get; set; } = [];
    public float Volume { get; set; } = 0.75f;
    public bool Shuffle { get; set; }
    public int Repeat { get; set; }
    public int Visualizer { get; set; }
    public string TrackPath { get; set; } = "";
    public double Position { get; set; }
}

internal static class Store
{
    private static readonly long SaveEvery = Stopwatch.Frequency * 5;

    private static readonly string Folder = StateFolder();

    private static readonly string StatePath = Path.Combine(Folder, "state.json");
    private static readonly string LibraryPath = Path.Combine(Folder, "library.json");

    /// <summary>The app-specific folder under the shared state root.</summary>
    public static string Home => Folder;

    private static string StateFolder()
    {
        string? state = Environment.GetEnvironmentVariable("CUISHARP_STATE_HOME");
        return state is { Length: > 0 }
            ? Path.Combine(state, "CuiMusic")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CuiMusic");
    }

    private static long _savedAt;

    public static Preferences LoadPreferences()
    {
        var preferences = new Preferences();

        try
        {
            if (!File.Exists(StatePath)) return preferences;

            using var json = JsonDocument.Parse(File.ReadAllBytes(StatePath));
            var root = json.RootElement;

            if (root.TryGetProperty("folders", out var folders) && folders.ValueKind == JsonValueKind.Array)
                preferences.Folders = [.. folders.EnumerateArray()
                    .Select(folder => folder.GetString())
                    .Where(folder => folder is { Length: > 0 })
                    .Select(folder => folder!)];

            preferences.Volume = ReadNumber(root, "volume", 0.75f);
            preferences.Shuffle = root.TryGetProperty("shuffle", out var shuffle) && shuffle.GetBoolean();
            preferences.Repeat = (int)ReadNumber(root, "repeat", 0);
            preferences.Visualizer = (int)ReadNumber(root, "visualizer", 0);
            preferences.TrackPath = root.TryGetProperty("track", out var track) ? track.GetString() ?? "" : "";
            preferences.Position = ReadNumber(root, "position", 0);
        }
        catch
        {
            // A state file from an older build is worth less than a clean start.
        }

        return preferences;
    }

    public static void MaybeSave(Preferences preferences, Player player, Library library)
    {
        var now = Stopwatch.GetTimestamp();
        if (now - _savedAt < SaveEvery) return;

        Save(preferences, player, library);
    }

    public static void Save(Preferences preferences, Player player, Library library)
    {
        _savedAt = Stopwatch.GetTimestamp();

        preferences.Folders = [.. library.Folders];
        preferences.Volume = player.Volume;
        preferences.Shuffle = player.Shuffle;
        preferences.Repeat = player.Repeat;
        preferences.TrackPath = player.Current?.Path ?? "";
        preferences.Position = player.Position;

        try
        {
            Directory.CreateDirectory(Folder);

            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                json.WriteNumber("v", 1);
                json.WriteNumber("volume", preferences.Volume);
                json.WriteBoolean("shuffle", preferences.Shuffle);
                json.WriteNumber("repeat", preferences.Repeat);
                json.WriteNumber("visualizer", preferences.Visualizer);
                json.WriteString("track", preferences.TrackPath);
                json.WriteNumber("position", preferences.Position);

                json.WriteStartArray("folders");
                foreach (var folder in preferences.Folders) json.WriteStringValue(folder);
                json.WriteEndArray();

                json.WriteEndObject();
            }

            Write(StatePath, buffer.ToArray());
        }
        catch
        {
            // Losing the state file must never take the player down with it.
        }
    }

    public static Track[] LoadLibrary()
    {
        try
        {
            if (!File.Exists(LibraryPath)) return [];

            using var json = JsonDocument.Parse(File.ReadAllBytes(LibraryPath));

            // Version 2 added the tag details; a cache from before that has to be
            // scanned again rather than shown with half its metadata missing.
            if (!json.RootElement.TryGetProperty("v", out var version) || version.GetInt32() < 2)
                return [];

            if (!json.RootElement.TryGetProperty("tracks", out var list) || list.ValueKind != JsonValueKind.Array)
                return [];

            var tracks = new List<Track>(list.GetArrayLength());

            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 7) continue;

                var details = item.GetArrayLength() >= 12
                    ? new Details(
                        Text(item[7]),
                        Text(item[8]),
                        (uint)item[9].GetInt64(),
                        (uint)item[10].GetInt64(),
                        (uint)item[11].GetInt64())
                    : default;

                tracks.Add(Track.Create(
                    Text(item[0]),
                    Text(item[1]),
                    Text(item[2]),
                    Text(item[3]),
                    item[4].GetDouble(),
                    item[5].GetInt64(),
                    item[6].GetInt64(),
                    details));
            }

            tracks.Sort(TrackOrder.Compare);
            return [.. tracks];
        }
        catch
        {
            return [];
        }
    }

    public static void SaveLibrary(Track[] tracks)
    {
        try
        {
            Directory.CreateDirectory(Folder);

            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                json.WriteNumber("v", 2);

                json.WriteStartArray("tracks");
                foreach (var track in tracks)
                {
                    json.WriteStartArray();
                    json.WriteStringValue(track.Path);
                    json.WriteStringValue(track.Title);
                    json.WriteStringValue(track.Artist);
                    json.WriteStringValue(track.Album);
                    json.WriteNumberValue(track.Duration);
                    json.WriteNumberValue(track.Size);
                    json.WriteNumberValue(track.Modified);
                    json.WriteStringValue(track.Details.AlbumArtist);
                    json.WriteStringValue(track.Details.Genre);
                    json.WriteNumberValue(track.Details.Year);
                    json.WriteNumberValue(track.Details.TrackNumber);
                    json.WriteNumberValue(track.Details.Bitrate);
                    json.WriteEndArray();
                }
                json.WriteEndArray();

                json.WriteEndObject();
            }

            Write(LibraryPath, buffer.ToArray());
        }
        catch
        {
            // The library rebuilds from the folders next time.
        }
    }

    private static float ReadNumber(JsonElement root, string name, float fallback) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? (float)value.GetDouble()
            : fallback;

    private static string Text(JsonElement item) =>
        item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";

    private static void Write(string path, byte[] bytes)
    {
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
    }
}

// ---------------------------------------------------------------------------
// What a track sounds like. CUI hands the app no samples, so the file is
// decoded once, off the frame thread, into a table of spectra: one row per
// ~20 ms of playback. The effects read the row at the position being heard, so
// the bars follow the song rather than a clock, and a seek moves them with it.
// ---------------------------------------------------------------------------

internal sealed class Sound
{
    public const int Bands = 64;

    private const int Window = 1024;         // 64 ms: long enough to show a bass note
    private const int RowsPerSecond = 50;    // one row per 20 ms of playback
    private const int FrameCap = 12000;      // bounds a long file's table at a few MB
    private const int LowBands = 14;         // a kick drum and a bass line live here
    private const int OnsetGap = 4;          // rows compared to spot a transient
    private const float OnsetDecay = 0.92f;  // how fast a hit lets go
    private const float MinHz = 40f;
    private const float MaxHz = 7000f;
    private const double MaxSeconds = 15 * 60;
    private const ulong MaxBytes = 30ul << 20;   // 15 minutes of the WAV profile below
    private const int Keep = 2;
    private const float Tau = MathF.PI * 2f;

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Sound?> Cache = [with(StringComparer.OrdinalIgnoreCase)];
    private static readonly Queue<string> Order = new();

    private readonly float[] _cells;    // Bands per row, normalized 0..1
    private readonly float[] _level;    // one loudness value per row
    private readonly float[] _attack;   // one onset value per row
    private readonly int _rows;

    private Sound(float[] cells, float[] level, float[] attack, int rows)
    {
        _cells = cells;
        _level = level;
        _attack = attack;
        _rows = rows;
    }

    /// <summary>
    /// Decodes and measures a track. Never throws: a file the system will not
    /// decode still plays, it just has no spectrum to draw.
    /// </summary>
    public static async Task<Sound?> LoadAsync(string path, double duration)
    {
        if (path.Length == 0 || duration > MaxSeconds) return null;

        lock (Gate)
        {
            if (Cache.TryGetValue(path, out var cached)) return cached;
        }

        Sound? sound;

        try
        {
            sound = await MeasureFileAsync(path);
        }
        catch
        {
            // A file that defeats the reader must not take the frame loop down.
            sound = null;
        }

        lock (Gate)
        {
            Cache[path] = sound;
            Order.Enqueue(path);

            // Two tracks is enough to make skipping back and forth instant.
            while (Order.Count > Keep) Cache.Remove(Order.Dequeue());
        }

        return sound;
    }

    /// <summary>
    /// The row at <paramref name="position"/>, blended between neighbours so a
    /// slow frame rate does not turn the spectrum into steps.
    /// </summary>
    public void Read(double position, double duration, Span<float> bands, out float level, out float attack)
    {
        var at = duration > 0 ? (float)(position / duration) : 0f;
        var row = Math.Clamp(at * (_rows - 1), 0f, _rows - 1f);
        var index = (int)row;
        var next = Math.Min(index + 1, _rows - 1);
        var blend = row - index;

        var now = index * Bands;
        var later = next * Bands;

        for (var band = 0; band < Bands; band++)
            bands[band] = _cells[now + band] + (_cells[later + band] - _cells[now + band]) * blend;

        level = _level[index] + (_level[next] - _level[index]) * blend;
        attack = _attack[index] + (_attack[next] - _attack[index]) * blend;
    }

    /// <summary>The loudest row behind one bar of the seek bar, 0..1.</summary>
    public float Shape(int index, int count)
    {
        var from = (int)((long)index * _rows / count);
        var to = Math.Max(from + 1, (int)((long)(index + 1) * _rows / count));
        var peak = 0f;

        for (var row = from; row < to && row < _rows; row++)
            peak = MathF.Max(peak, _level[row]);

        return peak;
    }

    private static async Task<Sound?> MeasureFileAsync(string path)
    {
        var pcm = await DecodeAsync(path);

        // The transcoder writes a RIFF header before the samples; reading it as
        // audio would put a click at the start of every track.
        return Parse(pcm) is { } wave ? Measure(pcm, wave) : null;
    }

    /// <summary>Any codec the system can read, through the same WAV.</summary>
    private static async Task<byte[]> DecodeAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);

            using var source = await file.OpenReadAsync();
            using var destination = new InMemoryRandomAccessStream();

            var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Low);
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = false };
            var prepared = await transcoder.PrepareStreamTranscodeAsync(source, destination, profile);

            if (!prepared.CanTranscode) return [];

            await prepared.TranscodeAsync();

            // Reading the whole stream is the expensive part; a track this long
            // is not worth the megabytes for a bar chart.
            if (destination.Size > MaxBytes) return [];

            var size = (uint)destination.Size;
            destination.Seek(0);

            using var reader = new DataReader(destination.GetInputStreamAt(0));
            await reader.LoadAsync(size);

            var pcm = new byte[(int)size];
            reader.ReadBytes(pcm);
            return pcm;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>PCM inside the RIFF the transcoder wrote: where it starts and what it is.</summary>
    private static Wave? Parse(byte[] file)
    {
        if (file.Length < 12) return null;
        if (!IsChunk(file, 0, "RIFF") || !IsChunk(file, 8, "WAVE")) return null;

        var rate = 0;
        var channels = 0;
        var at = 12;

        while (at + 8 <= file.Length)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at + 4));
            if (length < 0 || at + 8 + length > file.Length) break;

            if (IsChunk(file, at, "fmt "))
            {
                var tag = BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(at + 8));
                channels = BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(at + 10));
                rate = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at + 12));
                var bits = BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(at + 22));

                // The profile asks for PCM; anything else would read as noise.
                if (tag != 1 || bits != 16 || channels <= 0 || rate <= 0) return null;
            }
            else if (IsChunk(file, at, "data"))
            {
                return rate > 0 ? new Wave(rate, channels, at + 8, length) : null;
            }

            at += 8 + length + (length & 1);
        }

        return null;
    }

    private static bool IsChunk(byte[] file, int at, string id) =>
        file[at] == id[0] && file[at + 1] == id[1] && file[at + 2] == id[2] && file[at + 3] == id[3];

    private static Sound? Measure(byte[] pcm, Wave wave)
    {
        var frames = wave.Length / (wave.Channels * 2);
        if (frames < Window) return null;

        var seconds = frames / (double)wave.Rate;
        if (seconds > MaxSeconds) return null;

        // A three-minute single keeps one row every 20 ms; a long file uses fewer
        // rows per second so the table never grows past FrameCap.
        var rows = Math.Min(FrameCap, Math.Max(1, (int)(seconds * RowsPerSecond)));
        var hop = Math.Max(1, (frames - Window) / rows);
        rows = (frames - Window) / hop + 1;

        var cells = new float[rows * Bands];
        var level = new float[rows];
        var attack = new float[rows];
        var hann = new float[Window];
        var edges = BandEdges(wave.Rate);
        var step = wave.Channels * 2;

        for (var i = 0; i < Window; i++)
            hann[i] = 0.5f - 0.5f * MathF.Cos(Tau * i / (Window - 1));

        void MeasureRow(int row, float[] re, float[] im)
        {
            var start = wave.Start + row * hop * step;
            var energy = 0f;

            for (var i = 0; i < Window; i++)
            {
                var frame = start + i * step;
                var sum = 0f;

                for (var channel = 0; channel < wave.Channels; channel++)
                    sum += BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(frame + channel * 2)) / 32768f;

                var sample = sum / wave.Channels;

                re[i] = sample * hann[i];
                im[i] = 0f;
                energy += sample * sample;
            }

            level[row] = MathF.Sqrt(energy / Window);
            Transform(re, im);

            var cell = row * Bands;
            for (var band = 0; band < Bands; band++)
            {
                var first = edges[band];
                var last = Math.Max(first + 1, edges[band + 1]);
                var sum = 0f;

                for (var bin = first; bin < last; bin++)
                    sum += MathF.Sqrt(re[bin] * re[bin] + im[bin] * im[bin]);

                cells[cell + band] = sum / (last - first);
            }
        }

        // The rows are independent, and a whole track is a few seconds of maths:
        // one core less than the machine keeps the frame loop smooth meanwhile.
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };

        Parallel.For(0, rows, options, () => new Scratch(),
            (row, _, scratch) =>
            {
                MeasureRow(row, scratch.Re, scratch.Im);
                return scratch;
            },
            _ => { });

        Normalize(cells, level, rows);
        Transients(cells, attack, rows);

        return new Sound(cells, level, attack, rows);
    }

    /// <summary>
    /// Loudness is scaled to the track's loudest moment; each band to its own,
    /// because on one shared scale the bass would flatten the treble into the
    /// floor and half the bars would never move.
    /// </summary>
    private static void Normalize(float[] cells, float[] level, int rows)
    {
        var bandPeak = new float[Bands];

        for (var row = 0; row < rows; row++)
        {
            var cell = row * Bands;

            for (var band = 0; band < Bands; band++)
                bandPeak[band] = MathF.Max(bandPeak[band], cells[cell + band]);
        }

        for (var band = 0; band < Bands; band++)
        {
            var peak = bandPeak[band];
            if (peak <= 0f) continue;

            for (var row = 0; row < rows; row++)
            {
                var cell = row * Bands + band;
                cells[cell] = MathF.Sqrt(cells[cell] / peak);
            }
        }

        var loudest = 0f;
        foreach (var value in level) loudest = MathF.Max(loudest, value);

        if (loudest <= 0f) return;

        for (var row = 0; row < rows; row++)
            level[row] = MathF.Sqrt(level[row] / loudest);
    }

    /// <summary>
    /// A hit is the bass getting louder between two rows; a short tail keeps it
    /// on screen for the few frames a ring needs to be born.
    /// </summary>
    private static void Transients(float[] cells, float[] attack, int rows)
    {
        var low = new float[rows];

        for (var row = 0; row < rows; row++)
        {
            var cell = row * Bands;
            var sum = 0f;

            for (var band = 0; band < LowBands; band++) sum += cells[cell + band];
            low[row] = sum / LowBands;
        }

        var envelope = 0f;
        for (var row = 0; row < rows; row++)
        {
            var before = row >= OnsetGap ? low[row - OnsetGap] : low[row];
            var onset = MathF.Min(1f, MathF.Max(0f, low[row] - before) * 5f);

            envelope = MathF.Max(onset, envelope * OnsetDecay);
            attack[row] = envelope;
        }
    }

    /// <summary>Log-spaced band edges in FFT bins, so a bass octave gets as many bars as a treble one.</summary>
    private static int[] BandEdges(int rate)
    {
        var edges = new int[Bands + 1];

        for (var band = 0; band <= Bands; band++)
        {
            var hz = MinHz * MathF.Pow(MaxHz / MinHz, band / (float)Bands);
            var bin = Math.Clamp((int)MathF.Round(hz * Window / rate), 1, Window / 2 - 1);

            // Below the window's first few bins several bands share one; the
            // average then reads that bin once, instead of inventing detail.
            edges[band] = band == 0 ? bin : Math.Max(bin, edges[band - 1]);
        }

        return edges;
    }

    /// <summary>An in-place radix-2 FFT: the row loop allocates nothing.</summary>
    private static void Transform(float[] re, float[] im)
    {
        var n = re.Length;

        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;

            if (i >= j) continue;

            (re[i], re[j]) = (re[j], re[i]);
            (im[i], im[j]) = (im[j], im[i]);
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -Tau / length;
            var stepRe = MathF.Cos(angle);
            var stepIm = MathF.Sin(angle);
            var half = length / 2;

            for (var start = 0; start < n; start += length)
            {
                float turnRe = 1f, turnIm = 0f;

                for (var k = 0; k < half; k++)
                {
                    var at = start + k;
                    var mirror = at + half;

                    var vRe = re[mirror] * turnRe - im[mirror] * turnIm;
                    var vIm = re[mirror] * turnIm + im[mirror] * turnRe;

                    var uRe = re[at];
                    var uIm = im[at];

                    re[at] = uRe + vRe;
                    im[at] = uIm + vIm;
                    re[mirror] = uRe - vRe;
                    im[mirror] = uIm - vIm;

                    (turnRe, turnIm) = (
                        turnRe * stepRe - turnIm * stepIm,
                        turnRe * stepIm + turnIm * stepRe);
                }
            }
        }
    }

    /// <summary>Where the samples start, and the rate they were written at.</summary>
    private readonly record struct Wave(int Rate, int Channels, int Start, int Length);

    /// <summary>Two windows a worker reuses, so a whole track does not churn the heap.</summary>
    private sealed class Scratch
    {
        public readonly float[] Re = new float[Window];
        public readonly float[] Im = new float[Window];
    }
}

// ---------------------------------------------------------------------------
// The seek bar. The bars are the track's own loudness, one sample of the
// measured envelope per bar: the part already played is lit, the rest waits.
// ---------------------------------------------------------------------------

internal sealed class SeekBar
{
    private const int Bins = 96;

    private readonly Player _player;
    private readonly PaintFn _paint;

    public SeekBar(Player player)
    {
        _player = player;
        _paint = Paint;
    }

    public float Hue { get; set; } = 268f;

    /// <summary>The decoded track; null until the background read has one.</summary>
    public Sound? Sound { get; set; }

    public void Draw(Ui ui, UiText id, Size width, float height)
    {
        var canvas = ui.Canvas(id, width, Size.Px(height), _paint);

        if (canvas.Pressed && _player.Duration > 0)
        {
            var fraction = Math.Clamp(canvas.MouseX / MathF.Max(1f, canvas.Rect.W), 0f, 1f);
            _player.Seek(fraction * _player.Duration);
        }
    }

    private void Paint(Ui ui, RectF rect)
    {
        if (rect.W < 24 || rect.H < 6) return;

        var count = Math.Clamp((int)(rect.W / 5f), 24, Bins);
        var step = rect.W / count;
        var width = MathF.Max(1.5f, step - 1.2f);
        var bottom = rect.Y + rect.H;
        var span = rect.H - 2f;

        var duration = _player.Duration;
        var progress = duration > 0 ? (float)Math.Clamp(_player.Position / duration, 0d, 1d) : 0f;
        var played = rect.X + rect.W * progress;

        for (var i = 0; i < count; i++)
        {
            var value = Sound?.Shape(i, count) ?? 0f;
            var height = MathF.Max(2f, value * span);
            var x = rect.X + i * step;

            var color = x + width <= played + 0.5f
                ? Color.Oklch(0.74f, 0.16f, Hue + i * 0.5f)
                : Color.Oklch(0.44f, 0.02f, Hue).WithAlpha(0.8f);

            ui.DrawRect(new RectF(x, bottom - height, width, height), color, MathF.Min(2f, width / 2f));
        }

        var head = Math.Clamp(played, rect.X + 1f, rect.X + rect.W - 1f);
        ui.DrawRect(new RectF(head - 1f, rect.Y, 2f, rect.H), Color.Foreground, 1f);
    }
}

// ---------------------------------------------------------------------------
// The eight effects. What they move is measured from the file — the row of the
// spectrum being heard — so only the motion between rows is animation. Nothing
// here invents a wave: when the track is silent, the field is empty.
// ---------------------------------------------------------------------------

internal sealed class Visualizer
{
    public static readonly string[] Names =
        ["Bars", "Wave", "Ring", "Orbs", "Aurora", "Pulse", "Grid", "Nebula"];

    public static int Count => Names.Length;

    /// <summary>The Grid effect is drawn with CUI's own Heatmap in the mini window.</summary>
    public const int GridIndex = 6;

    private const int Bands = Sound.Bands;
    private const float Tau = MathF.PI * 2f;
    private const float RingLife = 1.5f;

    private readonly float[] _bands = new float[Bands];
    private readonly float[] _rings = [-1f, -1f, -1f, -1f];
    private readonly (float X, float Y)[] _points = new (float, float)[Bands * 2 + 4];
    private readonly PaintFn _paint;
    private readonly PaintFn _paintBackground;

    private float[] _heat = [];
    private float _time;
    private float _beat;
    private float _sinceRing;
    private int _nextRing;
    private int _mode;

    public Visualizer(int mode)
    {
        _mode = Math.Clamp(mode, 0, Count - 1);
        _paint = (ui, rect) => Paint(ui, rect, background: false);
        _paintBackground = (ui, rect) => Paint(ui, rect, background: true);
    }

    public int Mode
    {
        get => _mode;
        set => _mode = Math.Clamp(value, 0, Count - 1);
    }

    public float Hue { get; set; } = 268f;

    /// <summary>How loud the moment being played is, 0..1.</summary>
    public float Level { get; private set; }

    /// <summary>The current track's cover, drawn whole behind the bars.</summary>
    public ulong Art { get; set; }
    public int ArtWidth { get; set; }
    public int ArtHeight { get; set; }

    /// <summary>The same cover at 48 pixels, stretched behind the mini player as glass.</summary>
    public ulong Frost { get; set; }

    public void Advance(Sound? sound, double position, double duration, float delta, bool playing)
    {
        delta = Math.Clamp(delta, 0f, 0.1f);
        _time += playing ? delta : 0f;

        Span<float> target = stackalloc float[Bands];
        var level = 0f;
        var attack = 0f;

        sound?.Read(position, duration, target, out level, out attack);

        for (var band = 0; band < Bands; band++)
        {
            // A meter jumps to a hit and falls back; the spectrum is measured,
            // the fall is the only part that is taste.
            var rate = target[band] > _bands[band] ? 28f : 6f;
            _bands[band] += (target[band] - _bands[band]) * MathF.Min(1f, delta * rate);
        }

        Level = level;
        _beat = MathF.Max(attack, _beat * MathF.Exp(-delta * 6f));

        for (var ring = 0; ring < _rings.Length; ring++)
        {
            if (_rings[ring] < 0f) continue;

            _rings[ring] += delta;
            if (_rings[ring] > RingLife) _rings[ring] = -1f;
        }

        _sinceRing += delta;
        if (attack > 0.28f && _sinceRing > 0.15f)
        {
            _rings[_nextRing] = 0f;
            _nextRing = (_nextRing + 1) % _rings.Length;
            _sinceRing = 0f;
        }
    }

    public CanvasResult Draw(Ui ui, UiText id, Size width, Size height, bool background = false) =>
        ui.Canvas(id, width, height, background ? _paintBackground : _paint);

    /// <summary>
    /// The Grid effect as CUI's Heatmap. The wrapper is exactly the grid's
    /// height so the scale the control draws below it is clipped away.
    /// </summary>
    public void DrawHeat(Ui ui, UiText id, float width, float height)
    {
        if (width < 8 || height < 8) return;

        var cols = Math.Clamp((int)MathF.Ceiling(width / 24f), 6, 30);
        var rows = Math.Clamp((int)MathF.Ceiling(height / 24f), 4, 18);
        var cell = MathF.Min(width / cols, height / rows);

        var count = cols * rows;
        if (_heat.Length < count) _heat = new float[count];

        for (var c = 0; c < cols; c++)
        {
            var level = (0.18f + _bands[c * Bands / cols] * 0.82f) * rows;

            for (var r = 0; r < rows; r++)
                _heat[r * cols + c] = Math.Clamp(level - (rows - r) + 1f, 0f, 1f);
        }

        var gridWidth = cols * cell;
        var gridHeight = rows * cell;

        using (ui.Box(new Box
        {
            Width = Size.Px(width),
            Height = Size.Px(height),
            Align = Align.Center,
            Justify = Justify.Center,
            Clip = true,
        }))
        using (ui.Box(new Box { Width = Size.Px(gridWidth), Height = Size.Px(gridHeight), Clip = true }))
        {
            ui.Heatmap(id, _heat.AsSpan(0, count), rows, cols, new HeatmapOpts
            {
                Cell = cell,
                Min = 0f,
                Max = 1f,
                Color = Color.Oklch(0.66f, 0.20f, Hue),
                Width = Size.Px(gridWidth),
            });
        }
    }

    /// <summary>Where a dust mote sits in the field: a fixed scatter, not a signal.</summary>
    private static float Scatter(int index)
    {
        var hash = (uint)index * 2654435761u;
        hash ^= hash >> 13;
        return (hash & 0xFFFF) / 65535f;
    }

    private void Paint(Ui ui, RectF rect, bool background)
    {
        if (rect.W < 8 || rect.H < 8) return;

        ui.DrawRectGradient(rect, Color.Oklch(0.26f, 0.05f, Hue), Color.Oklch(0.15f, 0.02f, Hue), 10f);

        if (Art != 0 && ArtWidth > 0 && ArtHeight > 0) Cover(ui, rect, background);

        switch (_mode)
        {
            case 0: Bars(ui, rect); break;
            case 1: Wave(ui, rect); break;
            case 2: Ring(ui, rect); break;
            case 3: Orbs(ui, rect); break;
            case 4: Aurora(ui, rect); break;
            case 5: Pulse(ui, rect); break;
            case 6: Grid(ui, rect); break;
            default: Nebula(ui, rect); break;
        }

        // The mini player puts text on top; keep the moving parts behind it quiet.
        if (background)
            ui.DrawRect(rect, new Color(0, 0, 0, 16), 0f);
    }

    /// <summary>
    /// The cover whole and undistorted, under a scrim that deepens towards the
    /// floor: the art stays readable at the top, the bars and the wave at the bottom.
    /// </summary>
    private void Cover(Ui ui, RectF rect, bool fill)
    {
        // Behind the mini player the 48-pixel copy IS the blur: stretched over the
        // window it is a wash of the album's colours with nothing left to read.
        if (fill)
        {
            ui.DrawImage(Frost != 0 ? Frost : Art, rect, default, 0f);
            ui.DrawRect(rect, Color.Oklch(0.08f, 0.02f, Hue).WithAlpha(0.10f), 0f);
            return;
        }

        var scale = MathF.Min(rect.W / ArtWidth, rect.H / ArtHeight);
        var width = ArtWidth * scale;
        var height = ArtHeight * scale;

        if (Frost != 0 && (rect.W - width > 2f || rect.H - height > 2f))
        {
            ui.DrawImage(Frost, rect, default, 10f);
            ui.DrawRect(rect, Color.Oklch(0.08f, 0.02f, Hue).WithAlpha(0.58f), 10f);
        }

        var box = new RectF(
            rect.X + (rect.W - width) / 2f,
            rect.Y + (rect.H - height) / 2f,
            width,
            height);

        ui.DrawImage(Art, box, default, 10f);
        ui.DrawRectGradient(
            box,
            Color.Oklch(0.08f, 0.02f, Hue).WithAlpha(0.18f),
            Color.Oklch(0.08f, 0.02f, Hue).WithAlpha(0.62f),
            10f);
    }

    private void Bars(Ui ui, RectF rect)
    {
        var count = Math.Clamp((int)(rect.W / 9f), 12, Bands);
        var gap = MathF.Max(2f, rect.W / count * 0.22f);
        var width = (rect.W - gap * (count - 1) - 24f) / count;
        var left = rect.X + 12f;
        var floor = rect.Y + rect.H - 12f;
        var span = rect.H - 28f;

        for (var i = 0; i < count; i++)
        {
            var value = _bands[i * Bands / count];
            var height = MathF.Max(3f, value * span);
            var x = left + i * (width + gap);

            // The hue walks across the field, so the wall of bars reads as a rainbow.
            var color = Color.Oklch(0.56f + value * 0.30f, 0.21f, Hue + i * 3.2f);

            ui.DrawRect(new RectF(x, floor - height, width, height), color, MathF.Min(3f, width / 2f));

            if (value > 0.55f)
                ui.DrawRect(new RectF(x, floor - height - 2f, width, 2f), color.WithAlpha(0.85f), 1f);
        }
    }

    private void Wave(Ui ui, RectF rect)
    {
        var count = Math.Clamp((int)(rect.W / 4f), 24, Bands);
        var step = rect.W / (count - 1);
        var middle = rect.Y + rect.H * 0.5f;
        var amplitude = rect.H * 0.28f;

        for (var i = 0; i < count; i++)
        {
            var f = i / (float)(count - 1);
            var band = _bands[i * Bands / count];

            var y = middle
                + MathF.Sin(f * 18f + _time * 3.2f) * amplitude * (0.25f + band)
                + MathF.Sin(f * 41f - _time * 5.1f) * amplitude * 0.16f;

            _points[i] = (rect.X + i * step, y);
        }

        var bottom = rect.Y + rect.H - 8f;
        _points[count] = (rect.X + rect.W, bottom);
        _points[count + 1] = (rect.X, bottom);

        ui.DrawPolygon(_points.AsSpan(0, count + 2), Color.Oklch(0.60f, 0.20f, Hue + 40f).WithAlpha(0.20f));
        ui.DrawPolyline(_points.AsSpan(0, count), 2.4f, Color.Oklch(0.86f, 0.16f, Hue + 18f));

        // A second line, half a beat behind and a hue away, gives the wave depth.
        for (var i = 0; i < count; i++)
        {
            var f = i / (float)(count - 1);
            var band = _bands[i * Bands / count];

            var y = middle
                + MathF.Sin(f * 13f - _time * 2.1f + 1.7f) * amplitude * (0.18f + band * 0.8f)
                + MathF.Sin(f * 29f + _time * 3.7f) * amplitude * 0.12f;

            _points[i] = (rect.X + i * step, y);
        }

        ui.DrawPolyline(_points.AsSpan(0, count), 1.6f, Color.Oklch(0.78f, 0.17f, Hue - 42f).WithAlpha(0.55f));
    }

    private void Ring(Ui ui, RectF rect)
    {
        var cx = rect.X + rect.W * 0.5f;
        var cy = rect.Y + rect.H * 0.5f;
        var radius = MathF.Min(rect.W, rect.H) * 0.26f;
        var count = 56;

        ui.DrawCircle(cx, cy, radius * (0.96f + _beat * 0.10f), Color.Oklch(0.40f, 0.14f, Hue + 30f).WithAlpha(0.55f));

        for (var i = 0; i < count; i++)
        {
            var value = _bands[i * Bands / count];
            var angle = Tau * i / count - MathF.PI / 2f + _time * 0.15f;
            var length = radius * (0.10f + value * 1.15f);
            var cos = MathF.Cos(angle);
            var sin = MathF.Sin(angle);

            ui.DrawLine(
                cx + cos * radius, cy + sin * radius,
                cx + cos * (radius + length), cy + sin * (radius + length),
                2.4f,
                Color.Oklch(0.62f + value * 0.24f, 0.20f, Hue + i * 2.2f));
        }

        // A counter-rotating inner ring, dimmer, so the middle is not dead.
        for (var i = 0; i < 24; i++)
        {
            var angle = Tau * i / 24f + _time * 0.4f;
            var inner = radius * 0.62f;
            var reach = 4f + _beat * 6f;
            var cos = MathF.Cos(angle);
            var sin = MathF.Sin(angle);

            ui.DrawLine(
                cx + cos * inner, cy + sin * inner,
                cx + cos * (inner + reach), cy + sin * (inner + reach),
                1.6f,
                Color.Oklch(0.80f, 0.14f, Hue + 180f + i * 4f).WithAlpha(0.45f));
        }
    }

    private void Orbs(Ui ui, RectF rect)
    {
        var cx = rect.X + rect.W * 0.5f;
        var cy = rect.Y + rect.H * 0.5f;
        var orbit = MathF.Min(rect.W, rect.H) * 0.30f;

        for (var i = 0; i < 9; i++)
        {
            var value = _bands[i * Bands / 9];
            var angle = Tau * i / 9f + _time * (0.35f + i * 0.03f);
            var distance = orbit * (0.55f + value * 0.9f);
            var x = cx + MathF.Cos(angle) * distance;
            var y = cy + MathF.Sin(angle) * distance * 0.82f;
            var radius = 6f + value * 22f;
            var hue = Hue + i * 36f;

            ui.DrawCircle(x, y, radius * 1.9f, Color.Oklch(0.70f, 0.22f, hue).WithAlpha(0.10f + value * 0.16f));
            ui.DrawCircle(x, y, radius, Color.Oklch(0.72f, 0.20f, hue).WithAlpha(0.35f + value * 0.5f));
        }

        ui.DrawCircle(cx, cy, 16f + _beat * 10f, Color.Oklch(0.88f, 0.14f, Hue + 60f).WithAlpha(0.30f));
    }

    /// <summary>Three ribbons of light drifting across the panel, each a hue apart.</summary>
    private void Aurora(Ui ui, RectF rect)
    {
        var count = Math.Clamp((int)(rect.W / 6f), 24, Bands);
        var step = rect.W / (count - 1);
        var thickness = rect.H * 0.16f;

        for (var ribbon = 0; ribbon < 3; ribbon++)
        {
            var offset = ribbon * 1.9f;
            var baseY = rect.Y + rect.H * (0.30f + ribbon * 0.14f);
            var amplitude = rect.H * (0.14f - ribbon * 0.025f);
            var hue = Hue + ribbon * 34f;

            for (var i = 0; i < count; i++)
            {
                var f = i / (float)(count - 1);
                var band = _bands[i * Bands / count];

                var top = baseY
                    + MathF.Sin(f * 5.6f + _time * (0.55f + ribbon * 0.18f) + offset) * amplitude
                    + MathF.Sin(f * 13.3f - _time * 1.1f + offset) * amplitude * 0.4f
                    - band * amplitude * 1.1f;

                _points[i] = (rect.X + i * step, top);
            }

            // The underside trails the crest, and swells where the band is loud.
            for (var i = 0; i < count; i++)
            {
                var j = count - 1 - i;
                var band = _bands[j * Bands / count];

                _points[count + i] = (_points[j].X, _points[j].Y + thickness + band * amplitude * 0.8f);
            }

            ui.DrawPolygon(_points.AsSpan(0, count * 2), Color.Oklch(0.62f, 0.21f, hue).WithAlpha(0.18f));
            ui.DrawPolyline(_points.AsSpan(0, count), 2f, Color.Oklch(0.86f, 0.15f, hue + 12f).WithAlpha(0.85f));
        }
    }

    /// <summary>Rings of light opening outwards from each real onset.</summary>
    private void Pulse(Ui ui, RectF rect)
    {
        var cx = rect.X + rect.W * 0.5f;
        var cy = rect.Y + rect.H * 0.5f;
        var reach = MathF.Min(rect.W, rect.H) * 0.48f;
        var segments = 44;

        for (var ring = 0; ring < _rings.Length; ring++)
        {
            var age = _rings[ring];
            if (age < 0f) continue;

            var life = Math.Clamp(age / RingLife, 0f, 1f);
            var radius = reach * (0.12f + life * 0.88f);

            var color = Color.Oklch(0.72f, 0.19f, Hue + ring * 34f + life * 60f)
                .WithAlpha((1f - life) * (0.35f + _beat * 0.30f));

            for (var i = 0; i < segments; i++)
            {
                var a = Tau * i / segments;
                var b = Tau * (i + 1) / segments;

                ui.DrawLine(
                    cx + MathF.Cos(a) * radius, cy + MathF.Sin(a) * radius,
                    cx + MathF.Cos(b) * radius, cy + MathF.Sin(b) * radius,
                    2f,
                    color);
            }
        }

        ui.DrawCircle(cx, cy, 10f + _beat * 16f, Color.Oklch(0.88f, 0.15f, Hue + 24f).WithAlpha(0.32f));
    }

    /// <summary>An LED matrix: each column rises like a level meter, hue walking left to right.</summary>
    private void Grid(Ui ui, RectF rect)
    {
        var cols = Math.Clamp((int)(rect.W / 20f), 8, 26);
        var rows = Math.Clamp((int)(rect.H / 20f), 4, 14);
        var cell = MathF.Min(rect.W / cols, rect.H / rows);
        var size = cell * 0.78f;
        var left = rect.X + (rect.W - cell * cols) / 2f;
        var top = rect.Y + (rect.H - cell * rows) / 2f;

        for (var c = 0; c < cols; c++)
        {
            var level = (0.18f + _bands[c * Bands / cols] * 0.82f) * rows;

            for (var r = 0; r < rows; r++)
            {
                var lit = Math.Clamp(level - (rows - r) + 1f, 0f, 1f);

                var color = lit > 0.01f
                    ? Color.Oklch(0.50f + lit * 0.34f, 0.22f, Hue + c * 5f + lit * 24f)
                        .WithAlpha(0.30f + lit * 0.65f)
                    : Color.Oklch(0.34f, 0.02f, Hue).WithAlpha(0.45f);

                ui.DrawRect(
                    new RectF(
                        left + c * cell + (cell - size) / 2f,
                        top + r * cell + (cell - size) / 2f,
                        size,
                        size),
                    color,
                    MathF.Min(3f, size / 3f));
            }
        }
    }

    /// <summary>Dust drifting in slow orbits, brightening where the music pushes.</summary>
    private void Nebula(Ui ui, RectF rect)
    {
        var cx = rect.X + rect.W * 0.5f;
        var cy = rect.Y + rect.H * 0.5f;
        var span = MathF.Min(rect.W, rect.H) * 0.42f;
        var count = 44;

        for (var i = 0; i < count; i++)
        {
            var seed = Scatter(i * 7 + 3);
            var band = _bands[i * Bands / count];
            var angle = seed * Tau + _time * (0.10f + seed * 0.22f);
            var distance = span * (0.15f + seed * 0.95f);

            var x = cx + MathF.Cos(angle) * distance;
            var y = cy + MathF.Sin(angle * 1.3f) * distance * 0.8f;
            var radius = 1.6f + band * 7f + seed * 2f;
            var hue = Hue + seed * 180f;

            ui.DrawCircle(x, y, radius * 2.6f, Color.Oklch(0.62f, 0.20f, hue).WithAlpha(0.10f + band * 0.12f));
            ui.DrawCircle(x, y, radius, Color.Oklch(0.80f, 0.16f, hue).WithAlpha(0.35f + band * 0.5f));
        }
    }
}

// ---------------------------------------------------------------------------
// Numbers and colours.
// ---------------------------------------------------------------------------

internal static class Format
{
    public static string Time(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;

        var total = (int)seconds;
        var hours = total / 3600;
        var minutes = total / 60 % 60;
        var rest = total % 60;

        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{rest:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{rest:00}");
    }

    public static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>A track whose tags never gave a length reads as unknown, not as zero.</summary>
    public static string Length(double seconds) => seconds > 0 ? Time(seconds) : "--:--";

    /// <summary>"Artist · Album", or whichever half the tags actually gave us.</summary>
    public static string Credit(Track track) => (track.Artist.Length, track.Album.Length) switch
    {
        (0, 0) => "Unknown artist",
        (0, _) => track.Album,
        (_, 0) => track.Artist,
        _ => $"{track.Artist} · {track.Album}",
    };

    /// <summary>"1992 · Rock · 320 kbps · Track 4", skipping whatever the tags left out.</summary>
    public static string Meta(Details details)
    {
        var parts = new List<string>(4);

        if (details.Year > 0) parts.Add(details.Year.ToString(CultureInfo.InvariantCulture));
        if (details.Genre.Length > 0) parts.Add(details.Genre);

        if (details.Bitrate > 0)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{details.Bitrate / 1000} kbps"));

        if (details.TrackNumber > 0)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"Track {details.TrackNumber}"));

        return string.Join(" · ", parts);
    }
}

internal static class Palette
{
    /// <summary>A stable hue per file, so a track keeps its colour between runs.</summary>
    public static float HueOf(string path)
    {
        var hash = 2166136261u;

        foreach (var c in path)
        {
            hash ^= c;
            hash *= 16777619;
        }

        return hash % 360;
    }
}
