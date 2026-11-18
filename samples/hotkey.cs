#:package CuiSharp@*

// Hotkey — a minimal Windows hotkey manager.
//
//   dotnet run hotkey.cs
//   dotnet publish hotkey.cs -c Release -o .
//
// Bind a global hotkey to an application, optionally with arguments, a working directory and
// environment variables. A binding can capture the foreground application's selection first:
// selected text (Ctrl+C), the clipboard, or the files and folders selected in Explorer or on the
// desktop. The clipboard is restored after the capture.
//
// Windows has no public API that enumerates the hotkeys other processes register, so the app
// does the two things that are possible: it probes a combination by briefly registering it, and
// it lists and edits the hotkeys stored in .lnk shortcuts on the desktop and in the Start menu.
//
// Replace hotkey.ico to change the executable and the window icon.
#:property PublishAot=true
#:property OutputType=WinExe
#:property ApplicationIcon=hotkey.ico
#:property OptimizationPreference=Size
#:property InvariantGlobalization=true
#:property StripSymbols=true
#:property DebugType=none

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CuiSharp;

var app = new HotkeyApp();
try
{
    return CuiApp.Run(app.Options, app.Draw);
}
finally
{
    app.Dispose();
}

// ---------------------------------------------------------------------------
// The window: a list of bindings on the left, the editor on the right, and
// two more tabs for shortcut files and the activity log.
// ---------------------------------------------------------------------------

internal sealed class HotkeyApp : IDisposable
{
    private const string DiskExample =
        @"C:\Users\woo\Documents\Code\Slaveoftime\CuiSharp\samples\disk.exe";

    private static readonly string[] TabNames = ["Bindings", "Shortcuts", "Activity"];
    private static readonly string[] PayloadNames =
        ["Nothing", "Selected text", "Clipboard", "Selected files and folders"];

    private readonly bool _diskExampleExists = File.Exists(DiskExample);
    private readonly ActivityLog _log = new();
    private readonly HotkeyHost _host;
    private readonly List<Binding> _bindings = ConfigStore.Load();

    private ShortcutInfo[] _shortcuts = [];
    private bool _scanStarted;
    private volatile bool _scanning;

    private CuiWindow? _window;
    private TrayIcon? _tray;
    private nint _hwnd;
    private int _tab;
    private int _selected = -1;
    private Binding? _draft;
    private KeyCombo _combo;
    private string _message = "";
    private Tone _messageTone = Tone.Neutral;
    private string _probe = "";
    private Tone _probeTone = Tone.Neutral;

    public CuiAppOptions Options { get; }

    public HotkeyApp()
    {
        _host = new HotkeyHost(_log, () => _hwnd);
        Options = new CuiAppOptions
        {
            Title = "Hotkey",
            Width = 720,
            Height = 960,
            MinWidth = 600,
            MinHeight = 400,
            Theme = Theme.Dark,
            Accent = Accent.Rose,
            WindowCreated = window =>
            {
                _window = window;
                _hwnd = window.NativeWindow;
                _host.WakeWindow = _hwnd;
                _tray = new TrayIcon(_hwnd);
                if (!_tray.Visible) _log.Add("Tray", "Could not add the tray icon.", false);
            },
        };

        if (_bindings.Count > 0) Select(0);
        _host.SetBindings(_bindings);
    }

    public void Draw(Ui ui)
    {
        ui.UseSystemFontFallbacks();

        // A denser rhythm than the stock theme: tighter corners, small controls, and every default
        // control one size down. The tokens live in the theme, so no call site has to repeat them.
        ThemeStyle style = ui.Style;
        style.RadiusControl = 4;
        style.RadiusPanel = 4;
        style.RadiusSurface = 4;
        style.ControlMd = 30;
        style.ControlSm = 26;

        // Size.Fill resolves against the frame the app is drawing into, so it stays right across a
        // resize and at any DPI. ViewportSize does neither: it lags a resize by a frame.
        using (ui.Column(new Box
        {
            Width = Size.Fill,
            Height = Size.Fill,
        }))
        {
            DrawHeader(ui);
            ui.Separator();

            using (ui.Box(new Box { PadX = 16, Width = Size.Fill }))
            {
                ui.Tabs("tabs", TabNames, ref _tab, new TabsOpts { Variant = TabsVariant.Underline });
            }

            using (ui.Box(new Box { PadX = 16, PadTop = 10, PadBottom = 2, Width = Size.Fill, Height = Size.Fill }))
            {
                switch (_tab)
                {
                    case 0: DrawBindings(ui); break;
                    case 1: DrawShortcuts(ui); break;
                    default: DrawActivity(ui); break;
                }
            }

            DrawStatusBar(ui);
        }
    }

    private void DrawHeader(Ui ui)
    {
        using (ui.Row(new Box { PadX = 16, PadY = 10, Gap = 10, Align = Align.Center, Width = Size.Fill }))
        {
            using (ui.Column(new Box { Gap = 1 }))
            {
                ui.Heading("Hotkey", 3);
                ui.Muted("Global shortcuts for Windows");
            }

            ui.Spacer();

            int active = 0;
            int unavailable = 0;
            for (int i = 0; i < _bindings.Count; i++)
            {
                if (!_bindings[i].Enabled) continue;
                active++;
                string status = _host.Status(i);
                if (status is "Taken by another app" or "Registration failed") unavailable++;
            }

            ui.Muted($"{active} of {_bindings.Count} active");

            if (unavailable > 0)
                ui.Badge($"{unavailable} unavailable", new BadgeOpts
                {
                    Tone = Tone.Danger,
                    Size = ControlSize.Sm,
                    Dot = true,
                });

            if (ui.Button("New binding", new ButtonOpts { Variant = ButtonVariant.Primary })) AddBinding();
        }
    }

    private void DrawStatusBar(Ui ui)
    {
        LogEntry? latest = _log.Latest;

        ui.Separator();

        using (ui.Row(new Box { PadX = 16, PadY = 6, Gap = 8, Align = Align.Center, Width = Size.Fill }))
        {
            if (latest is { } entry)
            {
                ui.Badge(entry.Ok ? "ok" : "error", new BadgeOpts
                {
                    Tone = entry.Ok ? Tone.Success : Tone.Danger,
                    Size = ControlSize.Sm,
                    Dot = true,
                });
                ui.Text($"{entry.When:HH:mm:ss}", new TextOpts(12, Color.TextMuted));
                ui.Text(entry.Binding, new TextOpts(12, Color.TextSecondary));
                ui.Text(entry.Text, new TextOpts(12, Color.TextMuted) { NoWrap = true });
            }
            else
            {
                ui.Muted("No hotkey has run yet. Press one while another application is in front.");
            }

            ui.Spacer();

            if (latest is not null)
                ui.Text($"{_bindings.Count} binding{(_bindings.Count == 1 ? "" : "s")}",
                    new TextOpts(12, Color.TextMuted));
        }
    }

    // -----------------------------------------------------------------------
    // Bindings
    // -----------------------------------------------------------------------

