#:package CuiSharp@*
#:package MonoTorrent@3.0.2

// The properties below are what make `dotnet publish download.cs -c Release -o .`
// leave one small native executable beside this file, with no .NET on the target.
#:property PublishAot=true
#:property OptimizationPreference=Size
#:property InvariantGlobalization=true
#:property StripSymbols=true
#:property DebugType=none
#:property OutputType=WinExe
#:property ApplicationIcon=download.ico
#:property EventSourceSupport=false
#:property HttpActivityPropagationSupport=false
#:property MetadataUpdaterSupport=false
#:property UseSystemResourceKeys=true
#:property StackTraceSupport=false
#:property MetricsSupport=false

// Mono.Nat is MonoTorrent's optional UPnP helper. This app leaves port mapping
// off (outbound peers, DHT and trackers are enough), so the package's trim
// annotations are never reached; keep a clean publish by silencing the
// assembly-level report.
#:property NoWarn=IL2104

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CuiSharp;
using Microsoft.Win32.SafeHandles;
using MonoTorrent;
using MonoTorrent.Client;

// ---------------------------------------------------------------------------
// Downloads — a native CuiSharp download manager for magnet links and plain
// HTTP files. The list is newest first; the selected item opens a details pane
// with its real progress, peers and speed.
//
//   run      dotnet run download.cs                     the window
//   ship     dotnet publish download.cs -c Release -o . one download.exe, no runtime needed
//            (add -p:CuiSharpHeadless=true to publish a build `dotnet cui` can drive)
//
//   iterate  CUISHARP_HEADLESS=1 ./download.exe         no window; serves the tool's protocol
//            dotnet cui tree                            the controls the last frame drew
//            dotnet cui click Pause                     click one, print the frame it produced
//            dotnet cui png shot.png                    that frame as a PNG
//            dotnet cui docs read live-debugging        the guide, offline
//            dotnet cui api ProgressOpts                one exact declaration
//            dotnet cui session < steps.txt             many clicks against one app instance
//
// The BitTorrent half is MonoTorrent: trackers, DHT, magnet metadata, fast
// resume and multi-peer downloading are its job. HTTP files are fetched with
// several range requests at once, the way a download manager is expected to.
// ---------------------------------------------------------------------------

var state = StateStore.Load();
await using var downloads = new DownloadManager(state);
var app = new DownloadApp(downloads);

return CuiApp.Run(app.Options, app.Draw);

// ---------------------------------------------------------------------------
// The window: header, the one-line add bar, the queue and its details pane.
// ---------------------------------------------------------------------------

internal sealed class DownloadApp(DownloadManager downloads)
{
    private const float WideWidth = 940f;
    private const float CompactWidth = 560f;
    private const float CompactHeight = 520f;

    private static readonly string[] Filters = ["All", "Active", "Done"];

    private readonly DownloadManager _downloads = downloads;

    private string _url = "";
    private string _error = "";
    private string _selected = "";
    private string _picked = "";
    private bool _picking;
    private bool _detailsOpen;
    private bool _started;

    private int _filter;
    private long? _free;
    private long _freeAt;
    private string _freeFor = "";

    public CuiAppOptions Options { get; } = new()
    {
        Title = "Downloads",
        Width = 1180,
        Height = 820,
        MinWidth = 420,
        MinHeight = 360,
        Theme = Theme.Dark,
        Accent = Accent.Blue,
        Renderer = Renderer.Software,
    };

    public void Draw(Ui ui)
    {
        var (width, height) = ui.ViewportSize();
        var wide = width >= WideWidth && height >= 420f;

        _downloads.Tick();
        Notices(ui);

        if (Find() is null && _downloads.Items.Count > 0)
            _selected = _downloads.Items[0].Id;

        using (ui.Column(new Box { Pad = 14, Gap = 12, Height = Size.Fill }))
        {
            Header(ui, height, wide);
            AddBar(ui, width);

            if (wide) Body(ui, width, height);
            else QueuePanel(ui, Size.Fill, Size.Fill, false, width < CompactWidth);

            if (height >= 540f) Footer(ui, width);
        }

        Picker(ui);
        DetailDialog(ui, height);

        ui.RequestWakeup(_downloads.IsBusy ? 1f / 10f : 1f);
    }

    /// <summary>The title, the live totals and the folder downloads land in.</summary>
    private void Header(Ui ui, float height, bool wide)
    {
        var active = _downloads.Items.Count(item => item.IsActive);
        var down = _downloads.Items.Sum(item => item.Rate);
        var up = _downloads.Items.Sum(item => item.UploadRate);

        using (ui.Row(new Box { Gap = 12, Align = Align.Center, Width = Size.Fill, Wrap = !wide }))
        {
            using (ui.Box(new Box
            {
                Width = Size.Px(42),
                Height = Size.Px(42),
                Radius = 13,
                Bg = Color.SurfaceMuted,
                Align = Align.Center,
                Justify = Justify.Center,
            }))
            {
                ui.Icon(Icons.Download, 21, Color.Accent);
            }

            using (ui.Column(new Box { Gap = 2, MinWidth = 0 }))
            {
                ui.Text("Downloads", new TextOpts
                {
                    Font = Font.Medium,
                    Size = 26,
                    Color = Color.Foreground,
                });

                if (height >= CompactHeight)
                {
                    using (ui.Row(new Box { Gap = 8, Align = Align.Center, Wrap = true }))
                    {
                        ui.Text(Format.Items(_downloads.Items.Count), new TextOpts
                        {
                            Size = 11,
                            Color = Color.TextSecondary,
                        });

                        if (active > 0)
                        {
                            ui.Text($"{active} active", new TextOpts
                            {
                                Size = 11,
                                Color = Color.Accent,
                            });
                        }

                        ui.Text("newest first", new TextOpts
                        {
                            Size = 11,
                            Color = Color.TextMuted,
                        });
                    }
                }
            }

            ui.Spacer();

            if (down > 0 && active > 0)
            {
                ui.Badge(Format.Rate(down), new BadgeOpts
                {
                    Icon = Icons.ArrowDown,
                    Tone = Tone.Info,
                    Size = ControlSize.Sm,
                });
            }

            if (up > 0 && active > 0)
            {
                ui.Badge(Format.Rate(up), new BadgeOpts
                {
                    Icon = Icons.ArrowUp,
                    Tone = Tone.Neutral,
                    Size = ControlSize.Sm,
                });
            }

            if (ui.Button(Format.Clip(Format.Leaf(_downloads.Destination), 20), new ButtonOpts
            {
                Icon = Icons.FolderOpen,
                Size = ControlSize.Sm,
                Variant = ButtonVariant.Secondary,
                Tooltip = $"Downloads go to {_downloads.Destination}",
            }))
            {
                OpenPicker();
            }
        }
    }

    /// <summary>Paste a link and press Enter, or click Add.</summary>
    private void AddBar(Ui ui, float width)
    {
        using (ui.Card(new Card
        {
            Pad = 8,
            Gap = 8,
            Dir = Dir.Row,
            Align = Align.Center,
            Width = Size.Fill,
        }))
        {
            ui.Icon(Icons.Link, 16, Color.TextMuted);

            var focus = !_started;
            _started = true;

            var input = ui.Input("url", ref _url, new InputOpts
            {
                Placeholder = "Paste a magnet link or an HTTP URL",
                Clearable = true,
                AutoFocus = focus,
                Error = _error.Length > 0 ? _error : null,
                Width = Size.Fill,
            });

            if (input.Changed) _error = "";

            var add = input.Submitted;
            if (width >= 620f)
            {
                if (ui.Button("Add", new ButtonOpts
                {
                    Icon = Icons.Plus,
                    Variant = ButtonVariant.Primary,
                    Width = Size.Px(88),
                    Disabled = _url.Trim().Length == 0,
                }))
                {
                    add = true;
                }
            }
            else if (ui.IconButton(Icons.Plus, new ButtonOpts
            {
                Variant = ButtonVariant.Primary,
                Tooltip = "Add download",
                Disabled = _url.Trim().Length == 0,
            }))
            {
                add = true;
            }

            if (add) Add();
        }
    }