    private void DrawBindings(Ui ui)
    {
        using (ui.Row(new Box { Gap = 14, Width = Size.Fill, Height = Size.Fill, Align = Align.Stretch }))
        {
            using (ui.Column(new Box { Width = Size.Px(196), Height = Size.Fill, Gap = 6 }))
            {
                using (ui.Row(new Box { Gap = 6, Align = Align.Center, Width = Size.Fill, PadX = 2 }))
                {
                    ui.Label("Bindings");
                    ui.Badge(_bindings.Count.ToString(), new BadgeOpts { Size = ControlSize.Sm, Tone = Tone.Neutral });
                    ui.Spacer();
                    if (ui.Button("+", new ButtonOpts
                    {
                        Variant = ButtonVariant.Ghost,
                        Size = ControlSize.Sm,
                        Tooltip = "New binding",
                    }))
                    {
                        AddBinding();
                    }
                }

                using (ui.Scroll(new ScrollOpts { Height = Size.Fill, Gap = 5 }))
                {
                    if (_bindings.Count == 0)
                    {
                        using (ui.Card(new Card { Pad = 12, Gap = 8, Width = Size.Fill }))
                        {
                            ui.Label("No bindings yet");
                            ui.Muted("Give an application a global hotkey and choose what it receives.");
                            if (ui.Button("New binding", new ButtonOpts
                            {
                                Variant = ButtonVariant.Primary,
                                Size = ControlSize.Sm,
                            }))
                            {
                                AddBinding();
                            }

                            if (_diskExampleExists && ui.Button("Add the disk.exe example", new ButtonOpts
                            {
                                Variant = ButtonVariant.Ghost,
                                Size = ControlSize.Sm,
                            }))
                            {
                                AddDiskExample();
                            }
                        }
                    }

                    for (int i = 0; i < _bindings.Count; i++)
                    {
                        Binding b = _bindings[i];
                        string status = _host.Status(i);
                        string combo = KeyMap.Text(b.Mods, (Key)b.Key);
                        string target = b.Command.Length > 0 ? Path.GetFileName(b.Command) : "";
                        if (target.Length == 0) target = b.Command;

                        using (ui.Id(i))
                        {
                            bool clicked;
                            using (ui.Card(new Card
                            {
                                PadX = 10,
                                PadY = 8,
                                Gap = 4,
                                Width = Size.Fill,
                                Hoverable = true,
                                Selected = i == _selected,
                                Muted = !b.Enabled,
                            }, out clicked))
                            {
                                using (ui.Row(new Box { Gap = 8, Align = Align.Center, Width = Size.Fill }))
                                {
                                    ui.Label(b.Name.Length > 0 ? b.Name : "Untitled");
                                    ui.Spacer();
                                    ui.Kbd(combo);
                                }

                                using (ui.Row(new Box { Gap = 6, Align = Align.Center, Width = Size.Fill }))
                                {
                                    ui.Badge(ShortStatus(status), new BadgeOpts
                                    {
                                        Tone = StatusTone(status),
                                        Size = ControlSize.Sm,
                                        Dot = true,
                                    });
                                    ui.Text(target.Length > 0 ? target : "no command",
                                        new TextOpts(12, Color.TextMuted) { NoWrap = true });
                                }
                            }

                            if (clicked) Select(i);
                        }
                    }
                }
            }

            using (ui.Box(new Box { Width = Size.Px(1), Height = Size.Fill, Bg = Color.Border })) { }

            // A scroll view grows to fit its content, so the pane takes the remaining width and the
            // cards inside it fill the scroll's viewport: otherwise a long paragraph or path
            // decides the width of the whole window.
            using (ui.Column(new Box { Width = Size.Fill, Height = Size.Fill }))
            {
                if (_selected >= 0 && _draft is not null) DrawEditor(ui);
                else DrawEmpty(ui);
            }
        }
    }

    private void DrawEmpty(Ui ui)
    {
        using (ui.Column(new Box
        {
            Gap = 10,
            Height = Size.Fill,
            Pad = 18,
            Justify = Justify.Center,
            Align = Align.Start,
            MaxWidth = 520,
        }))
        {
            ui.Heading("No binding selected", 3);
            ui.Secondary("A binding turns a global hotkey into a launch: an application plus the text, " +
                         "clipboard, or the files and folders selected in Explorer.");
            using (ui.Row(new Box { Gap = 8, PadTop = 6, Wrap = true }))
            {
                if (ui.Button("New binding", new ButtonOpts { Variant = ButtonVariant.Primary })) AddBinding();
                if (_diskExampleExists && ui.Button("Add the disk.exe example", new ButtonOpts
                {
                    Variant = ButtonVariant.Outline,
                }))
                {
                    AddDiskExample();
                }
            }
        }
    }

    private void DrawEditor(Ui ui)
    {
        Binding b = _draft!;
        bool dirty = IsDirty();
        string status = dirty ? "Unsaved" : _host.Status(_selected);

        using (ui.Column(new Box { Gap = 0, Width = Size.Fill, Height = Size.Fill }))
        {
            using (ui.Row(new Box { Gap = 10, Align = Align.Center, Width = Size.Fill, PadBottom = 8, PadX = 2 }))
            {
                ui.Heading(b.Name.Length > 0 ? b.Name : "Untitled binding", 3);
                ui.Badge(status, new BadgeOpts
                {
                    Tone = dirty ? Tone.Warning : StatusTone(status),
                    Size = ControlSize.Sm,
                    Dot = !dirty,
                });
                ui.Spacer();
                ui.Kbd(KeyMap.Text(b.Mods, (Key)b.Key));
            }

            using (ui.Scroll(new ScrollOpts { Height = Size.Fill, Gap = 0, Pad = 2, FadeEdges = true }))
            using (ui.Column(new Box { Width = Size.Fill, Gap = 8 }))
            {
                using (ui.Card(new Card { Pad = 12, Gap = 8, Width = Size.Fill }))
                {
                    ui.Label("Trigger");
                    using (ui.Row(new Box { Gap = 8, Align = Align.Center, Wrap = true, Width = Size.Fill }))
                    {
                        if (ui.ShortcutRecorder("edit.combo", ref _combo, new ShortcutRecorderOpts
                        {
                            Width = Size.Px(260),
                            Placeholder = "Click, then press keys",
                        }))
                        {
                            _probe = "";
                        }

                        if (ui.Button("Check", new ButtonOpts { Variant = ButtonVariant.Ghost })) Probe();
                        if (_probe.Length > 0)
                            ui.Badge(_probe, new BadgeOpts { Tone = _probeTone, Size = ControlSize.Sm });
                    }

                    ui.Muted("Check registers it briefly. Windows does not expose other applications' hotkeys.");
                }

                using (ui.Card(new Card { Pad = 12, Gap = 8, Width = Size.Fill }))
                {
                    ui.Label("Application");
                    ui.Input("edit.name", ref b.Name, new InputOpts
                    {
                        Label = "Name",
                        Width = Size.Grow(1),
                    });
                    ui.Input("edit.workdir", ref b.WorkDir, new InputOpts
                    {
                        Label = "Working directory",
                        Placeholder = "optional",
                        Width = Size.Grow(1),
                    });
                    ui.Input("edit.command", ref b.Command, new InputOpts
                    {
                        Label = "Command",
                        Width = Size.Fill,
                        Placeholder = @"notepad  or  C:\Tools\disk.exe",
                        Hint = "A name found on PATH, or a full path. Environment variables are expanded.",
                    });

                    using (ui.Row(new Box { Gap = 8, Wrap = true }))
                    {
                        if (ui.Button("Browse…", new ButtonOpts { Variant = ButtonVariant.Outline })) Browse();
                        if (_diskExampleExists && ui.Button("Use disk.exe", new ButtonOpts
                        {
                            Variant = ButtonVariant.Ghost,
                        }))
                        {
                            UseDiskExample();
                        }
                    }

                    int capture = (int)b.Capture;
                    if (ui.Select("edit.capture", PayloadNames, ref capture,
                            new SelectOpts { Label = "Capture before launch", Width = Size.Fill }))
                    {
                        b.Capture = (PayloadMode)capture;
                    }
                    ui.Muted(PayloadHint(b.Capture));

                    ui.TextArea("edit.args", ref b.Args, new InputOpts
                    {
                        Label = "Arguments",
                        Width = Size.Fill,
                        Height = 52,
                        Placeholder = "-n \"{text}\"",
                        Hint = "Tokens: {text} {clipboard} {files} {file} {dir}. A captured value with no token is appended.",
                    });

                    ui.TextArea("edit.env", ref b.Env, new InputOpts
                    {
                        Label = "Environment",
                        Width = Size.Fill,
                        Height = 52,
                        Placeholder = "KEY=value\nPATH+=C:\\Tools",
                        Hint = "One KEY=value per line. KEY+=value appends to the existing value.",
                    });
                }
                using (ui.Card(new Card { Pad = 12, Gap = 6, Width = Size.Fill }))
                {
                    ui.Label("Test");
                    ui.Muted("Reads the clipboard as it is now; the saved hotkey captures a live selection.");
                }
            }

            using (ui.Row(new Box { Gap = 8, Align = Align.Center, Width = Size.Fill, PadY = 10, PadX = 2 }))
            {
                if (_message.Length > 0)
                    ui.Badge(_message, new BadgeOpts { Tone = _messageTone, Size = ControlSize.Sm });
                else if (dirty)
                    ui.Muted("Unsaved changes");

                ui.Spacer();

                ui.Switch("Enabled", ref b.Enabled, new CheckOpts());
                ui.Gap(6);

                if (ui.Button("Test", new ButtonOpts { Variant = ButtonVariant.Ghost, Size = ControlSize.Sm }))
                {
                    TestDraft();
                }
                if (ui.Button("Delete", new ButtonOpts
                {
                    Variant = ButtonVariant.Ghost,
                    Color = Color.Danger,
                    Size = ControlSize.Sm,
                }))
                {
                    DeleteDraft();
                }
                if (ui.Button("Save", new ButtonOpts { Variant = ButtonVariant.Primary, Size = ControlSize.Sm }))
                {
                    SaveDraft();
                }
            }
        }
    }

    // -----------------------------------------------------------------------
    // Shortcut files: the hotkeys Windows stores in .lnk files
    // -----------------------------------------------------------------------

    private void DrawShortcuts(Ui ui)
    {
        if (!_scanStarted)
        {
            _scanStarted = true;
            ScanShortcuts();
        }

        using (ui.Column(new Box { Gap = 8, Width = Size.Fill, Height = Size.Fill }))
        {
            using (ui.Row(new Box { Gap = 8, Align = Align.Center, Width = Size.Fill, PadX = 2 }))
            {
                ui.Label("Shortcut hotkeys");
                if (_scanning)
                    ui.Badge("Scanning", new BadgeOpts { Tone = Tone.Info, Dot = true, Size = ControlSize.Sm });
                if (_message.Length > 0 && _tab == 1)
                    ui.Badge(_message, new BadgeOpts { Tone = _messageTone, Size = ControlSize.Sm });
                ui.Spacer();
                if (ui.Button("Rescan", new ButtonOpts { Variant = ButtonVariant.Outline, Size = ControlSize.Sm }))
                    ScanShortcuts();
            }

            ui.Muted("Hotkeys stored in .lnk shortcuts on the desktop and in the Start menu. An edit is written " +
                     "into the shortcut file when it allows it; hotkeys that running programs register are " +
                     "invisible to Windows.");

            using (ui.Scroll(new ScrollOpts { Height = Size.Fill, Gap = 8, Pad = 2, FadeEdges = true }))
            {
                if (_shortcuts.Length == 0 && !_scanning)
                {
                    using (ui.Card(new Card { Pad = 12, Gap = 6, Width = Size.Fill }))
                    {
                        ui.Label("No shortcut hotkeys");
                        ui.Muted("No shortcut on the desktop or in the Start menu carries a hotkey yet.");
                    }
                }

                foreach (ShortcutInfo shortcut in _shortcuts)
                {
                    using (ui.Id(shortcut.Path))
                    using (ui.Card(new Card { PadX = 10, PadY = 8, Gap = 6, Width = Size.Fill }))
                    {
                        using (ui.Row(new Box { Gap = 10, Align = Align.Center, Width = Size.Fill }))
                        {
                            using (ui.Column(new Box { Gap = 2, Width = Size.Fill }))
                            {
                                ui.Label(shortcut.Name);
                                ui.Muted(shortcut.Folder);
                            }

                            ui.Kbd(KeyMap.Text(shortcut.Mods, shortcut.HotKey));
                            if (!shortcut.Writable)
                                ui.Badge("read-only", new BadgeOpts { Tone = Tone.Warning, Size = ControlSize.Sm });
                        }

                        var combo = new KeyCombo(shortcut.Mods, shortcut.HotKey);
                        if (ui.ShortcutRecorder("sc.combo", ref combo, new ShortcutRecorderOpts
                        {
                            Width = Size.Px(240),
                            Placeholder = "No hotkey",
                        }))
                        {
                            string outcome = ShortcutStore.Write(shortcut, combo);
                            if (outcome.StartsWith("Saved", StringComparison.Ordinal))
                            {
                                shortcut.Mods = combo.Mods;
                                shortcut.HotKey = combo.Key;
                            }

                            SetMessage(outcome, outcome.StartsWith("Saved", StringComparison.Ordinal)
                                ? Tone.Success
                                : Tone.Danger);
                        }
                    }
                }
            }
        }
    }

    // -----------------------------------------------------------------------
    // Activity
    // -----------------------------------------------------------------------

    private void DrawActivity(Ui ui)
    {
        LogEntry[] entries = _log.Snapshot();

        using (ui.Column(new Box { Gap = 8, Width = Size.Fill, Height = Size.Fill }))
        {
            using (ui.Row(new Box { Gap = 8, Align = Align.Center, Width = Size.Fill, PadX = 2 }))
            {
                ui.Label("Activity");
                ui.Spacer();
                if (ui.Button("Clear", new ButtonOpts { Variant = ButtonVariant.Outline, Size = ControlSize.Sm }))
                    _log.Clear();
            }

            if (entries.Length == 0)
            {
                using (ui.Card(new Card { Pad = 12, Gap = 6, Width = Size.Fill }))
                {
                    ui.Label("Nothing has run yet");
                    ui.Muted("Press a saved hotkey while another application is in front; every attempt lands here.");
                }
            }

            using (ui.Scroll(new ScrollOpts { Height = Size.Fill, Gap = 6, Pad = 2, FadeEdges = true }))
            {
                for (int i = entries.Length - 1; i >= 0; i--)
                {
                    LogEntry entry = entries[i];
                    using (ui.Id(i))
                    using (ui.Card(new Card { PadX = 10, PadY = 8, Gap = 6, Width = Size.Fill }))
                    {
                        using (ui.Row(new Box { Gap = 8, Align = Align.Center, Width = Size.Fill }))
                        {
                            ui.Badge(entry.Ok ? "ok" : "error", new BadgeOpts
                            {
                                Tone = entry.Ok ? Tone.Success : Tone.Danger,
                                Size = ControlSize.Sm,
                                Dot = true,
                            });
                            ui.Label(entry.Binding);
                            ui.Spacer();
                            ui.Text(entry.When.ToString("HH:mm:ss"), new TextOpts(12, Color.TextMuted));
                        }

                        ui.Secondary(entry.Text);
                    }
                }
            }
        }
    }

    // -----------------------------------------------------------------------
    // Actions
    // -----------------------------------------------------------------------

    private void AddBinding()
    {
        var binding = new Binding { Name = "New binding", Capture = PayloadMode.SelectedText };
        _bindings.Add(binding);
        Select(_bindings.Count - 1);
        SetMessage("Record a hotkey, then Save.", Tone.Info);
    }

    private void AddDiskExample()
    {
        var binding = new Binding
        {
            Name = "Disk usage",
            Command = DiskExample,
            Capture = PayloadMode.SelectedFiles,
            Mods = (int)(Mods.Ctrl | Mods.Alt),
            Key = (int)Key.D,
        };
        _bindings.Add(binding);
        Select(_bindings.Count - 1);
        SaveDraft();
    }

    private void UseDiskExample()
    {
        if (_draft is null) return;
        _draft.Name = "Disk usage";
        _draft.Command = DiskExample;
        _draft.Args = "";
        _draft.Capture = PayloadMode.SelectedFiles;
        _combo = new KeyCombo((int)(Mods.Ctrl | Mods.Alt), Key.D);
        SetMessage("disk.exe receives the selected folders as arguments. Save to bind it.", Tone.Info);
    }

    private void Select(int index)
    {
        _selected = index;
        if (index >= 0 && index < _bindings.Count)
        {
            _draft = _bindings[index].Clone();
            _combo = new KeyCombo(_draft.Mods, (Key)_draft.Key);
        }
        else
        {
            _draft = null;
            _combo = default;
        }

        _message = "";
        _probe = "";
    }

    private bool IsDirty()
    {
        if (_draft is null || _selected < 0 || _selected >= _bindings.Count) return false;

        Binding saved = _bindings[_selected];
        return _draft.Name != saved.Name
               || _draft.Command != saved.Command
               || _draft.Args != saved.Args
               || _draft.WorkDir != saved.WorkDir
               || _draft.Env != saved.Env
               || _draft.Capture != saved.Capture
               || _draft.Enabled != saved.Enabled
               || _combo.Mods != saved.Mods
               || (int)_combo.Key != saved.Key;
    }