    private void Add()
    {
        if (!_downloads.TryAdd(_url, out var item, out var error))
        {
            _error = error;
            return;
        }

        _url = "";
        _error = "";
        _selected = item!.Id;
    }

    private void Body(Ui ui, float width, float height)
    {
        var detailsWidth = Math.Clamp(width * 0.30f, 318f, 402f);

        using (ui.Row(new Box
        {
            Width = Size.Fill,
            Height = Size.Fill,
            Gap = 12,
            Align = Align.Stretch,
        }))
        {
            QueuePanel(ui, Size.Fill, Size.Fill, true, false);
            DetailsPanel(ui, Size.Px(detailsWidth), Size.Fill, height);
        }
    }

    /// <summary>The queue, newest first, filtered by All / Active / Done.</summary>
    private void QueuePanel(Ui ui, Size width, Size height, bool wide, bool compact)
    {
        using (ui.Card(new Card
        {
            Width = width,
            Height = height,
            Pad = 12,
            Gap = 10,
            Dir = Dir.Column,
        }))
        {
            using (ui.Row(new Box { Gap = 8, Align = Align.Center, Width = Size.Fill }))
            {
                using (ui.Column(new Box { Gap = 1, MinWidth = 0 }))
                {
                    ui.Text("Queue", new TextOpts { Font = Font.Medium, Size = 15 });
                    if (wide)
                        ui.Text("Newest first", new TextOpts { Size = 10, Color = Color.TextMuted });
                }

                ui.Badge(Format.Number(_downloads.Items.Count), new BadgeOpts
                {
                    Tone = Tone.Neutral,
                    Size = ControlSize.Sm,
                });

                ui.Spacer();

                ui.Segmented("filter", Filters, ref _filter, new SegmentedOpts
                {
                    Size = ControlSize.Sm,
                });
            }

            ui.Separator();

            var visible = Visible();
            if (visible.Count == 0)
            {
                DrawEmpty(ui);
                return;
            }

            using (ui.Scroll(new ScrollOpts
            {
                Id = "queue",
                Width = Size.Fill,
                Height = Size.Fill,
                Gap = 6,
            }))
            using (ui.Column(new Box { Gap = 6, Width = Size.Fill }))
            {
                foreach (var item in visible) Row(ui, item, wide, compact);
            }
        }
    }

    private void DrawEmpty(Ui ui)
    {
        var (title, description) = _downloads.Items.Count == 0
            ? ("Nothing in the queue", "Paste a magnet link or an HTTP URL above.")
            : _filter switch
            {
                1 => ("Nothing active", "Everything here has finished or is paused."),
                2 => ("Nothing finished yet", "Completed downloads appear here."),
                _ => ("Nothing to show", "Try another filter."),
            };

        using (ui.Box(new Box
        {
            Height = Size.Fill,
            Width = Size.Fill,
            Align = Align.Center,
            Justify = Justify.Center,
        }))
        {
            ui.EmptyState(title, new EmptyOpts
            {
                Description = description,
                Icon = Icons.Download,
            });
        }
    }

    private List<DownloadItem> Visible() => _filter switch
    {
        1 => [.. _downloads.Items.Where(item => item.IsActive)],
        2 => [.. _downloads.Items.Where(item => item.Status == DownloadStatus.Completed)],
        _ => [.. _downloads.Items],
    };

    private void Row(Ui ui, DownloadItem item, bool wide, bool compact)
    {
        var tone = ToneOf(item);
        var color = Color.For(tone);

        using (ui.Card(new Card
        {
            Id = "row:" + item.Id,
            Width = Size.Fill,
            Pad = 9,
            Gap = 10,
            Dir = Dir.Row,
            Align = Align.Center,
            Hoverable = true,
            Selected = item.Id == _selected,
        }, out var clicked))
        {
            using (ui.Box(new Box
            {
                Width = Size.Px(36),
                Height = Size.Px(36),
                Radius = 10,
                Bg = Color.SurfaceMuted,
                Align = Align.Center,
                Justify = Justify.Center,
            }))
            {
                ui.Icon(item.Kind == DownloadKind.Torrent ? Icons.Download : Icons.Globe, 17, color);
            }

            using (ui.Column(new Box { Width = Size.Fill, MinWidth = 0, Gap = 4 }))
            {
                using (ui.Row(new Box { Gap = 8, Align = Align.Center, Width = Size.Fill }))
                {
                    ui.Text(item.Name, new TextOpts
                    {
                        Width = Size.Fill,
                        Font = Font.Medium,
                        Size = 13,
                        NoWrap = true,
                    });

                    if (!compact && item.Status != DownloadStatus.Downloading)
                    {
                        ui.Badge(StatusText(item), new BadgeOpts
                        {
                            Tone = tone,
                            Dot = item.IsActive,
                            Size = ControlSize.Sm,
                        });
                    }
                }

                ui.Progress((float)item.Progress, new ProgressOpts
                {
                    Width = Size.Fill,
                    Height = 4,
                    Tone = tone,
                    Indeterminate = item.Status is DownloadStatus.Connecting or DownloadStatus.Metadata,
                });

                ui.Text(MetaText(item), new TextOpts
                {
                    Size = 10,
                    Color = Color.TextMuted,
                    NoWrap = true,
                });
            }

            var acted = false;

            if (ui.IconButton(item.Status switch
            {
                DownloadStatus.Completed => Icons.FolderOpen,
                DownloadStatus.Paused or DownloadStatus.Failed => Icons.Play,
                _ => Icons.Pause,
            }, new ButtonOpts
            {
                Tooltip = item.Status switch
                {
                    DownloadStatus.Completed => "Show in Explorer",
                    DownloadStatus.Paused or DownloadStatus.Failed => "Resume",
                    _ => "Pause",
                },
                Size = ControlSize.Sm,
                Variant = ButtonVariant.Ghost,
            }))
            {
                acted = true;

                if (item.Status == DownloadStatus.Completed) Shell.Reveal(item.SavePath);
                else _downloads.Toggle(item);
            }

            if (ui.IconButton(Icons.X, new ButtonOpts
            {
                Tooltip = "Remove from list",
                Size = ControlSize.Sm,
                Variant = ButtonVariant.Ghost,
            }))
            {
                acted = true;
                _downloads.Remove(item);
            }

            if (clicked && !acted)
            {
                _selected = item.Id;
                if (!wide) _detailsOpen = true;
            }
        }
    }

    /// <summary>The right-hand pane: the selected download in full.</summary>
    private void DetailsPanel(Ui ui, Size width, Size height, float viewportHeight)
    {
        using (ui.Card(new Card
        {
            Width = width,
            Height = height,
            Pad = 14,
            Gap = 10,
            Dir = Dir.Column,
        }))
        {
            using (ui.Row(new Box { Gap = 8, Align = Align.Center }))
            {
                ui.Text("Details", new TextOpts { Font = Font.Medium, Size = 15 });
                ui.Spacer();
                if (Find() is { } item)
                {
                    ui.Badge(item.Kind == DownloadKind.Torrent ? "MAGNET" : "HTTP", new BadgeOpts
                    {
                        Tone = Tone.Neutral,
                        Size = ControlSize.Sm,
                    });
                }
            }

            if (Find() is not { } selected)
            {
                using (ui.Box(new Box
                {
                    Width = Size.Fill,
                    Height = Size.Fill,
                    Align = Align.Center,
                    Justify = Justify.Center,
                }))
                {
                    ui.EmptyState("Select a download", new EmptyOpts
                    {
                        Description = "Its progress, peers and destination appear here.",
                        Icon = Icons.Activity,
                    });
                }

                return;
            }

            using (ui.Scroll(new ScrollOpts
            {
                Id = "details",
                Width = Size.Fill,
                Height = Size.Fill,
                Gap = 10,
            }))
            using (ui.Column(new Box { Gap = 10, Width = Size.Fill }))
            {
                DetailBody(ui, selected, viewportHeight, dialog: false);
            }
        }
    }

    private DownloadItem? Find()
    {
        foreach (var item in _downloads.Items)
        {
            if (item.Id == _selected) return item;
        }

        return null;
    }