    private void SaveDraft()
    {
        if (_draft is null || _selected < 0) return;

        if (_draft.Command.Trim().Length == 0)
        {
            SetMessage("Choose an application first.", Tone.Danger);
            return;
        }
        if (_combo.Key == Key.None)
        {
            SetMessage("Record a hotkey.", Tone.Danger);
            return;
        }
        if (_combo.Mods == 0)
        {
            SetMessage("A global hotkey needs at least one modifier.", Tone.Danger);
            return;
        }

        for (int i = 0; i < _bindings.Count; i++)
        {
            if (i == _selected) continue;
            Binding other = _bindings[i];
            if (other.Enabled && other.Mods == _combo.Mods && other.Key == (int)_combo.Key)
            {
                SetMessage($"That hotkey is already used by \"{other.Name}\".", Tone.Danger);
                return;
            }
        }

        _draft.Mods = _combo.Mods;
        _draft.Key = (int)_combo.Key;
        _bindings[_selected] = _draft.Clone();
        string? saveError = ConfigStore.Save(_bindings);
        _host.SetBindings(_bindings);
        if (saveError is null) SetMessage("Saved. Registration refreshed.", Tone.Success);
        else SetMessage("Saved in memory, but the file could not be written: " + saveError, Tone.Warning);
        _probe = "";
    }

    private void TestDraft()
    {
        if (_draft is null) return;
        try
        {
            Payload payload = PayloadCapture.Capture(_draft.Capture, _hwnd, allowCopy: false);
            string result = Launcher.Run(_draft, payload);
            _log.Add(_draft.Name, result, true);
            SetMessage(result, Tone.Success);
        }
        catch (Exception error)
        {
            _log.Add(_draft.Name, error.Message, false);
            SetMessage(error.Message, Tone.Danger);
        }
    }

    private void DeleteDraft()
    {
        if (_selected < 0 || _draft is null) return;

        string name = _bindings[_selected].Name;
        _bindings.RemoveAt(_selected);
        string? saveError = ConfigStore.Save(_bindings);
        _host.SetBindings(_bindings);
        Select(Math.Min(_selected, _bindings.Count - 1));
        SetMessage(saveError is null
                ? $"Deleted \"{name}\"."
                : $"Deleted \"{name}\", but the file could not be written: {saveError}",
            saveError is null ? Tone.Neutral : Tone.Warning);
    }

    private void Probe()
    {
        (string text, Tone tone) = _host.Probe(_combo, _bindings, _selected);
        _probe = text;
        _probeTone = tone;
    }

    private void Browse()
    {
        if (_window is null || _draft is null) return;

        FilePickResult pick = _window.PickFile(new FileDialogOpts
        {
            Kind = FileDialogKind.Open,
            Title = "Choose an application",
            FilterName = "Programs",
            Filter = "exe;com;bat;cmd;lnk",
        });

        if (pick.Picked) _draft.Command = pick.Path;
        else if (pick.Error is { } error) SetMessage(error, Tone.Danger);
    }

    private void ScanShortcuts()
    {
        if (_scanning) return;
        _scanning = true;

        _ = Task.Run(() =>
        {
            try
            {
                _shortcuts = ShortcutStore.Scan().ToArray();
            }
            catch (Exception error)
            {
                _log.Add("Shortcuts", error.Message, false);
            }
            finally
            {
                _scanning = false;
                Wake();
            }
        });
    }

    private void SetMessage(string text, Tone tone)
    {
        _message = text;
        _messageTone = tone;
    }

    private void Wake()
    {
        nint hwnd = _hwnd;
        if (hwnd != 0) Win32.PostMessage(hwnd, 0, 0, 0);
    }

    private static Tone StatusTone(string status) => status switch
    {
        "Active" => Tone.Success,
        "Needs a hotkey" => Tone.Warning,
        "Taken by another app" => Tone.Danger,
        "Registration failed" => Tone.Danger,
        _ => Tone.Neutral,
    };

    // The list is narrow: one short word keeps the badge and the capture mode on the same line.
    private static string ShortStatus(string status) => status switch
    {
        "Taken by another app" => "Taken",
        "Registration failed" => "Failed",
        "Needs a hotkey" => "No key",
        "Disabled" => "Off",
        _ => status,
    };

    private static string PayloadHint(PayloadMode mode) => mode switch
    {
        PayloadMode.SelectedText =>
            "Presses Ctrl+C in the foreground application, reads the copied text, then restores the clipboard.",
        PayloadMode.Clipboard =>
            "Reads the clipboard as it is, without touching the foreground application.",
        PayloadMode.SelectedFiles =>
            "Presses Ctrl+C in Explorer or on the desktop, reads the selected file and folder paths, then restores the clipboard.",
        _ => "Starts the application with only the arguments above.",
    };

    public void Dispose()
    {
        _tray?.Dispose();
        _tray = null;
        _host.Dispose();
    }
}

// ---------------------------------------------------------------------------
// Model and storage
// ---------------------------------------------------------------------------

internal enum PayloadMode
{
    None = 0,
    SelectedText = 1,
    Clipboard = 2,
    SelectedFiles = 3,
}

internal sealed class Binding
{
    public string Name = "";
    public int Mods;
    public int Key;
    public string Command = "";
    public string Args = "";
    public string WorkDir = "";
    public string Env = "";
    public PayloadMode Capture = PayloadMode.SelectedText;
    public bool Enabled = true;

    public Binding Clone() => (Binding)MemberwiseClone();
}

internal sealed class Config
{
    public List<Binding> Bindings = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, IncludeFields = true)]
[JsonSerializable(typeof(Config))]
internal partial class ConfigJson : JsonSerializerContext
{
}

internal static class ConfigStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "hotkey",
        "bindings.json");

    public static List<Binding> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize(json, ConfigJson.Default.Config)?.Bindings ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static string? Save(List<Binding> bindings)
    {
        try
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new Config { Bindings = bindings },
                ConfigJson.Default.Config));
            return null;
        }
        catch (Exception error)
        {
            return error.Message;
        }
    }
}

internal readonly record struct LogEntry(DateTime When, string Binding, string Text, bool Ok);

internal sealed class ActivityLog
{
    private static readonly string LogFile = CreateLogFile();

    private readonly object _gate = new();
    private readonly List<LogEntry> _items = [];

    public void Add(string binding, string text, bool ok)
    {
        DateTime when = DateTime.Now;
        lock (_gate)
        {
            _items.Add(new LogEntry(when, binding, text, ok));
            if (_items.Count > 200) _items.RemoveAt(0);
        }

        // A second copy on disk: a hotkey that fails while the window is behind another
        // application is otherwise invisible until someone opens the Activity tab.
        try
        {
            if (LogFile.Length > 0)
                File.AppendAllText(LogFile, $"{when:yyyy-MM-dd HH:mm:ss} [{(ok ? "ok" : "error")}] {binding}: {text}\n");
        }
        catch
        {
            // Logging must never be the reason a hotkey fails.
        }
    }

    private static string CreateLogFile()
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "hotkey");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "activity.log");
        }
        catch
        {
            return "";
        }
    }

    public LogEntry[] Snapshot()
    {
        lock (_gate) return _items.ToArray();
    }

    public LogEntry? Latest
    {
        get
        {
            lock (_gate) return _items.Count > 0 ? _items[^1] : null;
        }
    }

    public void Clear()
    {
        lock (_gate) _items.Clear();
    }
}

// ---------------------------------------------------------------------------
// Global hotkeys: one thread owns the registrations and its own message loop,
// so WM_HOTKEY arrives even while the window is asleep.
// ---------------------------------------------------------------------------

internal sealed class HotkeyHost : IDisposable
{
    private const uint WmApp = 0x8000;
    private const uint WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const int ProbeId = 0x3FFF;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly List<int> _registered = [];
    private readonly ActivityLog _log;
    private readonly Func<nint> _window;

    private uint _threadId;
    private volatile nint _wakeWindow;
    private Binding[] _active = [];
    private string[] _status = [];
    private bool _disposed;

    public nint WakeWindow
    {
        set => _wakeWindow = value;
    }

    public HotkeyHost(ActivityLog log, Func<nint> window)
    {
        _log = log;
        _window = window;

        _thread = new Thread(Loop) { IsBackground = true, Name = "hotkey-host" };
        if (OperatingSystem.IsWindows()) _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    public string Status(int index)
    {
        string[] status = _status;
        return (uint)index < (uint)status.Length ? status[index] : "";
    }

    public void SetBindings(IReadOnlyList<Binding> bindings)
    {
        var snapshot = new Binding[bindings.Count];
        for (int i = 0; i < snapshot.Length; i++) snapshot[i] = bindings[i].Clone();

        _commands.Enqueue(() => Apply(snapshot));
        WakeHost();
    }

    /// <summary>
    /// Registers the combination on the calling thread and releases it at once. Failure with
    /// error 1409 means another window or thread already owns it.
    /// </summary>
    public (string Text, Tone Tone) Probe(KeyCombo combo, IReadOnlyList<Binding> bindings, int skip)
    {
        int vk = KeyMap.ToVk(combo.Key);
        if (combo.Mods == 0 || vk == 0)
            return ("Choose a modifier and a key", Tone.Warning);

        if (skip >= 0 && skip < bindings.Count)
        {
            Binding current = bindings[skip];
            if (current.Enabled && current.Mods == combo.Mods && current.Key == (int)combo.Key)
                return ("This binding already owns it", Tone.Success);
        }

        for (int i = 0; i < bindings.Count; i++)
        {
            if (i == skip) continue;
            Binding other = bindings[i];
            if (other.Enabled && other.Mods == combo.Mods && other.Key == (int)combo.Key)
                return ($"Used by \"{other.Name}\"", Tone.Info);
        }

        if (Win32.RegisterHotKey(0, ProbeId, KeyMap.ToWin32(combo.Mods) | ModNoRepeat, (uint)vk) != 0)
        {
            Win32.UnregisterHotKey(0, ProbeId);
            return ("Available", Tone.Success);
        }

        int error = Marshal.GetLastWin32Error();
        return error == 1409
            ? ("Taken by another application", Tone.Danger)
            : ($"Not available (error {error})", Tone.Warning);
    }

    private void Loop()
    {
        _threadId = Win32.GetCurrentThreadId();

        try
        {
            // Create this thread's message queue before announcing readiness: PostThreadMessage fails
            // for a thread that has not made one yet, and the first SetBindings arrives immediately.
            Win32.PeekMessage(out _, 0, 0, 0, 0);
            _ready.Set();

            while (Win32.GetMessage(out Win32.MSG message, 0, 0, 0) > 0)
            {
                if (message.message == WmApp) Drain();
                else if (message.message == WmHotkey) OnHotkey((int)message.wParam);
            }

            // Falling out of the loop means every registration is gone with this thread, so the
            // bindings silently stop firing while the window still lists them as Active.
            _log.Add("Hotkey", "Hotkey thread stopped: no hotkey can fire until the app restarts.", false);
        }
        catch (Exception error)
        {
            _log.Add("Hotkey", "Hotkey thread crashed: " + error.Message, false);
        }
        finally
        {
            try { _ready.Set(); } catch (ObjectDisposedException) { }
            Wake();
        }
    }

    private void Drain()
    {
        while (_commands.TryDequeue(out Action? command))
        {
            try
            {
                command();
            }
            catch (Exception error)
            {
                _log.Add("Hotkey", error.Message, false);
                Wake();
            }
        }
    }

    private void Apply(Binding[] bindings)
    {
        foreach (int id in _registered) Win32.UnregisterHotKey(0, id);
        _registered.Clear();

        _active = bindings;
        var status = new string[bindings.Length];

        for (int i = 0; i < bindings.Length; i++)
        {
            Binding binding = bindings[i];
            if (!binding.Enabled)
            {
                status[i] = "Disabled";
                continue;
            }

            int vk = KeyMap.ToVk((Key)binding.Key);
            if (vk == 0 || binding.Mods == 0)
            {
                status[i] = "Needs a hotkey";
                continue;
            }

            int id = i + 1;
            string combo = KeyMap.Text(binding.Mods, (Key)binding.Key);
            if (Win32.RegisterHotKey(0, id, KeyMap.ToWin32(binding.Mods) | ModNoRepeat, (uint)vk) != 0)
            {
                _registered.Add(id);
                status[i] = "Active";
                _log.Add(binding.Name, $"Registered {combo}", true);
            }
            else
            {
                int error = Marshal.GetLastWin32Error();
                status[i] = error == 1409
                    ? "Taken by another app"
                    : "Registration failed";
                _log.Add(binding.Name, error == 1409
                    ? $"Could not register {combo}: another application already owns it"
                    : $"Could not register {combo}: error {error}", false);
            }
        }

        _status = status;
        Wake();
    }

    private void OnHotkey(int id)
    {
        nint own = _window();
        bool ownForeground = own != 0 && Win32.GetForegroundWindow() == own;

        int index = id - 1;
        Binding[] active = _active;
        if ((uint)index >= (uint)active.Length) return;

        Binding binding = active[index];
        _log.Add(binding.Name, ownForeground ? "Hotkey pressed (this window is in front)" : "Hotkey pressed", true);

        try
        {
            // Never send Ctrl+C into our own window. With our window in front there is no other
            // application's selection to read, so use the clipboard as it stands.
            Payload payload = PayloadCapture.Capture(binding.Capture, own, allowCopy: !ownForeground);
            string result = Launcher.Run(binding, payload);
            _log.Add(binding.Name, result, true);
        }
        catch (Exception error)
        {
            _log.Add(binding.Name, error.Message, false);
        }

        Wake();
    }

    private void WakeHost()
    {
        if (_threadId != 0) Win32.PostThreadMessage(_threadId, WmApp, 0, 0);
    }

    private void Wake()
    {
        nint hwnd = _wakeWindow;
        if (hwnd != 0) Win32.PostMessage(hwnd, 0, 0, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _commands.Enqueue(() => Win32.PostQuitMessage(0));
        WakeHost();
        _thread.Join(800);
        _ready.Dispose();
    }
}

// ---------------------------------------------------------------------------
// Capture: selected text, clipboard text, and file/folder paths
// ---------------------------------------------------------------------------

internal readonly struct Payload
{
    public static readonly string[] NoFiles = [];
    public static readonly Payload Empty = new("", NoFiles);

    public readonly string Text;
    public readonly string[] Files;

    public Payload(string text, string[] files)
    {
        Text = text;
        Files = files;
    }
}

internal static class PayloadCapture
{
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const int VkC = 0x43;

    public static Payload Capture(PayloadMode mode, nint ownWindow, bool allowCopy)
    {
        if (mode == PayloadMode.None) return Payload.Empty;

        Payload payload;
        if (mode == PayloadMode.Clipboard || !allowCopy)
        {
            payload = ReadClipboard();
        }
        else
        {
            nint foreground = Win32.GetForegroundWindow();
            payload = foreground == 0 || foreground == ownWindow ? ReadClipboard() : CopySelection();
        }

        // A binding asks for one kind of selection: do not hand a text binding the file list
        // (or the reverse) just because the clipboard happened to carry both.
        return mode switch
        {
            PayloadMode.SelectedText => new Payload(payload.Text, Payload.NoFiles),
            PayloadMode.SelectedFiles => new Payload("", payload.Files),
            _ => payload,
        };
    }

    private static Payload CopySelection()
    {
        // Take a copy of what the clipboard holds now, so the simulated Ctrl+C does not cost the
        // user their clipboard. The copy is made with the plain clipboard API on purpose: the
        // object OleGetClipboard returns is backed by the clipboard itself, so handing it back to
        // OleSetClipboard and flushing makes OLE render it from the clipboard it is backing —
        // a recursion that overflows the stack inside ole32 and takes the process down.
        Payload previous = ReadClipboard();

        try
        {
            uint sequence = Win32.GetClipboardSequenceNumber();
            SendCopy();

            for (int i = 0; i < 50; i++)
            {
                Thread.Sleep(10);
                if (Win32.GetClipboardSequenceNumber() != sequence) break;
            }

            return ReadClipboard();
        }
        finally
        {
            RestoreClipboard(previous);
        }
    }

    private static Payload ReadClipboard()
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (Win32.OpenClipboard(0) == 0)
            {
                Thread.Sleep(15);
                continue;
            }

            try
            {
                return new Payload(ReadUnicodeText(), ReadDropFiles());
            }
            finally
            {
                Win32.CloseClipboard();
            }
        }

        return Payload.Empty;
    }