    /// <summary>Name, ring, speed graph, facts and actions. Shared with the narrow dialog.</summary>
    private void DetailBody(Ui ui, DownloadItem item, float height, bool dialog, bool actions = true)
    {
        if (!dialog)
        {
            using (ui.Column(new Box { Gap = 2, Width = Size.Fill }))
            {
                ui.Text(item.Name, new TextOpts
                {
                    Font = Font.Medium,
                    Size = 16,
                    NoWrap = true,
                    Width = Size.Fill,
                });
                ui.Tooltip(item.Name);

                ui.Text(DetailLine(item), new TextOpts
                {
                    Size = 11,
                    Color = item.Status == DownloadStatus.Failed ? Color.Danger : Color.TextSecondary,
                    NoWrap = true,
                    Width = Size.Fill,
                });
            }
        }
        else
        {
            ui.Text(DetailLine(item), new TextOpts
            {
                Size = 11,
                Color = item.Status == DownloadStatus.Failed ? Color.Danger : Color.TextSecondary,
                NoWrap = true,
                Width = Size.Fill,
            });
        }

        if (item.Status is DownloadStatus.Connecting or DownloadStatus.Metadata)
        {
            using (ui.Row(new Box
            {
                Width = Size.Fill,
                Height = Size.Px(108),
                Align = Align.Center,
                Justify = Justify.Center,
                Gap = 10,
            }))
            {
                ui.Spinner(18);
                ui.Text(StatusText(item), new TextOpts
                {
                    Size = 12,
                    Color = Color.TextSecondary,
                });
            }
        }
        else
        {
            using (ui.Row(new Box
            {
                Width = Size.Fill,
                Height = Size.Px(108),
                Align = Align.Center,
                Justify = Justify.Center,
            }))
            {
                ui.ProgressRing("Progress", (float)item.Progress, 104);
            }
        }

        if (item.Status is not (DownloadStatus.Connecting or DownloadStatus.Metadata))
        {
            using (ui.Row(new Box { Width = Size.Fill, Align = Align.Center, Gap = 8 }))
            {
                ui.Text(Format.Size(item.DoneBytes), new TextOpts
                {
                    Size = 12,
                    Font = Font.Medium,
                    Tabular = true,
                });

                ui.Text("of " + Format.Size(item.TotalBytes), new TextOpts
                {
                    Size = 11,
                    Color = Color.TextMuted,
                });

                ui.Spacer();
            }
        }

        ui.Text("Speed · last 60 seconds", new TextOpts
        {
            Size = 10,
            Color = Color.TextMuted,
        });

        ui.Sparkline(item.History, Size.Fill, 42f, Color.For(ToneOf(item)));

        ui.Separator();

        Fact(ui, "Speed", item.Rate > 0 ? Format.Rate(item.Rate) : "—");
        Fact(ui, "Time left", item.Status == DownloadStatus.Completed ? "Done" : Format.Eta(item));
        Fact(ui, "Peers", PeerText(item));
        Fact(ui, "Type", item.Kind == DownloadKind.Torrent
            ? item.FileCount > 0
                ? $"BitTorrent · {Format.Files(item.FileCount)}"
                : "BitTorrent"
            : item.Connections > 1 ? $"HTTP · {item.Connections} connections" : "HTTP");

        if (item.Kind == DownloadKind.Torrent && item.InfoHash.Length > 0)
            Fact(ui, "Info hash", Format.ShortHash(item.InfoHash), item.InfoHash);

        if (!dialog && item.Kind == DownloadKind.Torrent && item.Tracker.Length > 0)
            Fact(ui, "Tracker", item.Tracker);

        Fact(ui, "Folder", Format.Leaf(item.SaveDirectory), item.SaveDirectory);

        if (!dialog)
        {
            Fact(ui, "Added", Format.Ago(item.AddedAt));

            if (item.Status == DownloadStatus.Completed && item.CompletedAt is { } completed)
                Fact(ui, "Finished", Format.Ago(completed));
        }

        if (!dialog && height >= 660f && item.Kind == DownloadKind.Http)
            Fact(ui, "Source", Format.Host(item.Source), item.Source);

        if (actions)
        {
            ui.Separator();
            Actions(ui, item, dialog);
        }
    }

    private void Actions(Ui ui, DownloadItem item, bool dialog)
    {
        var full = !dialog;

        if (item.Status == DownloadStatus.Completed)
        {
            if (ui.Button("Show in Explorer", new ButtonOpts
            {
                Icon = Icons.FolderOpen,
                FullWidth = full,
                Variant = ButtonVariant.Secondary,
            }))
            {
                Shell.Reveal(item.SavePath);
            }
        }
        else if (item.Status == DownloadStatus.Failed)
        {
            if (ui.Button("Try again", new ButtonOpts
            {
                Icon = Icons.RotateCcw,
                FullWidth = full,
                Variant = ButtonVariant.Primary,
            }))
            {
                _downloads.Toggle(item);
            }
        }
        else if (ui.Button(item.UserPaused ? "Resume" : "Pause", new ButtonOpts
        {
            Icon = item.UserPaused ? Icons.Play : Icons.Pause,
            FullWidth = full,
            Variant = ButtonVariant.Secondary,
        }))
        {
            _downloads.Toggle(item);
        }

        if (ui.Button(dialog ? "Remove" : "Remove from list", new ButtonOpts
        {
            Icon = Icons.Trash2,
            FullWidth = full,
            Variant = ButtonVariant.Ghost,
            Color = Color.Danger,
        }))
        {
            _downloads.Remove(item);
            if (dialog) _detailsOpen = false;
        }
    }

    private void DetailDialog(Ui ui, float height)
    {
        if (!_detailsOpen) return;

        var item = Find();
        if (item is null)
        {
            _detailsOpen = false;
            return;
        }

        using var dialog = ui.Dialog(item.Name, ref _detailsOpen, new DialogOpts
        {
            Description = "Download details",
            Width = 460,
        });

        if (dialog.Open)
        {
            using (ui.Scroll(new ScrollOpts
            {
                Id = "dialog-details",
                Width = Size.Fill,
                Height = Size.Px(Math.Clamp(height - 300f, 220f, 520f)),
                Gap = 10,
                FadeEdges = true,
            }))
            using (ui.Column(new Box { Gap = 10, Width = Size.Fill }))
            {
                DetailBody(ui, item, height, dialog: true, actions: false);
            }

            using (ui.DialogFooter())
            {
                Actions(ui, item, dialog: true);
                if (ui.Button("Close")) _detailsOpen = false;
            }
        }
    }

    private void Footer(Ui ui, float width)
    {
        var completed = _downloads.Items.Count(item => item.Status == DownloadStatus.Completed);

        using (ui.Row(new Box { Width = Size.Fill, Align = Align.Center, Gap = 8 }))
        {
            ui.Text($"{Format.Downloads(_downloads.Items.Count)} · {completed} complete", new TextOpts
            {
                Size = 10,
                Color = Color.TextMuted,
            });

            ui.Spacer();

            if (width >= 760f && _downloads.DhtNodes > 0)
            {
                ui.Text($"DHT {Format.Number(_downloads.DhtNodes)} nodes", new TextOpts
                {
                    Size = 10,
                    Color = Color.TextMuted,
                });
            }

            FreeSpace();

            if (_free is { } free)
            {
                ui.Text($"{Format.Size(free)} free", new TextOpts
                {
                    Size = 10,
                    Color = Color.TextMuted,
                    Tabular = true,
                });
            }
        }
    }

    private void FreeSpace()
    {
        var now = Stopwatch.GetTimestamp();
        if (_freeFor == _downloads.Destination && now - _freeAt < Stopwatch.Frequency * 5) return;

        _freeFor = _downloads.Destination;
        _freeAt = now;
        _free = Format.FreeSpace(_downloads.Destination);
    }

    private void Notices(Ui ui)
    {
        while (_downloads.TryTakeCompleted(out var name))
        {
            ui.Toast("Download complete", new ToastOpts
            {
                Description = name,
                Tone = Tone.Success,
                Duration = 6,
            });
        }
    }

    private void OpenPicker()
    {
        _picked = _downloads.Destination;
        _picking = true;
    }

    private void Picker(Ui ui)
    {
        if (ui.FilePicker("destination", ref _picking, ref _picked, 1024, new FilePickerOpts
        {
            Folders = true,
            Title = "Choose download folder",
            Start = _downloads.Destination,
        }))
        {
            _downloads.SetDestination(_picked);
            _free = null;
        }
    }

    private static void Fact(Ui ui, string label, string value, string? tooltip = null)
    {
        using (ui.Row(new Box { Width = Size.Fill, Align = Align.Center, Gap = 10 }))
        {
            ui.Text(label, new TextOpts
            {
                Width = Size.Px(76),
                Size = 10,
                Color = Color.TextMuted,
            });

            ui.Text(value, new TextOpts
            {
                Width = Size.Fill,
                Size = 11,
                NoWrap = true,
                Color = Color.TextSecondary,
            });

            if (tooltip is not null) ui.Tooltip(tooltip);
        }
    }

    private static string StatusText(DownloadItem item) => item.Status switch
    {
        DownloadStatus.Queued => "Queued",
        DownloadStatus.Connecting => item.Kind == DownloadKind.Torrent ? "Connecting" : "Resolving",
        DownloadStatus.Metadata => "Metadata",
        DownloadStatus.Downloading => "Downloading",
        DownloadStatus.Checking => "Checking",
        DownloadStatus.Paused => "Paused",
        DownloadStatus.Completed => "Completed",
        _ => "Failed",
    };

    private static string DetailLine(DownloadItem item) => item.Status switch
    {
        DownloadStatus.Failed => item.Error ?? "The download stopped unexpectedly.",
        DownloadStatus.Completed => $"Finished {Format.Ago(item.CompletedAt ?? item.AddedAt)}",
        DownloadStatus.Metadata => "Fetching the torrent metadata from peers…",
        DownloadStatus.Connecting => item.Kind == DownloadKind.Torrent
            ? "Contacting trackers and the DHT…"
            : "Asking the server for the file size…",
        DownloadStatus.Checking => "Checking the files already on disk…",
        DownloadStatus.Paused => "Paused · resume whenever you like",
        DownloadStatus.Queued => "Waiting to start…",
        _ => item.Kind == DownloadKind.Torrent ? "BitTorrent · magnet link" : "HTTP file",
    };

    private static string MetaText(DownloadItem item) => item.Status switch
    {
        DownloadStatus.Completed =>
            $"{Format.Size(item.TotalBytes)} · completed {Format.Ago(item.CompletedAt ?? item.AddedAt)}",
        DownloadStatus.Failed =>
            $"Failed · {item.Error ?? "unknown error"}",
        DownloadStatus.Paused =>
            $"{Format.Size(item.DoneBytes)} of {Format.Size(item.TotalBytes)} · paused",
        DownloadStatus.Metadata =>
            $"{Format.Size(item.TotalBytes)} · fetching metadata · {Format.Number(item.Peers)} peers",
        DownloadStatus.Connecting =>
            item.Kind == DownloadKind.Torrent ? "Contacting trackers…" : "Resolving link…",
        DownloadStatus.Checking =>
            "Checking existing data…",
        DownloadStatus.Queued =>
            "Queued",
        _ => DownloadText(item),
    };

    private static string DownloadText(DownloadItem item)
    {
        var size = item.TotalBytes > 0
            ? $"{Format.Size(item.DoneBytes)} of {Format.Size(item.TotalBytes)}"
            : Format.Size(item.DoneBytes);

        var rate = item.Rate > 0 ? $" · {Format.Rate(item.Rate)}" : "";
        var peers = item.Kind == DownloadKind.Torrent && item.Peers > 0
            ? $" · {Format.Number(item.Peers)} peers"
            : "";
        var eta = item.Rate > 0 ? $" · {Format.Eta(item)}" : "";

        return size + rate + peers + eta;
    }

    private static string PeerText(DownloadItem item)
    {
        if (item.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Paused)
            return "—";

        if (item.Kind == DownloadKind.Http) return $"{Math.Max(1, item.Connections)} connections";
        if (item.Peers == 0 && item.Seeds == 0) return "Waiting for peers";

        return $"{Format.Number(item.Peers)} connected · {Format.Number(item.Seeds)} seeds";
    }

    private static Tone ToneOf(DownloadItem item) => item.Status switch
    {
        DownloadStatus.Completed => Tone.Success,
        DownloadStatus.Failed => Tone.Danger,
        DownloadStatus.Paused => Tone.Neutral,
        DownloadStatus.Connecting or DownloadStatus.Metadata or DownloadStatus.Checking =>
            Tone.Info,
        _ => Tone.Accent,
    };
}

// ---------------------------------------------------------------------------
// The downloads themselves: one list, one BitTorrent engine, one tick.
// ---------------------------------------------------------------------------

internal sealed class DownloadManager : IAsyncDisposable
{
    private const int HistoryLength = 60;
    private static readonly long SaveEvery = Stopwatch.Frequency * 5;

    private readonly SavedState _state;
    private readonly List<DownloadItem> _items = [];
    private readonly Queue<string> _completed = new();

    private readonly ClientEngine? _engine;
    private readonly string? _engineError;
    private readonly TorrentSettings _torrentSettings;

    private long _lastSample;
    private long _lastSave;
    private bool _dirty;
    private bool _closed;

    public DownloadManager(SavedState state)
    {
        _state = state;
        Destination = state.Destination;

        var builder = new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(StateStore.Home, "cache"),
            MaximumConnections = 300,
            MaximumHalfOpenConnections = 16,
            MaximumOpenFiles = 256,
            DiskCacheBytes = 16 << 20,
            ConnectionTimeout = TimeSpan.FromSeconds(10),
            AllowPortForwarding = false,
            AllowLocalPeerDiscovery = true,
            AutoSaveLoadDhtCache = true,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true,
        };

        _torrentSettings = new TorrentSettingsBuilder
        {
            MaximumConnections = 120,
            UploadSlots = 4,
            CreateContainingDirectory = true,
            AllowDht = true,
            AllowPeerExchange = true,
        }.ToSettings();

        try
        {
            _engine = new ClientEngine(builder.ToSettings());
        }
        catch (Exception error)
        {
            _engineError = error.Message;
        }

        foreach (var saved in state.Items)
        {
            var item = DownloadItem.Restore(saved);
            if (item is not null) _items.Add(item);
        }

        _items.Sort(static (a, b) => b.AddedAt.CompareTo(a.AddedAt));