    private static string ReadUnicodeText()
    {
        nint handle = Win32.GetClipboardData(Win32.CfUnicodeText);
        if (handle != 0)
        {
            nint pointer = Win32.GlobalLock(handle);
            if (pointer != 0)
            {
                try
                {
                    return Marshal.PtrToStringUni(pointer) ?? "";
                }
                finally
                {
                    Win32.GlobalUnlock(handle);
                }
            }
        }

        handle = Win32.GetClipboardData(Win32.CfText);
        if (handle == 0) return "";

        nint ansi = Win32.GlobalLock(handle);
        if (ansi == 0) return "";

        try
        {
            return Marshal.PtrToStringAnsi(ansi) ?? "";
        }
        finally
        {
            Win32.GlobalUnlock(handle);
        }
    }

    private static string[] ReadDropFiles()
    {
        nint handle = Win32.GetClipboardData(Win32.CfHDrop);
        if (handle == 0) return Payload.NoFiles;

        uint count = Win32.DragQueryFileW(handle, 0xFFFFFFFF, null, 0);
        if (count == 0) return Payload.NoFiles;

        var files = new string[count];
        for (uint i = 0; i < count; i++)
        {
            uint length = Win32.DragQueryFileW(handle, i, null, 0);
            var buffer = new StringBuilder((int)length + 1);
            if (Win32.DragQueryFileW(handle, i, buffer, (uint)buffer.Capacity) > 0)
                files[i] = buffer.ToString();
        }

        return files;
    }

    private static void SendCopy()
    {
        var inputs = new Win32.INPUT[9];
        int n = 0;

        // The hotkey's own modifiers are still physically down. Release them first, or the
        // application receives Ctrl+Alt+C instead of Ctrl+C.
        inputs[n++] = KeyInput(VkControl, up: true);
        inputs[n++] = KeyInput(VkMenu, up: true);
        inputs[n++] = KeyInput(VkShift, up: true);
        inputs[n++] = KeyInput(VkLWin, up: true);
        inputs[n++] = KeyInput(VkRWin, up: true);
        inputs[n++] = KeyInput(VkControl, up: false);
        inputs[n++] = KeyInput(VkC, up: false);
        inputs[n++] = KeyInput(VkC, up: true);
        inputs[n++] = KeyInput(VkControl, up: true);

        uint inserted = Win32.SendInput((uint)n, inputs, Unsafe.SizeOf<Win32.INPUT>());
        if (inserted != (uint)n)
        {
            throw new InvalidOperationException(
                "Windows blocked the simulated Ctrl+C. The foreground application may be running elevated.");
        }
    }

    private static Win32.INPUT KeyInput(int virtualKey, bool up) => new()
    {
        type = 1,
        U = new Win32.InputUnion
        {
            ki = new Win32.KEYBDINPUT
            {
                wVk = (ushort)virtualKey,
                dwFlags = up ? 0x0002u : 0u,
            },
        },
    };

    /// <summary>
    /// Puts back what the clipboard held before the simulated Ctrl+C. Text and file lists are the
    /// two kinds this app reads, so they are the two it can restore; anything else (an image, say)
    /// stays replaced by the selection rather than being emptied out.
    /// </summary>
    private static void RestoreClipboard(Payload previous)
    {
        if (previous.Files.Length > 0) WriteFiles(previous.Files);
        else if (previous.Text.Length > 0) WriteText(previous.Text);
    }

    private static void WriteFiles(string[] files)
    {
        if (files.Length == 0) return;

        // A CF_HDROP block: a DROPFILES header, then the paths as wide characters, each with its
        // own terminator and one more at the end.
        const int header = 20;
        int chars = 1;
        foreach (string file in files) chars += file.Length + 1;

        var block = new byte[header + chars * 2];
        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(0, 4), header);   // pFiles
        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(16, 4), 1);      // fWide

        int offset = header;
        foreach (string file in files)
        {
            offset += Encoding.Unicode.GetBytes(file, 0, file.Length, block, offset);
            offset += 2;
        }

        if (Win32.OpenClipboard(0) == 0) return;

        nint memory = 0;
        try
        {
            // GMEM_MOVEABLE | GMEM_ZEROINIT, so the terminators are already zero.
            memory = Win32.GlobalAlloc(0x0042, (nuint)block.Length);
            if (memory == 0) return;

            nint target = Win32.GlobalLock(memory);
            if (target == 0) return;

            Marshal.Copy(block, 0, target, block.Length);
            Win32.GlobalUnlock(memory);

            Win32.EmptyClipboard();
            if (Win32.SetClipboardData(Win32.CfHDrop, memory) != 0) memory = 0;
        }
        finally
        {
            if (memory != 0) Win32.GlobalFree(memory);
            Win32.CloseClipboard();
        }
    }

    private static void WriteText(string text)
    {
        if (text.Length == 0 || Win32.OpenClipboard(0) == 0) return;

        nint memory = 0;
        try
        {
            Win32.EmptyClipboard();
            int bytes = (text.Length + 1) * 2;
            memory = Win32.GlobalAlloc(0x0042, (nuint)bytes);
            if (memory == 0) return;

            nint target = Win32.GlobalLock(memory);
            if (target == 0)
            {
                Win32.GlobalFree(memory);
                memory = 0;
                return;
            }

            Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
            Win32.GlobalUnlock(memory);

            // SetClipboardData takes ownership of the block on success.
            if (Win32.SetClipboardData(Win32.CfUnicodeText, memory) != 0) memory = 0;
        }
        finally
        {
            if (memory != 0) Win32.GlobalFree(memory);
            Win32.CloseClipboard();
        }
    }
}

// ---------------------------------------------------------------------------
// Launch: resolve a name on PATH, expand the argument template, start the app
// ---------------------------------------------------------------------------

internal static class Launcher
{
    public static string Run(Binding binding, Payload payload)
    {
        string command = Environment.ExpandEnvironmentVariables(binding.Command.Trim().Trim('"'));
        if (command.Length == 0) throw new InvalidOperationException("The binding has no application.");

        string? executable = Resolve(command);
        if (executable is null)
            throw new InvalidOperationException($"\"{command}\" was not found on PATH.");

        bool isScript = executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                        || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

        var start = new ProcessStartInfo
        {
            FileName = isScript ? "cmd.exe" : executable,
            UseShellExecute = false,
            CreateNoWindow = false,
        };

        if (isScript)
        {
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(executable);
        }

        string workDir = Environment.ExpandEnvironmentVariables(binding.WorkDir.Trim().Trim('"'));
        if (workDir.Length > 0) start.WorkingDirectory = workDir;

        foreach (string argument in BuildArguments(binding.Args, payload, workDir))
            start.ArgumentList.Add(argument);

        foreach ((string key, string value) in ParseEnvironment(binding.Env))
            start.Environment[key] = Environment.ExpandEnvironmentVariables(value);

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("The application did not start.");

        ActivateLater(process.Id);

        return $"Started {Path.GetFileName(executable)} (pid {process.Id})";
    }