        var now = Stopwatch.GetTimestamp();
        _lastSample = now;
        _lastSave = now;
    }

    public string Destination { get; private set; }

    public List<DownloadItem> Items => _items;

    public bool IsBusy => _items.Exists(static item => item.IsActive);

    public int DhtNodes
    {
        get
        {
            try
            {
                return _engine?.Dht?.NodeCount ?? 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    public void SetDestination(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var full = Format.FullPath(path);
        if (full is null) return;

        Destination = full;
        _state.Destination = full;
        _dirty = true;
    }

    public bool TryAdd(string source, out DownloadItem? item, out string error)
    {
        item = null;
        error = "";
        source = source.Trim();

        if (source.Length == 0)
        {
            error = "Paste a magnet link or an HTTP URL first.";
            return false;
        }

        foreach (var existing in _items)
        {
            if (string.Equals(existing.Source, source, StringComparison.OrdinalIgnoreCase))
            {
                item = existing;
                error = "That download is already in the list.";
                return false;
            }
        }

        DownloadKind kind;
        string name;
        var infoHash = "";
        var tracker = "";
        var total = 0L;

        if (source.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
        {
            if (!MagnetLink.TryParse(source, out var magnet) || magnet is null)
            {
                error = "That magnet link is not valid.";
                return false;
            }

            kind = DownloadKind.Torrent;
            name = Format.Display(magnet.Name, "Magnet download");
            infoHash = magnet.InfoHashes.V1OrV2?.ToHex() ?? "";
            tracker = Format.Host(magnet.AnnounceUrls.FirstOrDefault() ?? "");
            total = magnet.Size ?? 0;
        }
        else if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
                 && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            kind = DownloadKind.Http;
            name = Format.FromUrl(uri);
        }
        else
        {
            error = "Use an http(s) link or a magnet link.";
            return false;
        }

        var directory = Destination;
        if (!Format.TryCreateFolder(directory, out error)) return false;

        item = new DownloadItem
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Source = source,
            Name = name,
            Kind = kind,
            SaveDirectory = directory,
            SavePath = Path.Combine(directory, Format.SafeName(name)),
            AddedAt = DateTime.Now,
            TotalBytes = total,
            InfoHash = infoHash,
            Tracker = tracker,
            Status = DownloadStatus.Queued,
            UserPaused = false,
            History = new float[HistoryLength],
        };

        _items.Insert(0, item);
        _dirty = true;
        Start(item);
        return true;
    }

    public void Toggle(DownloadItem item)
    {
        if (item.Status == DownloadStatus.Completed) return;

        if (item.Status is DownloadStatus.Paused or DownloadStatus.Failed) Resume(item);
        else Pause(item);
    }

    public void Pause(DownloadItem item)
    {
        if (item.Status == DownloadStatus.Completed) return;

        item.UserPaused = true;
        item.Status = DownloadStatus.Paused;
        item.Rate = 0;
        _dirty = true;

        if (item.Torrent is { } manager) _ = PauseTorrentAsync(manager);
        item.Http?.Pause();
    }

    public void Resume(DownloadItem item)
    {
        if (item.Status == DownloadStatus.Completed) return;

        item.UserPaused = false;
        item.Error = null;
        _dirty = true;

        if (item.Torrent is { } manager)
        {
            item.Status = DownloadStatus.Queued;
            _ = ResumeTorrentAsync(manager);
        }
        else if (item.Http is { } http)
        {
            item.Status = DownloadStatus.Downloading;
            http.Resume();
        }
        else
        {
            Start(item);
        }
    }

    public void Remove(DownloadItem item)
    {
        item.Removed = true;
        _items.Remove(item);
        _dirty = true;

        if (item.Torrent is { } manager) _ = ForgetAsync(manager);
        item.Http?.Dispose();
    }

    private void Start(DownloadItem item)
    {
        item.Status = DownloadStatus.Connecting;
        item.Error = null;

        if (item.Kind == DownloadKind.Torrent) _ = StartTorrentAsync(item);
        else StartHttp(item);
    }

    private static void StartHttp(DownloadItem item)
    {
        var job = new HttpJob(item.Source, item.SaveDirectory);
        item.Http = job;
        job.Start();
    }

    private async Task StartTorrentAsync(DownloadItem item)
    {
        if (_engine is null)
        {
            item.Status = DownloadStatus.Failed;
            item.Error = _engineError ?? "The BitTorrent engine could not start.";
            return;
        }

        try
        {
            var magnet = MagnetLink.Parse(item.Source);
            var manager = await _engine.AddAsync(magnet, item.SaveDirectory, _torrentSettings).ConfigureAwait(false);

            item.Torrent = manager;
            item.InfoHash = manager.InfoHashes.V1OrV2?.ToHex() ?? item.InfoHash;

            if (item.Removed)
            {
                await ForgetAsync(manager).ConfigureAwait(false);
                return;
            }

            if (item.UserPaused)
            {
                await manager.PauseAsync().ConfigureAwait(false);
                return;
            }

            await manager.StartAsync().ConfigureAwait(false);

            // A pause pressed while AddAsync was still registering arrives here.
            if (item.UserPaused) await manager.PauseAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (item.Removed) return;

            item.Status = DownloadStatus.Failed;
            item.Error = error.Message;
        }
    }

    private static async Task ResumeTorrentAsync(TorrentManager manager)
    {
        try
        {
            await manager.StartAsync().ConfigureAwait(false);
        }
        catch
        {
            // The tick reports whatever state the manager lands in.
        }
    }

    private static async Task PauseTorrentAsync(TorrentManager manager)
    {
        try
        {
            await manager.PauseAsync().ConfigureAwait(false);
        }
        catch
        {
            // A manager that has already stopped is fine.
        }
    }

    private async Task ForgetAsync(TorrentManager manager)
    {
        try
        {
            if (manager.State != TorrentState.Stopped)
                await manager.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            if (_engine is not null)
                await _engine.RemoveAsync(manager, RemoveMode.KeepAllData).ConfigureAwait(false);
        }
        catch
        {
            // Removing a download must never take the list down with it.
        }
    }

    /// <summary>Pull the engine and the HTTP jobs into the list; sample speed once a second.</summary>
    public void Tick()
    {
        var now = Stopwatch.GetTimestamp();
        var sample = now - _lastSample >= Stopwatch.Frequency;

        foreach (var item in _items) Refresh(item);

        if (sample)
        {
            var elapsed = Math.Max(0.001, (now - _lastSample) / (double)Stopwatch.Frequency);

            foreach (var item in _items)
            {
                if (item.Kind == DownloadKind.Http && item.Status == DownloadStatus.Downloading)
                {
                    var delta = Math.Max(0L, item.DoneBytes - item.LastDone);
                    item.Rate = (long)(delta / elapsed);
                }

                item.LastDone = item.DoneBytes;
                Push(item, item.Rate);
            }

            _lastSample = now;
        }

        if (_dirty && now - _lastSave >= SaveEvery) Save();
    }

    private void Refresh(DownloadItem item)
    {
        if (item.Http is { } http)
        {
            item.Name = http.Name;
            item.TotalBytes = http.Total;
            item.DoneBytes = Math.Min(http.Done, http.Total > 0 ? http.Total : http.Done);
            item.SavePath = http.SavePath;
            item.Error = http.Error;
            item.Status = http.Status;
            item.Connections = http.Status == DownloadStatus.Downloading ? http.Connections : 0;

            if (item.Status == DownloadStatus.Completed) Finish(item);
            return;
        }

        if (item.Torrent is not { } manager) return;

        var total = manager.Torrent?.Size ?? manager.MagnetLink?.Size ?? item.TotalBytes;
        if (total > 0) item.TotalBytes = total;

        var name = manager.Torrent?.Name ?? manager.Name;
        if (!string.IsNullOrWhiteSpace(name)) item.Name = name;

        if (manager.HasMetadata && manager.Torrent is { } torrent)
        {
            item.FileCount = torrent.Files.Count;
            item.InfoHash = manager.InfoHashes.V1OrV2?.ToHex() ?? item.InfoHash;

            if (manager.Files.Count == 1 && manager.Files[0].FullPath.Length > 0)
                item.SavePath = manager.Files[0].FullPath;
            else
                item.SavePath = manager.ContainingDirectory ?? manager.SavePath;
        }

        item.DoneBytes = (long)Math.Clamp(manager.Progress * item.TotalBytes, 0, item.TotalBytes);
        item.Rate = manager.Monitor.DownloadRate;
        item.UploadRate = manager.Monitor.UploadRate;
        item.Peers = manager.OpenConnections;
        item.Seeds = manager.Peers.Seeds;
        item.Error = manager.Error?.Exception?.Message ?? manager.Error?.Reason.ToString();

        if (item.Status != DownloadStatus.Completed)
            item.Status = Map(manager, item.UserPaused);

        if (manager.Complete && item.CompletedAt is null) Finish(item);
    }

    private static DownloadStatus Map(TorrentManager manager, bool paused)
    {
        if (paused) return DownloadStatus.Paused;

        return manager.State switch
        {
            TorrentState.Metadata or TorrentState.FetchingHashes => DownloadStatus.Metadata,
            TorrentState.Downloading => DownloadStatus.Downloading,
            TorrentState.Seeding => DownloadStatus.Completed,
            TorrentState.Hashing or TorrentState.HashingPaused => DownloadStatus.Checking,
            TorrentState.Starting or TorrentState.Stopping => DownloadStatus.Connecting,
            TorrentState.Paused or TorrentState.Stopped => DownloadStatus.Paused,
            TorrentState.Error => DownloadStatus.Failed,
            _ => DownloadStatus.Queued,
        };
    }

    private void Finish(DownloadItem item)
    {
        var first = item.CompletedAt is null;

        item.Status = DownloadStatus.Completed;
        item.CompletedAt ??= DateTime.Now;
        item.DoneBytes = item.TotalBytes;
        item.Rate = 0;
        item.UploadRate = 0;
        item.Peers = 0;
        item.Seeds = 0;
        item.Connections = 0;
        item.Error = null;
        item.UserPaused = false;

        if (!first) return;

        _dirty = true;
        _completed.Enqueue(item.Name);

        if (item.Torrent is { } manager) _ = StopTorrentAsync(manager);
    }

    private static async Task StopTorrentAsync(TorrentManager manager)
    {
        try
        {
            if (manager.State != TorrentState.Stopped)
                await manager.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // The data is already on disk; stopping is only about the network.
        }
    }

    private static void Push(DownloadItem item, long rate)
    {
        var history = item.History;
        Array.Copy(history, 1, history, 0, history.Length - 1);
        history[^1] = rate;
    }

    public bool TryTakeCompleted(out string name)
    {
        if (_completed.Count == 0)
        {
            name = "";
            return false;
        }

        name = _completed.Dequeue();
        return true;
    }

    public void Save()
    {
        _dirty = false;
        _lastSave = Stopwatch.GetTimestamp();
        StateStore.Save(_state, _items);
    }

    public async Task ShutdownAsync()
    {
        if (_closed) return;
        _closed = true;

        Save();

        foreach (var item in _items) item.Http?.Cancel();

        if (_engine is null) return;

        try
        {
            await _engine.StopAllAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // Closing the window wins over a tracker that will not answer.
        }

        _engine.Dispose();
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync().ConfigureAwait(false);
}

// ---------------------------------------------------------------------------
// One row's model. The engine and the HTTP job write into it; the frame reads.
// ---------------------------------------------------------------------------

internal enum DownloadKind
{
    Torrent,
    Http,
}

internal enum DownloadStatus
{
    Queued,
    Connecting,
    Metadata,
    Downloading,
    Checking,
    Paused,
    Completed,
    Failed,
}

internal sealed class DownloadItem
{
    public required string Id { get; init; }
    public required string Source { get; init; }
    public required string Name { get; set; }
    public required DownloadKind Kind { get; init; }
    public required string SaveDirectory { get; init; }
    public required float[] History { get; init; }
    public required DateTime AddedAt { get; init; }

    public string SavePath { get; set; } = "";
    public string InfoHash { get; set; } = "";
    public string Tracker { get; set; } = "";
    public string? Error { get; set; }
    public DateTime? CompletedAt { get; set; }

    public DownloadStatus Status { get; set; }
    public long TotalBytes { get; set; }
    public long DoneBytes { get; set; }
    public long Rate { get; set; }
    public long UploadRate { get; set; }
    public int Peers { get; set; }
    public int Seeds { get; set; }
    public int Connections { get; set; }
    public int FileCount { get; set; }
    public long LastDone { get; set; }

    public bool UserPaused { get; set; }
    public bool Removed { get; set; }

    public TorrentManager? Torrent { get; set; }
    public HttpJob? Http { get; set; }

    public bool IsActive => Status is
        DownloadStatus.Queued or
        DownloadStatus.Connecting or
        DownloadStatus.Metadata or
        DownloadStatus.Downloading or
        DownloadStatus.Checking;

    public double Progress => Status == DownloadStatus.Completed
        ? 1
        : TotalBytes <= 0
            ? 0
            : Math.Clamp(DoneBytes / (double)TotalBytes, 0, 1);

    public static DownloadItem? Restore(SavedItem saved)
    {
        if (saved.Source.Length == 0) return null;

        var completed = saved.Status == "completed"
                        || (saved.Total > 0 && saved.Done >= saved.Total);

        return new()
        {
            Id = saved.Id.Length > 0 ? saved.Id : Guid.NewGuid().ToString("N")[..12],
            Source = saved.Source,
            Name = saved.Name.Length > 0 ? saved.Name : "Download",
            Kind = saved.Kind == "torrent" ? DownloadKind.Torrent : DownloadKind.Http,
            SaveDirectory = saved.Directory,
            SavePath = saved.Path,
            AddedAt = saved.AddedAt == default ? DateTime.Now : saved.AddedAt,
            TotalBytes = saved.Total,
            DoneBytes = completed ? saved.Total : saved.Done,
            InfoHash = saved.InfoHash,
            Tracker = saved.Tracker,
            FileCount = saved.Files,
            CompletedAt = completed ? saved.CompletedAt ?? saved.AddedAt : null,
            Status = completed
                ? DownloadStatus.Completed
                : saved.Status == "failed"
                    ? DownloadStatus.Failed
                    : DownloadStatus.Paused,
            Error = saved.Error.Length > 0 ? saved.Error : null,
            UserPaused = !completed,
            History = new float[60],
        };
    }
}

// ---------------------------------------------------------------------------
// HTTP: probe the size, then fetch several byte ranges at once. Pause keeps
// the completed ranges, so resuming in the same session does not redownload.
// ---------------------------------------------------------------------------

internal sealed class HttpJob : IDisposable
{
    private const int Workers = 8;
    private const long ChunkSize = 4L << 20;
    private const int BufferSize = 64 << 10;

    private static readonly HttpClient Client = CreateClient();

    private readonly string _url;
    private readonly string _directory;
    private readonly ConcurrentQueue<int> _pending = new();

    private CancellationTokenSource _cancel = new();
    private FileStream? _file;
    private SafeFileHandle? _handle;
    private Task? _work;
    private bool[] _done = [];
    private bool _ranges;
    private bool _resumeWanted;
    private bool _disposed;
    private int _chunks;
    private long _doneBytes;
    private string _target = "";
    private string _part = "";

    public HttpJob(string url, string directory)
    {
        _url = url;
        _directory = directory;
        Name = Format.SafeName(Format.FromUrl(url));
        _target = Path.Combine(directory, Name);
    }

    public string Name { get; private set; }

    public long Total { get; private set; }

    public long Done => Interlocked.Read(ref _doneBytes);

    public DownloadStatus Status { get; private set; } = DownloadStatus.Queued;

    public string? Error { get; private set; }

    public string SavePath => _target;

    public int Connections => _ranges ? Math.Min(Workers, Math.Max(1, _chunks)) : 1;

    public void Start()
    {
        if (_disposed) return;

        Status = DownloadStatus.Connecting;
        _work = Task.Run(ProbeAsync);
        Watch(_work);
    }

    public void Pause()
    {
        if (Status == DownloadStatus.Completed) return;

        Status = DownloadStatus.Paused;
        _cancel.Cancel();
    }

    public void Resume()
    {
        if (_disposed || Status == DownloadStatus.Completed) return;

        if (_work is { IsCompleted: false })
        {
            _resumeWanted = true;
            Status = DownloadStatus.Connecting;
            return;
        }

        ResumeNow();
    }

    public void Cancel() => _cancel.Cancel();

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _cancel.Cancel();

        var work = _work;
        _ = Task.Run(async () =>
        {
            if (work is not null)
            {
                try
                {
                    await work.ConfigureAwait(false);
                }
                catch
                {
                    // Cancelled work has nothing left to report.
                }
            }

            _file?.Dispose();
            _file = null;
            Format.TryDelete(_part);
        });
    }

    private void ResumeNow()
    {
        if (_disposed) return;

        _cancel = new CancellationTokenSource();
        Status = DownloadStatus.Connecting;

        if (_file is not null && _ranges)
        {
            Recount();
            Status = DownloadStatus.Downloading;
            _work = Task.Run(SegmentLoopAsync);
        }
        else
        {
            _work = Task.Run(ProbeAsync);
        }

        Watch(_work);
    }

    private void Watch(Task work)
    {
        _ = work.ContinueWith(_ =>
        {
            if (_resumeWanted && !_disposed)
            {
                _resumeWanted = false;
                ResumeNow();
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task ProbeAsync()
    {
        try
        {
            Status = DownloadStatus.Connecting;

            using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, _url);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));

            using var response = await Client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, _cancel.Token).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var name = Format.FileName(response);
            if (name is { Length: > 0 }) Name = Format.SafeName(name);

            var range = response.Content.Headers.ContentRange;
            var total = range?.Length ?? response.Content.Headers.ContentLength ?? 0;

            _ranges = response.StatusCode == HttpStatusCode.PartialContent && range?.Length is > 0;

            if (_disposed) return;

            if (_ranges && total > 0)
            {
                Open(total);
                await SegmentLoopAsync().ConfigureAwait(false);
            }
            else
            {
                await SingleAsync(response.Content.Headers.ContentLength ?? 0).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            if (Status != DownloadStatus.Completed) Status = DownloadStatus.Paused;
        }
        catch (Exception error)
        {
            Fail(error.Message);
        }
    }

    private async Task SingleAsync(long total)
    {
        _ranges = false;
        Total = total;
        Status = DownloadStatus.Downloading;

        Format.TryCreateFolder(_directory, out _);
        _target = Format.UniqueFinal(Path.Combine(_directory, Name));
        _part = _target + ".part";

        try
        {
            using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, _url);
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));

            using var response = await Client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, _cancel.Token).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            if (Total <= 0) Total = response.Content.Headers.ContentLength ?? 0;

            var input = await response.Content.ReadAsStreamAsync(_cancel.Token).ConfigureAwait(false);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

            try
            {
                await using var output = new FileStream(
                    _part, FileMode.Create, FileAccess.Write, FileShare.Read,
                    BufferSize, FileOptions.Asynchronous);

                while (true)
                {
                    var read = await input.ReadAsync(buffer, _cancel.Token).ConfigureAwait(false);
                    if (read <= 0) break;

                    await output.WriteAsync(buffer.AsMemory(0, read), _cancel.Token).ConfigureAwait(false);
                    Interlocked.Add(ref _doneBytes, read);
                }

                await output.FlushAsync(_cancel.Token).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                input.Dispose();
            }

            Finish();
        }
        catch (OperationCanceledException)
        {
            Status = DownloadStatus.Paused;
        }
        catch (Exception error)
        {
            Fail(error.Message);
            Format.TryDelete(_part);
        }
    }

    private void Open(long total)
    {
        Total = total;
        Status = DownloadStatus.Downloading;

        Format.TryCreateFolder(_directory, out _);
        _target = Format.UniqueFinal(Path.Combine(_directory, Name));
        _part = _target + ".part";

        _file = new FileStream(
            _part, FileMode.Create, FileAccess.Write, FileShare.Read, 1, FileOptions.RandomAccess);
        _file.SetLength(total);
        _handle = _file.SafeFileHandle;

        _chunks = (int)((total + ChunkSize - 1) / ChunkSize);
        _done = new bool[_chunks];
        _pending.Clear();

        for (var i = 0; i < _chunks; i++) _pending.Enqueue(i);
    }

    private async Task SegmentLoopAsync()
    {
        while (true)
        {
            await RunWorkersAsync().ConfigureAwait(false);

            if (_cancel.IsCancellationRequested)
            {
                if (_resumeWanted && !_disposed)
                {
                    _resumeWanted = false;
                    _cancel = new CancellationTokenSource();
                    Recount();
                    Status = DownloadStatus.Downloading;
                    continue;
                }

                Status = DownloadStatus.Paused;
                return;
            }

            break;
        }

        if (_done.Length > 0 && Array.TrueForAll(_done, static done => done)) Finish();
        else Fail(Error ?? "The server stopped sending data.");
    }

    private Task RunWorkersAsync()
    {
        var count = Math.Max(1, Math.Min(Workers, _chunks));
        var token = _cancel.Token;
        var workers = new Task[count];

        for (var i = 0; i < count; i++) workers[i] = Task.Run(() => WorkerAsync(token), token);

        return Task.WhenAll(workers);
    }

    private async Task WorkerAsync(CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            while (!token.IsCancellationRequested && _pending.TryDequeue(out var index))
            {
                if (_done[index]) continue;

                var start = index * ChunkSize;
                var end = Math.Min(start + ChunkSize, Total) - 1;
                var attempts = 0;
                var ok = false;

                while (!ok && !token.IsCancellationRequested)
                {
                    try
                    {
                        using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, _url);
                        request.Headers.Range = new RangeHeaderValue(start, end);
                        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));

                        using var response = await Client.SendAsync(
                            request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

                        response.EnsureSuccessStatusCode();

                        if (response.StatusCode != HttpStatusCode.PartialContent)
                            throw new IOException("The server ignored the range request.");

                        var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                        var handle = _handle;

                        try
                        {
                            var offset = start;

                            while (offset <= end)
                            {
                                var want = (int)Math.Min(buffer.Length, end - offset + 1);
                                var read = await stream.ReadAsync(buffer.AsMemory(0, want), token)
                                    .ConfigureAwait(false);

                                if (read <= 0) break;

                                RandomAccess.Write(handle!, buffer.AsSpan(0, read), offset);
                                offset += read;
                                Interlocked.Add(ref _doneBytes, read);
                            }

                            if (offset <= end) throw new IOException("The connection closed early.");
                        }
                        finally
                        {
                            stream.Dispose();
                        }

                        _done[index] = true;
                        ok = true;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception error) when (++attempts < 3)
                    {
                        _ = error;
                        await Task.Delay(200 * attempts, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        Error ??= error.Message;
                    }
                }

                if (!ok)
                {
                    if (token.IsCancellationRequested) _pending.Enqueue(index);
                    else Error ??= "A part of the file could not be downloaded.";
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void Recount()
    {
        var done = 0L;

        for (var i = 0; i < _chunks; i++)
        {
            if (_done[i]) done += Math.Min(ChunkSize, Total - i * ChunkSize);
        }

        Interlocked.Exchange(ref _doneBytes, done);
    }

    private void Finish()
    {
        if (Status == DownloadStatus.Completed) return;

        try
        {
            _file?.Dispose();
            _file = null;

            if (File.Exists(_part))
            {
                _target = Format.UniqueFinal(_target);
                File.Move(_part, _target);
            }

            if (Total <= 0) Total = Done;

            Interlocked.Exchange(ref _doneBytes, Total);
            Status = DownloadStatus.Completed;
            Error = null;
        }
        catch (Exception error)
        {
            Fail(error.Message);
        }
    }

    private void Fail(string message)
    {
        Error = message;
        Status = DownloadStatus.Failed;
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 16,
        };

        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CuiDownload/1.0");
        return client;
    }
}

// ---------------------------------------------------------------------------
// State: a small JSON file beside the cache. Manual reads and writes keep the
// app free of reflection-based serializers under Native AOT.
// ---------------------------------------------------------------------------

internal sealed class SavedState
{
    public string Destination { get; set; } = StateStore.DefaultDestination();

    public List<SavedItem> Items { get; } = [];
}

internal sealed class SavedItem
{
    public string Id = "";
    public string Source = "";
    public string Name = "";
    public string Directory = "";
    public string Path = "";
    public string Kind = "http";
    public string Status = "paused";
    public string InfoHash = "";
    public string Tracker = "";
    public string Error = "";
    public DateTime AddedAt;
    public DateTime? CompletedAt;
    public long Total;
    public long Done;
    public int Files;
}

internal static class StateStore
{
    private static readonly string Folder = StateFolder();

    private static readonly string StatePath = Path.Combine(Folder, "state.json");

    public static string Home => Folder;

    private static string StateFolder()
    {
        if (Environment.GetEnvironmentVariable("CUIDOWNLOAD_HOME") is { Length: > 0 } custom)
            return Format.FullPath(custom) ?? custom;

        if (Environment.GetEnvironmentVariable("CUISHARP_STATE_HOME") is { Length: > 0 } root)
            return Path.Combine(root, "CuiDownload");

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CuiDownload");
    }