    /// <summary>
    /// A process started from a background hotkey is not allowed to steal focus, and it has no
    /// window for a moment after it starts. Ask for its first window for about a second.
    /// </summary>
    private static void ActivateLater(int processId)
    {
        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(50);
                try
                {
                    using Process process = Process.GetProcessById(processId);
                    nint window = process.MainWindowHandle;
                    if (window == 0) continue;

                    Win32.ShowWindow(window, 9); // SW_RESTORE
                    Win32.SetForegroundWindow(window);
                    return;
                }
                catch
                {
                    return;
                }
            }
        });
    }

    private static string? Resolve(string command)
    {
        bool looksLikePath = command.Contains('\\') || command.Contains('/') || Path.IsPathRooted(command);
        if (looksLikePath)
            return File.Exists(command) ? Path.GetFullPath(command) : null;

        string[] extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        string[] directories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (string directory in directories)
        {
            string folder = directory.Trim().Trim('"');
            if (folder.Length == 0) continue;

            string candidate = Path.Combine(folder, command);
            if (File.Exists(candidate)) return candidate;

            foreach (string extension in extensions)
            {
                if (File.Exists(candidate + extension)) return candidate + extension;
            }
        }

        foreach (string extension in extensions)
        {
            if (File.Exists(command + extension)) return Path.GetFullPath(command + extension);
        }

        return null;
    }

    private static List<string> BuildArguments(string template, Payload payload, string workDir)
    {
        var arguments = new List<string>();
        bool usedText = false;
        bool usedFiles = false;

        foreach (string token in SplitArguments(template))
        {
            string argument = token;

            if (argument.Contains("{text}", StringComparison.Ordinal))
            {
                argument = argument.Replace("{text}", payload.Text, StringComparison.Ordinal);
                usedText = true;
            }
            if (argument.Contains("{clipboard}", StringComparison.Ordinal))
            {
                argument = argument.Replace("{clipboard}", payload.Text, StringComparison.Ordinal);
                usedText = true;
            }
            if (argument.Contains("{files}", StringComparison.Ordinal))
            {
                usedFiles = true;
                if (argument == "{files}")
                {
                    arguments.AddRange(payload.Files);
                    continue;
                }
                argument = argument.Replace("{files}", string.Join(" ", payload.Files), StringComparison.Ordinal);
            }
            if (argument.Contains("{file}", StringComparison.Ordinal))
            {
                usedFiles = true;
                argument = argument.Replace("{file}", payload.Files.Length > 0 ? payload.Files[0] : "",
                    StringComparison.Ordinal);
            }
            if (argument.Contains("{dir}", StringComparison.Ordinal))
                argument = argument.Replace("{dir}", workDir, StringComparison.Ordinal);

            if (argument.Length > 0 || token.Length > 0) arguments.Add(argument);
        }

        if (!usedFiles && payload.Files.Length > 0) arguments.AddRange(payload.Files);
        if (!usedText && payload.Text.Length > 0) arguments.Add(payload.Text);

        return arguments;
    }

    private static IEnumerable<string> SplitArguments(string template)
    {
        var current = new StringBuilder();
        bool quoted = false;

        foreach (char c in template)
        {
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0) yield return current.ToString();
    }

    private static IEnumerable<(string Key, string Value)> ParseEnvironment(string text)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            int equals = line.IndexOf('=');
            if (equals <= 0) continue;

            string key = line[..equals].Trim();
            string value = line[(equals + 1)..].Trim();

            if (key.EndsWith('+'))
            {
                key = key[..^1].TrimEnd();
                string existing = Environment.GetEnvironmentVariable(key) ?? "";
                value = existing.Length > 0 ? existing + ";" + value : value;
            }

            if (key.Length > 0) yield return (key, value);
        }
    }
}

// ---------------------------------------------------------------------------
// Shortcut files (.lnk): read and write the hotkey field in the header
// ---------------------------------------------------------------------------

internal sealed class ShortcutInfo
{
    public required string Path;
    public required string Name;
    public required string Folder;
    public int Mods;
    public Key HotKey;
    public bool Writable;
}

internal static class ShortcutStore
{
    private static readonly byte[] LinkClsid =
    [
        0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46,
    ];

    public static List<ShortcutInfo> Scan()
    {
        var found = new List<ShortcutInfo>();

        foreach ((string root, string label) in Roots())
        {
            if (root.Length == 0 || !Directory.Exists(root)) continue;

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
            };

            try
            {
                foreach (string file in Directory.EnumerateFiles(root, "*.lnk", options))
                {
                    if (!TryRead(file, out int mods, out Key key)) continue;
                    if (key == Key.None) continue;

                    found.Add(new ShortcutInfo
                    {
                        Path = file,
                        Name = Path.GetFileNameWithoutExtension(file),
                        Folder = label,
                        Mods = mods,
                        HotKey = key,
                        Writable = IsWritable(file),
                    });
                }
            }
            catch
            {
                // A locked or inaccessible Start menu branch is not a reason to fail the scan.
            }
        }