    public static string DefaultDestination()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) home = Environment.CurrentDirectory;
        return Path.Combine(home, "Downloads");
    }

    public static SavedState Load()
    {
        var state = new SavedState();

        try
        {
            if (!File.Exists(StatePath)) return state;

            using var json = JsonDocument.Parse(File.ReadAllBytes(StatePath));
            var root = json.RootElement;

            if (Str(root, "destination") is { Length: > 0 } destination
                && Format.FullPath(destination) is { } full)
            {
                state.Destination = full;
            }

            if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return state;

            foreach (var element in items.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;

                var source = Str(element, "source");
                if (source.Length == 0) continue;

                var saved = new SavedItem
                {
                    Id = Str(element, "id"),
                    Source = source,
                    Name = Str(element, "name"),
                    Directory = Str(element, "directory"),
                    Path = Str(element, "path"),
                    Kind = Str(element, "kind"),
                    Status = Str(element, "status"),
                    InfoHash = Str(element, "hash"),
                    Tracker = Str(element, "tracker"),
                    Error = Str(element, "error"),
                    AddedAt = Time(element, "added"),
                    CompletedAt = NullableTime(element, "completed"),
                    Total = Number(element, "total"),
                    Done = Number(element, "done"),
                    Files = (int)Number(element, "files"),
                };

                if (saved.Directory.Length == 0) saved.Directory = state.Destination;
                state.Items.Add(saved);
            }
        }
        catch
        {
            // A state file from an older build is worth less than a clean start.
        }

        return state;
    }

    public static void Save(SavedState state, IReadOnlyList<DownloadItem> items)
    {
        try
        {
            Directory.CreateDirectory(Folder);

            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();
                json.WriteNumber("v", 1);
                json.WriteString("destination", state.Destination);

                json.WriteStartArray("items");
                foreach (var item in items)
                {
                    json.WriteStartObject();
                    json.WriteString("id", item.Id);
                    json.WriteString("source", item.Source);
                    json.WriteString("name", item.Name);
                    json.WriteString("directory", item.SaveDirectory);
                    json.WriteString("path", item.SavePath);
                    json.WriteString("kind", item.Kind == DownloadKind.Torrent ? "torrent" : "http");
                    json.WriteString("status", SaveStatus(item));
                    json.WriteString("hash", item.InfoHash);
                    json.WriteString("tracker", item.Tracker);
                    json.WriteString("error", item.Error ?? "");
                    json.WriteString("added", item.AddedAt.ToString("O", CultureInfo.InvariantCulture));
                    if (item.CompletedAt is { } completed)
                        json.WriteString("completed", completed.ToString("O", CultureInfo.InvariantCulture));
                    json.WriteNumber("total", item.TotalBytes);
                    json.WriteNumber("done", item.DoneBytes);
                    json.WriteNumber("files", item.FileCount);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            var temporary = StatePath + ".tmp";
            File.WriteAllBytes(temporary, buffer.ToArray());
            File.Move(temporary, StatePath, true);
        }
        catch
        {
            // Losing the state file must never take the downloads down with it.
        }
    }

    private static string SaveStatus(DownloadItem item) => item.Status switch
    {
        DownloadStatus.Completed => "completed",
        DownloadStatus.Failed => "failed",
        DownloadStatus.Paused => "paused",
        _ => "active",
    };

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : 0;

    private static DateTime Time(JsonElement element, string name) =>
        DateTime.TryParse(
            Str(element, name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)
            ? time
            : default;

    private static DateTime? NullableTime(JsonElement element, string name) =>
        DateTime.TryParse(
            Str(element, name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)
            ? time
            : null;
}

// ---------------------------------------------------------------------------
// Small helpers: sizes, times, names and the one process launch we need.
// ---------------------------------------------------------------------------

internal static class Format
{
    public static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Items(int count) => $"{Number(count)} {(count == 1 ? "item" : "items")}";

    public static string Files(int count) => $"{Number(count)} {(count == 1 ? "file" : "files")}";

    public static string Downloads(int count) =>
        $"{Number(count)} {(count == 1 ? "download" : "downloads")}";

    public static string Size(long bytes) => bytes switch
    {
        <= 0 => "0 B",
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.##} TB",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.##} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };

    public static string Rate(long bytes) => bytes <= 0 ? "0 B/s" : $"{Size(bytes)}/s";

    public static string Eta(DownloadItem item)
    {
        if (item.TotalBytes <= 0 || item.Rate <= 0) return "—";

        var seconds = Math.Max(0, item.TotalBytes - item.DoneBytes) / (double)item.Rate;
        if (seconds < 1) return "Done";
        if (seconds < 60) return $"{seconds:0}s";
        if (seconds < 3600) return $"{(int)(seconds / 60)}m {seconds % 60:0}s";
        if (seconds < 86400) return $"{(int)(seconds / 3600)}h {(int)(seconds % 3600 / 60)}m";
        return "> 1 day";
    }

    public static string Ago(DateTime time)
    {
        var delta = DateTime.Now - time;
        if (delta < TimeSpan.Zero || delta.TotalSeconds < 20) return "just now";
        if (delta.TotalMinutes < 1) return $"{(int)delta.TotalSeconds}s ago";
        if (delta.TotalHours < 1) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalDays < 1) return $"{(int)delta.TotalHours}h ago";
        if (delta.TotalDays < 30) return $"{(int)delta.TotalDays}d ago";
        return time.ToString("MMM d", CultureInfo.InvariantCulture);
    }

    public static string Leaf(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var leaf = Path.GetFileName(trimmed);
        return leaf.Length > 0 ? leaf : trimmed;
    }

    public static string Clip(string text, int length) =>
        text.Length <= length ? text : text[..(length - 1)] + "…";

    public static string Host(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    public static string ShortHash(string hash) =>
        hash.Length <= 16 ? hash : hash[..16] + "…";

    public static string Display(string? name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;

        var clean = name.ReplaceLineEndings(" ").Trim();
        return clean.Length > 0 ? clean : fallback;
    }

    public static string FromUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? FromUrl(uri) : "download";

    public static string FromUrl(Uri uri)
    {
        var name = Path.GetFileName(uri.LocalPath);
        if (!string.IsNullOrWhiteSpace(name)) return SafeName(Uri.UnescapeDataString(name));

        return SafeName(uri.Host + "-download");
    }

    public static string FileName(HttpResponseMessage response)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        var name = disposition?.FileNameStar ?? disposition?.FileName;
        if (string.IsNullOrWhiteSpace(name)) return "";

        name = name.Trim().Trim('"');
        if (name.StartsWith("UTF-8''", StringComparison.OrdinalIgnoreCase)) name = name[7..];
        return Uri.UnescapeDataString(name);
    }

    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var buffer = new char[name.Length];

        for (var i = 0; i < name.Length; i++)
            buffer[i] = Array.IndexOf(invalid, name[i]) >= 0 ? '_' : name[i];

        var clean = new string(buffer).Trim().TrimEnd('.');
        if (clean.Length == 0) clean = "download";

        if (clean.Length > 120)
        {
            var extension = Path.GetExtension(clean);
            var stem = Path.GetFileNameWithoutExtension(clean);
            var room = Math.Max(1, 120 - extension.Length);
            clean = stem[..Math.Min(stem.Length, room)] + extension;
        }

        return clean;
    }

    public static string UniqueFinal(string path)
    {
        if (!File.Exists(path)) return path;

        var directory = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var i = 1; i < 10_000; i++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }

        return path;
    }

    public static string? FullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    public static bool TryCreateFolder(string path, out string error)
    {
        error = "";

        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch (Exception exception)
        {
            error = "Cannot use that folder: " + exception.Message;
            return false;
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (path.Length > 0 && File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A locked partial file is not worth failing the app over.
        }
    }

    public static long? FreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return null;

            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch
        {
            return null;
        }
    }
}

internal static class Shell
{
    public static void Reveal(string path)
    {
        try
        {
            if (path.Length == 0) return;

            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return;
            }

            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true,
                });
                return;
            }

            var folder = Path.GetDirectoryName(path);
            if (folder is { Length: > 0 } && Directory.Exists(folder))
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch
        {
            // Explorer refusing to open is not a download failure.
        }
    }
}