        found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return found;
    }

    public static string Write(ShortcutInfo shortcut, KeyCombo combo)
    {
        if (!shortcut.Writable) return "This shortcut is read-only.";

        int virtualKey = KeyMap.ToVk(combo.Key);
        if (combo.Key != Key.None && virtualKey == 0)
            return "That key cannot be stored in a shortcut.";

        if ((combo.Mods & 8) != 0)
            return "Windows-key hotkeys cannot be stored in a shortcut.";

        ushort value = combo.Key == Key.None
            ? (ushort)0
            : (ushort)(((KeyMap.ToWin32(combo.Mods) & 0x07) << 8) | (uint)virtualKey);

        try
        {
            using var stream = new FileStream(shortcut.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            stream.Seek(0x40, SeekOrigin.Begin);
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
            stream.Write(bytes);
            return $"Saved to {Path.GetFileName(shortcut.Path)}";
        }
        catch (Exception error)
        {
            return $"Could not write: {error.Message}";
        }
    }

    private static IEnumerable<(string Root, string Label)> Roots()
    {
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Desktop");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Public desktop");

        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return (Path.Combine(roaming, @"Microsoft\Windows\Start Menu\Programs"), "Start menu");

        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        yield return (Path.Combine(common, @"Microsoft\Windows\Start Menu\Programs"), "All users start menu");
    }

    private static bool TryRead(string path, out int mods, out Key key)
    {
        mods = 0;
        key = Key.None;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < 0x4C) return false;

            Span<byte> header = stackalloc byte[0x4C];
            stream.ReadExactly(header);

            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x4C) return false;
            if (!header.Slice(4, 16).SequenceEqual(LinkClsid)) return false;

            ushort hotkey = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(0x40, 2));
            int virtualKey = hotkey & 0xFF;
            if (virtualKey == 0) return false;

            key = KeyMap.FromVk(virtualKey);
            if (key == Key.None) return false;

            mods = KeyMap.FromWin32((hotkey >> 8) & 0x07);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsWritable(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

// ---------------------------------------------------------------------------
// Key names and virtual-key conversion
// ---------------------------------------------------------------------------

internal static class KeyMap
{
    public static string Text(int mods, Key key)
    {
        if (key == Key.None) return "None";

        var text = new StringBuilder();
        if ((mods & 2) != 0) text.Append("Ctrl+");
        if ((mods & 4) != 0) text.Append("Alt+");
        if ((mods & 1) != 0) text.Append("Shift+");
        if ((mods & 8) != 0) text.Append("Win+");
        text.Append(Name(key));
        return text.ToString();
    }

    private static string Name(Key key)
    {
        int k = (int)key;
        if (k >= (int)Key.A && k <= (int)Key.Z) return ((char)('A' + k - (int)Key.A)).ToString();
        if (k >= (int)Key.D0 && k <= (int)Key.D9) return ((char)('0' + k - (int)Key.D0)).ToString();
        if (k >= (int)Key.F1 && k <= (int)Key.F12) return "F" + (k - (int)Key.F1 + 1);

        return key switch
        {
            Key.Tab => "Tab",
            Key.Enter => "Enter",
            Key.Escape => "Esc",
            Key.Backspace => "Backspace",
            Key.Delete => "Delete",
            Key.Space => "Space",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Minus => "-",
            Key.Equal => "=",
            Key.Comma => ",",
            Key.Period => ".",
            Key.Slash => "/",
            _ => "?",
        };
    }

    // CuiSharp numbers the modifiers Shift = 1, Ctrl = 2, Alt = 4, Win = 8, while Windows uses
    // MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8. Only Ctrl happens to line up, so
    // anything leaving for Win32 — RegisterHotKey, the .lnk hotkey field — must be translated here
    // or Ctrl+Alt+D is quietly registered as Ctrl+Shift+D.
    public static uint ToWin32(int mods)
    {
        uint result = 0;
        if ((mods & 1) != 0) result |= 0x0004; // Shift
        if ((mods & 2) != 0) result |= 0x0002; // Ctrl
        if ((mods & 4) != 0) result |= 0x0001; // Alt
        if ((mods & 8) != 0) result |= 0x0008; // Win
        return result;
    }

    public static int FromWin32(int mods)
    {
        int result = 0;
        if ((mods & 0x0004) != 0) result |= 1; // Shift
        if ((mods & 0x0002) != 0) result |= 2; // Ctrl
        if ((mods & 0x0001) != 0) result |= 4; // Alt
        if ((mods & 0x0008) != 0) result |= 8; // Win
        return result;
    }

    public static int ToVk(Key key)
    {
        int k = (int)key;
        if (k >= (int)Key.A && k <= (int)Key.Z) return 0x41 + k - (int)Key.A;
        if (k >= (int)Key.D0 && k <= (int)Key.D9) return 0x30 + k - (int)Key.D0;
        if (k >= (int)Key.F1 && k <= (int)Key.F12) return 0x70 + k - (int)Key.F1;

        return key switch
        {
            Key.Tab => 0x09,
            Key.Enter => 0x0D,
            Key.Escape => 0x1B,
            Key.Backspace => 0x08,
            Key.Delete => 0x2E,
            Key.Space => 0x20,
            Key.Left => 0x25,
            Key.Right => 0x27,
            Key.Up => 0x26,
            Key.Down => 0x28,
            Key.Home => 0x24,
            Key.End => 0x23,
            Key.PageUp => 0x21,
            Key.PageDown => 0x22,
            Key.Minus => 0xBD,
            Key.Equal => 0xBB,
            Key.Comma => 0xBC,
            Key.Period => 0xBE,
            Key.Slash => 0xBF,
            _ => 0,
        };
    }

    public static Key FromVk(int virtualKey)
    {
        if (virtualKey >= 0x41 && virtualKey <= 0x5A) return (Key)((int)Key.A + virtualKey - 0x41);
        if (virtualKey >= 0x30 && virtualKey <= 0x39) return (Key)((int)Key.D0 + virtualKey - 0x30);
        if (virtualKey >= 0x70 && virtualKey <= 0x7B) return (Key)((int)Key.F1 + virtualKey - 0x70);

        return virtualKey switch
        {
            0x09 => Key.Tab,
            0x0D => Key.Enter,
            0x1B => Key.Escape,
            0x08 => Key.Backspace,
            0x2E => Key.Delete,
            0x20 => Key.Space,
            0x25 => Key.Left,
            0x27 => Key.Right,
            0x26 => Key.Up,
            0x28 => Key.Down,
            0x24 => Key.Home,
            0x23 => Key.End,
            0x21 => Key.PageUp,
            0x22 => Key.PageDown,
            0xBD => Key.Minus,
            0xBB => Key.Equal,
            0xBC => Key.Comma,
            0xBE => Key.Period,
            0xBF => Key.Slash,
            _ => Key.None,
        };
    }
}

// ---------------------------------------------------------------------------
// Tray icon: closing the window hides it instead of quitting, so the hotkeys
// keep working; the tray brings the window back or exits for real.
// ---------------------------------------------------------------------------

internal sealed class TrayIcon : IDisposable
{
    private const uint TrayMessage = 0x8001; // WM_APP + 1
    private const uint WmClose = 0x0010;
    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const uint NifMessage = 0x1;
    private const uint NifIcon = 0x2;
    private const uint NifTip = 0x4;
    private const uint NimAdd = 0;
    private const uint NimDelete = 2;
    private const uint MfString = 0;
    private const uint MfSeparator = 0x800;
    private const uint TpmRightButton = 0x2;
    private const uint TpmReturnCmd = 0x100;
    private const int OpenCommand = 1;
    private const int ExitCommand = 2;

    private readonly nint _hwnd;
    private readonly Win32.WndProc _proc;
    private nint _original;
    private nint _icon;
    private bool _added;
    private bool _exiting;

    public TrayIcon(nint hwnd)
    {
        _hwnd = hwnd;
        _proc = OnMessage;
        _original = Win32.SetWindowLongPtr(hwnd, Win32.GwlpWndProc, Marshal.GetFunctionPointerForDelegate(_proc));
        _icon = Win32.LoadAppIcon();
        Add();
    }

    public bool Visible => _added;

    private void Add()
    {
        var data = new Win32.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = TrayMessage,
            hIcon = _icon,
            szTip = "Hotkey — click to open, right-click to exit",
        };

        _added = Win32.Shell_NotifyIconW(NimAdd, ref data) != 0;
    }

    private void Remove()
    {
        if (!_added) return;

        var data = new Win32.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
        };

        Win32.Shell_NotifyIconW(NimDelete, ref data);
        _added = false;
    }

    private nint OnMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        // The window's close button hides it: the hotkeys live on their own thread and keep
        // firing, and the tray icon is the way back. Without an icon the window must close for
        // real, or the app would be left running with no way to reach it.
        if (message == WmClose && !_exiting && _added)
        {
            Win32.ShowWindow(hwnd, SwHide);
            return 0;
        }

        if (message == TrayMessage)
        {
            int mouse = (int)(lParam & 0xFFFF);
            if (mouse is Win32.WmLButtonUp or Win32.WmLButtonDblClk) Open();
            else if (mouse == Win32.WmRButtonUp) Menu();
            return 0;
        }

        return Win32.CallWindowProcW(_original, hwnd, message, wParam, lParam);
    }

    private void Open()
    {
        Win32.ShowWindow(_hwnd, Win32.IsIconic(_hwnd) != 0 ? SwRestore : SwShow);
        Win32.SetForegroundWindow(_hwnd);
    }

    private void Menu()
    {
        nint menu = Win32.CreatePopupMenu();
        Win32.AppendMenuW(menu, MfString, OpenCommand, "Open Hotkey");
        Win32.AppendMenuW(menu, MfSeparator, 0, null);
        Win32.AppendMenuW(menu, MfString, ExitCommand, "Exit");

        Win32.GetCursorPos(out Win32.POINT point);

        // Without foregrounding first, the menu stays open when the user clicks elsewhere.
        Win32.SetForegroundWindow(_hwnd);
        int command = Win32.TrackPopupMenu(menu, TpmRightButton | TpmReturnCmd, point.X, point.Y, 0, _hwnd, 0);
        Win32.DestroyMenu(menu);
        Win32.PostMessage(_hwnd, 0, 0, 0);

        if (command == OpenCommand) Open();
        else if (command == ExitCommand) Quit();
    }

    private void Quit()
    {
        // Remove the icon before the window goes away: a dead hwnd leaves a ghost in the tray.
        Remove();
        _exiting = true;
        Win32.PostMessage(_hwnd, WmClose, 0, 0);
    }

    public void Dispose()
    {
        Remove();

        if (_original != 0)
        {
            Win32.SetWindowLongPtr(_hwnd, Win32.GwlpWndProc, _original);
            _original = 0;
        }

        if (_icon != 0)
        {
            Win32.DestroyIcon(_icon);
            _icon = 0;
        }
    }
}

// ---------------------------------------------------------------------------
// Win32
// ---------------------------------------------------------------------------

internal static class Win32
{
    public const uint CfText = 1;
    public const uint CfUnicodeText = 13;
    public const uint CfHDrop = 15;

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern int ShowWindow(nint hWnd, int command);

    [DllImport("user32.dll")]
    public static extern int SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern int PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int PostThreadMessage(uint idThread, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG msg, nint hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    public static extern int PeekMessage(out MSG msg, nint hWnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int code);

    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int OpenClipboard(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetClipboardData(uint format, nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint GlobalFree(nint memory);

    [DllImport("kernel32.dll")]
    public static extern nint GlobalLock(nint handle);

    [DllImport("kernel32.dll")]
    public static extern int GlobalUnlock(nint handle);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint DragQueryFileW(nint hDrop, uint index, StringBuilder? file, uint length);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    // ---- tray icon -------------------------------------------------------

    public const int GwlpWndProc = -4;
    public const int WmLButtonUp = 0x0202;
    public const int WmLButtonDblClk = 0x0203;
    public const int WmRButtonUp = 0x0205;

    public delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public int cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint ExtractIconExW(string file, int index, out nint large, out nint small, uint count);

    [DllImport("user32.dll")]
    public static extern int DestroyIcon(nint icon);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint hWnd, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(nint hWnd, int index, int value);

    public static nint SetWindowLongPtr(nint hWnd, int index, nint value) => nint.Size == 8
        ? SetWindowLongPtr64(hWnd, index, value)
        : SetWindowLong32(hWnd, index, (int)value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint CallWindowProcW(nint previous, nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int AppendMenuW(nint menu, uint flags, int id, string? item);

    [DllImport("user32.dll")]
    public static extern int DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    public static extern int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hWnd, nint rect);

    [DllImport("user32.dll")]
    public static extern int GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    public static extern int IsIconic(nint hWnd);

    /// <summary>The icon the executable carries, as the shell would show it.</summary>
    public static nint LoadAppIcon()
    {
        string? path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path)) return 0;

        ExtractIconExW(path, 0, out nint large, out nint small, 1);
        if (small != 0)
        {
            if (large != 0) DestroyIcon(large);
            return small;
        }

        return large;
    }
}
