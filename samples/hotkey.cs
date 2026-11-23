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
using System.Collections.Frozen;
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
    private static readonly string[] CaptureNames =
        ["Nothing", "Selected text", "Clipboard", "Selected files and folders"];

    private readonly bool _diskExampleExists = File.Exists(DiskExample);
    private readonly ActivityLog _log = new();
    private readonly HotkeyHost _host;
    private readonly List<Binding> _bindings = ConfigStore.Load();

    // The quick launcher lives in a second, small window. Its bridge is the only thing shared
    // across the two UI threads; actions queued here land on this window's thread before a frame.
    private readonly LauncherBridge _launcherBridge = new();
    private readonly LauncherWindow _launcher;
    private readonly ConcurrentQueue<Action> _uiActions = new();

    // The key-value editor edits this array in place. It is loaded from the draft on Select and
    // written back to Binding.Env on Save; _envDirty makes an edit show up as an unsaved change.
    private const int MaxEnvPairs = 32;
    private readonly KeyValue[] _envPairs = new KeyValue[MaxEnvPairs];
    private int _envCount;
    private bool _envDirty;

    private ShortcutInfo[] _shortcuts = [];
    private bool _scanStarted;
    private bool _scanning;

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
        _launcher = new LauncherWindow(_launcherBridge, _log);
        _launcherBridge.WakeMain = Wake;
        _host.ShowLauncher = () => _launcher.Show();
        _host.LauncherPayload = payload => _launcher.Complete(payload);
        _host.LauncherHandle = () => _launcher.Handle;
        // Last-access bookkeeping and config writes belong on this window's thread.
        _host.Triggered = id => _uiActions.Enqueue(() => NoteTrigger(id));

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

                // Build the launcher now, not on the first hotkey: its window, GPU context and
                // fonts are then warm by the time Ctrl+Space arrives. In headless runs this
                // callback never fires, so no second message loop is started there.
                _launcher.Start();
            },
        };

        if (_bindings.Count > 0) Select(0);
        _host.SetBindings(_bindings);
        RefreshLauncher();
    }

    public void Draw(Ui ui)
    {
        // The hotkey thread and the launcher thread never touch the model; they hand work back here.
        while (_uiActions.TryDequeue(out Action? action)) action();
        foreach (string id in _launcherBridge.DrainTriggered()) NoteTrigger(id);

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
                DrawResultBadge(ui, entry.Ok);
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
            DrawBindingList(ui);

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

    private void DrawBindingList(Ui ui)
    {
        using (ui.Column(new Box { Width = Size.Px(220), Height = Size.Fill, Gap = 6 }))
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
                if (_bindings.Count == 0) DrawEmptyBindingList(ui);

                for (int i = 0; i < _bindings.Count; i++) DrawBindingCard(ui, i);
            }
        }
    }

    private void DrawEmptyBindingList(Ui ui)
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

    private void DrawBindingCard(Ui ui, int index)
    {
        Binding binding = _bindings[index];
        string status = _host.Status(index);
        string combo = KeyMap.Text(binding.Mods, (Key)binding.Key);
        string target = Path.GetFileName(binding.Command);
        if (target.Length == 0) target = binding.Command;

        using (ui.Id(index))
        {
            bool clicked;
            using (ui.Card(new Card
            {
                PadX = 10,
                PadY = 8,
                Gap = 4,
                Width = Size.Fill,
                Hoverable = true,
                Selected = index == _selected,
                Muted = !binding.Enabled,
            }, out clicked))
            {
                using (ui.Row(new Box { Gap = 8, Align = Align.Center, Width = Size.Fill }))
                {
                    ui.Label(binding.Name.Length > 0 ? binding.Name : "Untitled");
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

            if (clicked) Select(index);
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
        Binding binding = _draft!;
        bool builtIn = binding.BuiltIn;
        bool dirty = IsDirty();
        string status = dirty ? "Unsaved" : _host.Status(_selected);

        using (ui.Column(new Box { Gap = 0, Width = Size.Fill, Height = Size.Fill }))
        {
            DrawEditorHeader(ui, binding, builtIn, dirty, status);

            using (ui.Scroll(new ScrollOpts { Height = Size.Fill, Gap = 0, Pad = 2, FadeEdges = true }))
            using (ui.Column(new Box { Width = Size.Fill, Gap = 8 }))
            {
                DrawTriggerCard(ui);

                if (builtIn) DrawBuiltInCard(ui);
                else DrawApplicationCard(ui, binding);
            }

            DrawEditorFooter(ui, binding, builtIn, dirty);
        }
    }

    private void DrawEditorHeader(Ui ui, Binding binding, bool builtIn, bool dirty, string status)
    {
        using (ui.Row(new Box { Gap = 10, Align = Align.Center, Width = Size.Fill, PadBottom = 8, PadX = 2 }))
        {
            ui.Heading(binding.Name.Length > 0 ? binding.Name : "Untitled binding", 3);
            if (builtIn)
                ui.Badge("built-in", new BadgeOpts { Tone = Tone.Info, Size = ControlSize.Sm });
            ui.Badge(status, new BadgeOpts
            {
                Tone = dirty ? Tone.Warning : StatusTone(status),
                Size = ControlSize.Sm,
                Dot = !dirty,
            });
            ui.Spacer();
            ui.Kbd(KeyMap.Text(binding.Mods, (Key)binding.Key));
        }
    }

    private void DrawTriggerCard(Ui ui)
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
    }

    private void DrawApplicationCard(Ui ui, Binding binding)
    {
        using (ui.Card(new Card { Pad = 12, Gap = 8, Width = Size.Fill }))
        {
            ui.Label("Application");
            ui.Input("edit.name", ref binding.Name, new InputOpts
            {
                Label = "Name",
                Width = Size.Grow(1),
            });
            ui.Input("edit.workdir", ref binding.WorkDir, new InputOpts
            {
                Label = "Working directory",
                Placeholder = "optional",
                Width = Size.Grow(1),
            });
            ui.Input("edit.command", ref binding.Command, new InputOpts
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

            int capture = (int)binding.Capture;
            if (ui.Select("edit.capture", CaptureNames, ref capture,
                    new SelectOpts { Label = "Capture before launch", Width = Size.Fill }))
            {
                binding.Capture = (CaptureMode)capture;
            }
            ui.Muted(CaptureHint(binding.Capture));

            ui.TextArea("edit.args", ref binding.Args, new InputOpts
            {
                Label = "Arguments",
                Width = Size.Fill,
                Height = 52,
                Placeholder = "-n \"{text}\"",
                Hint = "Tokens: {text} {clipboard} {files} {file} {dir}. {dir} is the captured folder (or a " +
                       "captured file's parent), falling back to Working directory. A captured value with no token is appended.",
            });

            DrawEnvEditor(ui);
        }

        using (ui.Card(new Card { Pad = 12, Gap = 6, Width = Size.Fill }))
        {
            ui.Label("Test");
            ui.Muted("Reads the clipboard as it is now; the saved hotkey captures a live selection.");
        }
    }

    private void DrawEnvEditor(Ui ui)
    {
        ui.Label("Environment");
        int envCount = _envCount;
        if (ui.KvEditor("edit.env", _envPairs, ref envCount, new KvEditorOpts
        {
            Width = Size.Fill,
            KeyPlaceholder = "Variable",
            ValuePlaceholder = "Value",
        }))
        {
            _envCount = envCount;
            _envDirty = true;
        }
        ui.Muted("The launched process inherits the system and user environment. " +
                 "Each enabled row overrides one variable; end a name with + to append " +
                 "to its current value (NAME+ adds to NAME). %VARIABLE% expands from this process.");
    }

    private void DrawBuiltInCard(Ui ui)
    {
        using (ui.Card(new Card { Pad = 12, Gap = 8, Width = Size.Fill }))
        {
            ui.Label("Quick launcher");
            ui.Secondary("This hotkey opens a 380 × 340 window listing every available binding, " +
                         "ordered by when it was last used. Type to filter, use ↑ and ↓ to move, " +
                         "and press Enter to run the selected binding.");
            ui.Muted("Shift+Tab switches to the captured text, where file and folder paths can be " +
                     "edited before Enter returns to the list. This binding cannot be deleted, " +
                     "but it can be disabled or given a different hotkey.");
        }
    }

    private void DrawEditorFooter(Ui ui, Binding binding, bool builtIn, bool dirty)
    {
        using (ui.Row(new Box { Gap = 8, Align = Align.Center, Width = Size.Fill, PadY = 10, PadX = 2 }))
        {
            if (_message.Length > 0)
                ui.Badge(_message, new BadgeOpts { Tone = _messageTone, Size = ControlSize.Sm });
            else if (dirty)
                ui.Muted("Unsaved changes");

            ui.Spacer();

            ui.Switch("Enabled", ref binding.Enabled, new CheckOpts());
            ui.Gap(6);

            if (!builtIn && ui.Button("Test", new ButtonOpts { Variant = ButtonVariant.Ghost, Size = ControlSize.Sm }))
            {
                TestDraft();
            }
            if (!builtIn && ui.Button("Delete", new ButtonOpts
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
            DrawShortcutsHeader(ui);

            ui.Muted("Hotkeys stored in .lnk shortcuts on the desktop and in the Start menu. An edit is written " +
                     "into the shortcut file when it allows it; hotkeys that running programs register are " +
                     "invisible to Windows.");

            using (ui.Scroll(new ScrollOpts { Height = Size.Fill, Gap = 8, Pad = 2, FadeEdges = true }))
            {
                if (_shortcuts.Length == 0 && !_scanning) DrawNoShortcuts(ui);

                foreach (ShortcutInfo shortcut in _shortcuts) DrawShortcutCard(ui, shortcut);
            }
        }
    }

    private void DrawShortcutsHeader(Ui ui)
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
    }

    private static void DrawNoShortcuts(Ui ui)
    {
        using (ui.Card(new Card { Pad = 12, Gap = 6, Width = Size.Fill }))
        {
            ui.Label("No shortcut hotkeys");
            ui.Muted("No shortcut on the desktop or in the Start menu carries a hotkey yet.");
        }
    }

    private void DrawShortcutCard(Ui ui, ShortcutInfo shortcut)
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
            if (!ui.ShortcutRecorder("sc.combo", ref combo, new ShortcutRecorderOpts
            {
                Width = Size.Px(240),
                Placeholder = "No hotkey",
            }))
            {
                return;
            }

            string outcome = ShortcutStore.Write(shortcut, combo);
            bool saved = outcome.StartsWith("Saved", StringComparison.Ordinal);
            if (saved)
            {
                shortcut.Mods = combo.Mods;
                shortcut.HotKey = combo.Key;
            }

            SetMessage(outcome, saved ? Tone.Success : Tone.Danger);
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
            DrawActivityHeader(ui);

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
                    using (ui.Id(i)) DrawLogCard(ui, entries[i]);
                }
            }
        }
    }

    private void DrawActivityHeader(Ui ui)
    {
        using (ui.Row(new Box { Gap = 8, Align = Align.Center, Width = Size.Fill, PadX = 2 }))
        {
            ui.Label("Activity");
            ui.Spacer();
            if (ui.Button("Clear", new ButtonOpts { Variant = ButtonVariant.Outline, Size = ControlSize.Sm }))
                _log.Clear();
        }
    }

    private static void DrawLogCard(Ui ui, LogEntry entry)
    {
        using (ui.Card(new Card { PadX = 10, PadY = 8, Gap = 6, Width = Size.Fill }))
        {
            using (ui.Row(new Box { Gap = 8, Align = Align.Center, Width = Size.Fill }))
            {
                DrawResultBadge(ui, entry.Ok);
                ui.Label(entry.Binding);
                ui.Spacer();
                ui.Text(entry.When.ToString("HH:mm:ss"), new TextOpts(12, Color.TextMuted));
            }

            ui.Secondary(entry.Text);
        }
    }

    private static void DrawResultBadge(Ui ui, bool ok) =>
        ui.Badge(ok ? "ok" : "error", new BadgeOpts
        {
            Tone = ok ? Tone.Success : Tone.Danger,
            Size = ControlSize.Sm,
            Dot = true,
        });

    // -----------------------------------------------------------------------
    // Actions
    // -----------------------------------------------------------------------

    private void RefreshLauncher()
    {
        _launcherBridge.SetItems(_bindings);
    }

    private void AddBinding()
    {
        var binding = new Binding { Name = "New binding", Capture = CaptureMode.SelectedText };
        _bindings.Add(binding);
        Select(_bindings.Count - 1);
        RefreshLauncher();
        SetMessage("Record a hotkey, then Save.", Tone.Info);
    }

    private void AddDiskExample()
    {
        var binding = new Binding
        {
            Name = "Disk usage",
            Command = DiskExample,
            Capture = CaptureMode.SelectedFiles,
            Mods = (int)(Mods.Ctrl | Mods.Alt),
            Key = (int)Key.D,
        };
        _bindings.Add(binding);
        Select(_bindings.Count - 1);
        SaveDraft();
        RefreshLauncher();
    }

    private void UseDiskExample()
    {
        if (_draft is null) return;
        _draft.Name = "Disk usage";
        _draft.Command = DiskExample;
        _draft.Args = "";
        _draft.Capture = CaptureMode.SelectedFiles;
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

        LoadEnv(_draft?.Env ?? "");
        _message = "";
        _probe = "";
    }

    /// <summary>Loads the saved KEY=value text into the key-value editor's array.</summary>
    private void LoadEnv(string text)
    {
        _envCount = 0;
        foreach (EnvEntry entry in EnvText.Parse(text))
        {
            if (_envCount >= _envPairs.Length) break;
            _envPairs[_envCount++] = new KeyValue(entry.Key, entry.Value, entry.Enabled);
        }
        _envDirty = false;
    }

    /// <summary>Writes the editor's rows back to the saved KEY=value text format.</summary>
    private string SaveEnv()
    {
        var entries = new List<EnvEntry>(_envCount);
        for (int i = 0; i < _envCount; i++)
        {
            KeyValue pair = _envPairs[i];
            if (string.IsNullOrWhiteSpace(pair.Key)) continue;
            entries.Add(new EnvEntry(pair.Key!, pair.Value ?? "", pair.Enabled));
        }
        return EnvText.Format(entries);
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
               || _envDirty
               || _combo.Mods != saved.Mods
               || (int)_combo.Key != saved.Key;
    }

    private void SaveDraft()
    {
        if (_draft is null || _selected < 0) return;

        // The built-in has no external application: it opens the quick launcher instead.
        if (!_draft.BuiltIn && _draft.Command.Trim().Length == 0)
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
        _draft.Env = SaveEnv();
        _envDirty = false;
        _bindings[_selected] = _draft.Clone();
        string? saveError = ConfigStore.Save(_bindings);
        _host.SetBindings(_bindings);
        RefreshLauncher();
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
        if (_draft.BuiltIn)
        {
            SetMessage("The quick launcher is built in. You can disable it, but not delete it.", Tone.Warning);
            return;
        }

        string name = _bindings[_selected].Name;
        _bindings.RemoveAt(_selected);
        string? saveError = ConfigStore.Save(_bindings);
        _host.SetBindings(_bindings);
        RefreshLauncher();
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
            ShortcutInfo[] shortcuts;
            string? error = null;
            try
            {
                shortcuts = ShortcutStore.Scan().ToArray();
            }
            catch (Exception exception)
            {
                shortcuts = [];
                error = exception.Message;
            }

            // Hand the result to the UI thread: the array a frame reads is never replaced from
            // here, and the badge and the list change together on the next frame.
            _uiActions.Enqueue(() =>
            {
                _shortcuts = shortcuts;
                _scanning = false;
                if (error is not null) _log.Add("Shortcuts", error, false);
            });
            Wake();
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

    /// <summary>
    /// Called on the UI thread after a binding fired from either the hotkey thread or the launcher.
    /// The timestamp is persisted and the launcher's snapshot is refreshed for the next open.
    /// </summary>
    private void NoteTrigger(string id)
    {
        int index = _bindings.FindIndex(b => b.Id == id);
        if (index < 0) return;

        _bindings[index].LastAccess = DateTime.UtcNow.Ticks;
        ConfigStore.Save(_bindings);
        RefreshLauncher();
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

    private static string CaptureHint(CaptureMode mode) => mode switch
    {
        CaptureMode.SelectedText =>
            "Presses Ctrl+C in the foreground application, reads the copied text, then restores the clipboard.",
        CaptureMode.Clipboard =>
            "Reads the clipboard as it is, without touching the foreground application.",
        CaptureMode.SelectedFiles =>
            "Presses Ctrl+C in Explorer or on the desktop, reads the selected file and folder paths, then restores the clipboard.",
        _ => "Starts the application with only the arguments above.",
    };

    public void Dispose()
    {
        _tray?.Dispose();
        _tray = null;
        _launcher.Dispose();
        _host.Dispose();
    }
}

// ---------------------------------------------------------------------------
// Model and storage
// ---------------------------------------------------------------------------

internal enum CaptureMode
{
    None = 0,
    SelectedText = 1,
    Clipboard = 2,
    SelectedFiles = 3,
}

internal readonly record struct EnvEntry(string Key, string Value, bool Enabled);

/// <summary>The saved environment format: one KEY=value per line, with # disabling a row.</summary>
internal static class EnvText
{
    public static IEnumerable<EnvEntry> Parse(string text)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            bool enabled = !line.StartsWith('#');
            if (!enabled) line = line[1..].TrimStart();

            int equals = line.IndexOf('=');
            if (equals <= 0) continue;

            string key = line[..equals].Trim();
            if (key.Length == 0) continue;

            yield return new EnvEntry(key, line[(equals + 1)..].Trim(), enabled);
        }
    }

    public static string Format(IEnumerable<EnvEntry> entries)
    {
        var text = new StringBuilder();
        foreach (EnvEntry entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Key)) continue;
            if (!entry.Enabled) text.Append('#');
            text.Append(entry.Key).Append('=').Append(entry.Value).Append('\n');
        }
        return text.ToString();
    }
}

internal sealed class Binding
{
    // A stable id lets the launcher report a trigger back to the owner even after the list was
    // reordered or another binding was added; array indices are not stable across edits.
    public string Id = Guid.NewGuid().ToString("N");
    public string Name = "";
    public int Mods;
    public int Key;
    public string Command = "";
    public string Args = "";
    public string WorkDir = "";
    public string Env = "";
    public CaptureMode Capture = CaptureMode.SelectedText;
    public bool Enabled = true;

    // The quick launcher is a built-in binding: it can be disabled and re-keyed, never deleted.
    public bool BuiltIn;

    // Ticks of the last time this binding fired. The launcher orders by this (most recent first).
    public long LastAccess;

    public Binding Clone() => (Binding)MemberwiseClone();
}

internal sealed class Config
{
    public List<Binding> Bindings { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, IncludeFields = true)]
[JsonSerializable(typeof(Config))]
internal partial class ConfigJson : JsonSerializerContext
{
}

/// <summary>Where the settings and the activity log live: %APPDATA%\hotkey.</summary>
internal static class AppData
{
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hotkey");
}

internal static class ConfigStore
{
    private static string FilePath => Path.Combine(AppData.Folder, "bindings.json");

    public static List<Binding> Load()
    {
        List<Binding> bindings;
        try
        {
            if (!File.Exists(FilePath))
            {
                bindings = [];
            }
            else
            {
                string json = File.ReadAllText(FilePath);
                bindings = JsonSerializer.Deserialize(json, ConfigJson.Default.Config)?.Bindings ?? [];
            }
        }
        catch
        {
            bindings = [];
        }

        // The built-in is re-created when missing, so editing the JSON by hand cannot delete it.
        if (!bindings.Any(b => b.BuiltIn))
        {
            bindings.Insert(0, CreateBuiltIn());
            Save(bindings);
        }

        return bindings;
    }

    public static Binding CreateBuiltIn() => new()
    {
        Name = "Quick launcher",
        Mods = (int)Mods.Ctrl,
        Key = (int)Key.Space,
        Capture = CaptureMode.Clipboard,
        Enabled = true,
        BuiltIn = true,
    };

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
            string directory = AppData.Folder;
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
    private const int ProbeId = 0x3FFF;
    private const int ErrorAlreadyRegistered = 1409;

    // RegisterHotKey can report a press the keyboard hook already handled; ignore its copy when
    // it arrives right behind the hook's message.
    private const long HookDebounceMs = 250;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly List<int> _registered = [];
    private readonly ActivityLog _log;
    private readonly Func<nint> _window;

    private uint _threadId;
    private volatile nint _wakeWindow;
    private nint _keyboardHook;
    private Win32.KeyboardProc? _keyboardProc;
    private int _hookHeldVk = -1;
    private long _hookTick;
    private Binding[] _active = [];
    private string[] _status = [];
    private bool _disposed;

    public nint WakeWindow
    {
        set => _wakeWindow = value;
    }

    /// <summary>Shows the quick launcher immediately; the payload follows once it is captured.</summary>
    public Action? ShowLauncher { get; set; }

    /// <summary>Fills the launcher with the captured payload and gives it keyboard focus.</summary>
    public Action<Payload>? LauncherPayload { get; set; }

    /// <summary>Reports a binding id after it fired, for last-access bookkeeping on the UI thread.</summary>
    public Action<string>? Triggered { get; set; }

    /// <summary>The launcher window, whose own focus must still receive ordinary keys.</summary>
    public Func<nint>? LauncherHandle { get; set; }

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

        if (Win32.RegisterHotKey(0, ProbeId, KeyMap.ToWin32(combo.Mods) | HotkeyModifiers.NoRepeat, (uint)vk) != 0)
        {
            Win32.UnregisterHotKey(0, ProbeId);
            return ("Available", Tone.Success);
        }

        int error = Marshal.GetLastWin32Error();
        return error == ErrorAlreadyRegistered
            ? ("Taken by another application", Tone.Danger)
            : ($"Not available (error {error})", Tone.Warning);
    }

    private void Loop()
    {
        _threadId = Win32.GetCurrentThreadId();

        try
        {
            StartThread();
            PumpMessages();

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

    /// <summary>
    /// Creates this thread's message queue and installs the keyboard hook before announcing
    /// readiness: PostThreadMessage fails for a thread that has not made a queue yet, and the
    /// first SetBindings arrives immediately.
    /// </summary>
    private void StartThread()
    {
        Win32.PeekMessage(out _, 0, 0, 0, 0);

        // The hook runs on this thread, so the message loop is already in place. It swallows
        // the keystroke before the foreground app sees it and posts the trigger back here.
        _keyboardProc = OnKeyboardHook;
        _keyboardHook = Win32.SetWindowsHookEx(
            Win32.WhKeyboardLl, _keyboardProc, Win32.GetModuleHandle(null), 0);
        if (_keyboardHook == 0)
            _log.Add("Hotkey", "Could not install the key hook; hotkeys may reach other apps.", false);

        _ready.Set();
    }

    private void PumpMessages()
    {
        while (Win32.GetMessage(out Win32.MSG message, 0, 0, 0) > 0) Dispatch(message);
    }

    private void Dispatch(in Win32.MSG message)
    {
        if (message.message == Win32.WmApp)
        {
            Drain();
        }
        else if (message.message == Win32.WmHookHotkey)
        {
            _hookTick = Environment.TickCount64;
            OnHotkey((int)message.wParam);
        }
        else if (message.message == Win32.WmHotkey)
        {
            // RegisterHotKey can still report a press the hook already handled.
            if (Environment.TickCount64 - _hookTick >= HookDebounceMs) OnHotkey((int)message.wParam);
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
        for (int i = 0; i < bindings.Length; i++) status[i] = Register(bindings[i], i + 1);

        _status = status;
        Wake();
    }

    /// <summary>Registers one binding, logs the outcome, and returns the status shown in the list.</summary>
    private string Register(Binding binding, int id)
    {
        if (!binding.Enabled) return "Disabled";

        int vk = KeyMap.ToVk((Key)binding.Key);
        if (vk == 0 || binding.Mods == 0) return "Needs a hotkey";

        string combo = KeyMap.Text(binding.Mods, (Key)binding.Key);
        if (Win32.RegisterHotKey(0, id, KeyMap.ToWin32(binding.Mods) | HotkeyModifiers.NoRepeat, (uint)vk) != 0)
        {
            _registered.Add(id);
            _log.Add(binding.Name, $"Registered {combo}", true);
            return "Active";
        }

        int error = Marshal.GetLastWin32Error();
        _log.Add(binding.Name, error == ErrorAlreadyRegistered
            ? $"Could not register {combo}: another application already owns it"
            : $"Could not register {combo}: error {error}", false);
        return error == ErrorAlreadyRegistered ? "Taken by another app" : "Registration failed";
    }

    /// <summary>
    /// Called on the hotkey thread for every key event on the desktop. A combination belonging to
    /// an enabled binding is consumed here, so the foreground application never gets to act on it
    /// (Ctrl+Space is also an IME switch, for example). The trigger itself is posted back to this
    /// thread's loop so no real work happens inside the time-critical hook callback.
    /// </summary>
    private nint OnKeyboardHook(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && IsKeyEvent(wParam) && Consume(lParam, wParam)) return 1;
        return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private static bool IsKeyEvent(nint message) =>
        message == Win32.WmKeyDown || message == Win32.WmSysKeyDown
        || message == Win32.WmKeyUp || message == Win32.WmSysKeyUp;

    /// <summary>Returns true when the key belongs to a binding and must not reach the foreground app.</summary>
    private bool Consume(nint lParam, nint message)
    {
        Win32.KBDLLHOOKSTRUCT info = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);

        // Ignore keys we injected ourselves (the simulated Ctrl+C used for capture).
        if ((info.flags & Win32.LlkhfInjected) != 0) return false;

        int vk = (int)info.vkCode;
        bool down = message == Win32.WmKeyDown || message == Win32.WmSysKeyDown;

        if (!down)
        {
            if (vk == _hookHeldVk) _hookHeldVk = -1;
            return false;
        }

        int id = MatchBinding(vk, CurrentMods());
        if (id <= 0 || vk == _hookHeldVk) return false;

        _hookHeldVk = vk;
        nint foreground = Win32.GetForegroundWindow();
        nint own = _window();
        nint launcher = LauncherHandle?.Invoke() ?? 0;

        // In our own windows, leave keys alone so text fields keep their shortcuts.
        if ((own == 0 || foreground != own) && (launcher == 0 || foreground != launcher))
        {
            Win32.PostThreadMessage(_threadId, Win32.WmHookHotkey, (nint)id, 0);
            return true;
        }

        return false;
    }

    private int MatchBinding(int virtualKey, Mods mods)
    {
        Key key = KeyMap.FromVk(virtualKey);
        if (key == Key.None) return -1;

        Binding[] active = _active;
        for (int i = 0; i < active.Length; i++)
        {
            Binding binding = active[i];
            if (binding.Enabled && binding.Mods == (int)mods && binding.Key == (int)key)
                return i + 1;
        }
        return -1;
    }

    private static Mods CurrentMods()
    {
        Mods mods = 0;
        if (Win32.GetKeyState(VirtualKeys.Shift) < 0) mods |= Mods.Shift;
        if (Win32.GetKeyState(VirtualKeys.Control) < 0) mods |= Mods.Ctrl;
        if (Win32.GetKeyState(VirtualKeys.Menu) < 0) mods |= Mods.Alt;
        if (Win32.GetKeyState(VirtualKeys.LeftWindows) < 0
            || Win32.GetKeyState(VirtualKeys.RightWindows) < 0) mods |= Mods.Super;
        return mods;
    }

    private void OnHotkey(int id)
    {
        int index = id - 1;
        Binding[] active = _active;
        if ((uint)index >= (uint)active.Length) return;

        nint own = _window();
        bool ownForeground = own != 0 && Win32.GetForegroundWindow() == own;
        Binding binding = active[index];
        _log.Add(binding.Name, ownForeground ? "Hotkey pressed (this window is in front)" : "Hotkey pressed", true);

        if (binding.BuiltIn) FireBuiltIn(binding, own, ownForeground);
        else FireBinding(binding, own, ownForeground);

        Wake();
    }

    private void FireBuiltIn(Binding binding, nint own, bool ownForeground)
    {
        try
        {
            // Show the window first: a simulated Ctrl+C can wait hundreds of milliseconds
            // when the foreground application has no selection, and the launcher should not
            // be held back by that. Showing without activation leaves that application in
            // front, so its selection is still there to capture a moment later.
            ShowLauncher?.Invoke();

            Payload all;
            try
            {
                all = PayloadCapture.CaptureAll(own, allowCopy: !ownForeground, maxWaitMs: 120);
            }
            catch
            {
                all = Payload.Empty; // a failed capture must not leave the window unfocused
            }

            LauncherPayload?.Invoke(all);
            _log.Add(binding.Name, "Quick launcher opened", true);
        }
        catch (Exception error)
        {
            _log.Add(binding.Name, "Could not open the quick launcher: " + error.Message, false);
        }
    }

    private void FireBinding(Binding binding, nint own, bool ownForeground)
    {
        try
        {
            // Never send Ctrl+C into our own window. With our window in front there is no other
            // application's selection to read, so use the clipboard as it stands.
            Payload payload = PayloadCapture.Capture(binding.Capture, own, allowCopy: !ownForeground);
            string result = Launcher.Run(binding, payload);
            _log.Add(binding.Name, result, true);
            Triggered?.Invoke(binding.Id);
        }
        catch (Exception error)
        {
            _log.Add(binding.Name, error.Message, false);
        }
    }

    private void WakeHost()
    {
        if (_threadId != 0) Win32.PostThreadMessage(_threadId, Win32.WmApp, 0, 0);
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
        if (_keyboardHook != 0) Win32.UnhookWindowsHookEx(_keyboardHook);
        _keyboardProc = null;
        _ready.Dispose();
    }
}

// ---------------------------------------------------------------------------
// Capture: selected text, clipboard text, and file/folder paths
// ---------------------------------------------------------------------------

internal readonly record struct Payload(string Text, string[] Files)
{
    public static readonly string[] NoFiles = [];
    public static readonly Payload Empty = new("", NoFiles);
}

/// <summary>Reads and writes the two clipboard shapes this app understands: text and file lists.</summary>
internal static class Clipboard
{
    public static Payload Read()
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
                return new Payload(ReadText(), ReadFiles());
            }
            finally
            {
                Win32.CloseClipboard();
            }
        }

        return Payload.Empty;
    }

    /// <summary>
    /// Puts back what the clipboard held before the simulated Ctrl+C. Text and file lists are the
    /// two kinds this app reads, so they are the two it can restore; anything else (an image, say)
    /// stays replaced by the selection rather than being emptied out.
    /// </summary>
    public static void Write(Payload payload)
    {
        if (payload.Files.Length > 0) WriteFiles(payload.Files);
        else if (payload.Text.Length > 0) WriteText(payload.Text);
    }

    private static string ReadText()
    {
        nint handle = Win32.GetClipboardData(ClipboardFormat.UnicodeText);
        if (handle != 0) return ReadString(handle, Marshal.PtrToStringUni);

        handle = Win32.GetClipboardData(ClipboardFormat.Text);
        return handle == 0 ? "" : ReadString(handle, Marshal.PtrToStringAnsi);
    }

    private static string ReadString(nint handle, Func<nint, string?> read)
    {
        nint pointer = Win32.GlobalLock(handle);
        if (pointer == 0) return "";

        try
        {
            return read(pointer) ?? "";
        }
        finally
        {
            Win32.GlobalUnlock(handle);
        }
    }

    private static string[] ReadFiles()
    {
        nint handle = Win32.GetClipboardData(ClipboardFormat.HDrop);
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

    private static void WriteText(string text)
    {
        // The trailing NUL is part of the clipboard text block.
        WriteBlock(ClipboardFormat.UnicodeText, Encoding.Unicode.GetBytes(text + "\0"));
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

        WriteBlock(ClipboardFormat.HDrop, block);
    }

    private static void WriteBlock(ClipboardFormat format, byte[] block)
    {
        if (Win32.OpenClipboard(0) == 0) return;

        nint memory = 0;
        try
        {
            // GMEM_MOVEABLE | GMEM_ZEROINIT, so the terminators are already zero.
            memory = Win32.GlobalAlloc(Win32.GmemMoveable, (nuint)block.Length);
            if (memory == 0) return;

            nint target = Win32.GlobalLock(memory);
            if (target == 0) return;

            Marshal.Copy(block, 0, target, block.Length);
            Win32.GlobalUnlock(memory);

            Win32.EmptyClipboard();

            // SetClipboardData takes ownership of the block on success.
            if (Win32.SetClipboardData(format, memory) != 0) memory = 0;
        }
        finally
        {
            if (memory != 0) Win32.GlobalFree(memory);
            Win32.CloseClipboard();
        }
    }
}

internal static class PayloadCapture
{
    public static Payload Capture(CaptureMode mode, nint ownWindow, bool allowCopy)
    {
        if (mode == CaptureMode.None) return Payload.Empty;

        Payload payload = mode == CaptureMode.Clipboard
            ? Clipboard.Read()
            : CaptureAll(ownWindow, allowCopy); // ordinary bindings keep the 500 ms capture budget

        // A binding asks for one kind of selection: do not hand a text binding the file list
        // (or the reverse) just because the clipboard happened to carry both.
        return mode switch
        {
            CaptureMode.SelectedText => new Payload(payload.Text, Payload.NoFiles),
            CaptureMode.SelectedFiles => new Payload("", payload.Files),
            _ => payload,
        };
    }

    /// <summary>
    /// Captures everything the selection or clipboard holds, without narrowing it to text or
    /// files. The quick launcher uses this so its text area can edit either kind of payload.
    /// </summary>
    public static Payload CaptureAll(nint ownWindow, bool allowCopy, int maxWaitMs = 500)
    {
        if (!allowCopy) return Clipboard.Read();

        nint foreground = Win32.GetForegroundWindow();
        return foreground == 0 || foreground == ownWindow ? Clipboard.Read() : CopySelection(maxWaitMs);
    }

    private static Payload CopySelection(int maxWaitMs)
    {
        // Take a copy of what the clipboard holds now, so the simulated Ctrl+C does not cost the
        // user their clipboard. The copy is made with the plain clipboard API on purpose: the
        // object OleGetClipboard returns is backed by the clipboard itself, so handing it back to
        // OleSetClipboard and flushing makes OLE render it from the clipboard it is backing —
        // a recursion that overflows the stack inside ole32 and takes the process down.
        Payload previous = Clipboard.Read();

        try
        {
            uint sequence = Win32.GetClipboardSequenceNumber();
            SendCopy();

            // A foreground app with no selection never touches the clipboard. Waiting the full
            // half second on every such press is what made the quick launcher feel slow, so the
            // launcher asks for a short budget; an ordinary binding keeps the generous one.
            long until = Environment.TickCount64 + maxWaitMs;
            while (Environment.TickCount64 < until)
            {
                Thread.Sleep(10);
                if (Win32.GetClipboardSequenceNumber() != sequence) break;
            }

            return Clipboard.Read();
        }
        finally
        {
            Clipboard.Write(previous);
        }
    }

    private static void SendCopy()
    {
        var inputs = new Win32.INPUT[9];
        int n = 0;

        // The hotkey's own modifiers are still physically down. Release them first, or the
        // application receives Ctrl+Alt+C instead of Ctrl+C.
        inputs[n++] = KeyInput(VirtualKeys.Control, up: true);
        inputs[n++] = KeyInput(VirtualKeys.Menu, up: true);
        inputs[n++] = KeyInput(VirtualKeys.Shift, up: true);
        inputs[n++] = KeyInput(VirtualKeys.LeftWindows, up: true);
        inputs[n++] = KeyInput(VirtualKeys.RightWindows, up: true);
        inputs[n++] = KeyInput(VirtualKeys.Control, up: false);
        inputs[n++] = KeyInput(VirtualKeys.C, up: false);
        inputs[n++] = KeyInput(VirtualKeys.C, up: true);
        inputs[n++] = KeyInput(VirtualKeys.Control, up: true);

        uint inserted = Win32.SendInput((uint)n, inputs, Unsafe.SizeOf<Win32.INPUT>());
        if (inserted != (uint)n)
        {
            throw new InvalidOperationException(
                "Windows blocked the simulated Ctrl+C. The foreground application may be running elevated.");
        }
    }

    private static Win32.INPUT KeyInput(int virtualKey, bool up) => new()
    {
        type = Win32.InputKeyboard,
        U = new Win32.InputUnion
        {
            ki = new Win32.KEYBDINPUT
            {
                wVk = (ushort)virtualKey,
                dwFlags = up ? Win32.KeyEventKeyUp : 0u,
            },
        },
    };
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

        foreach (string argument in BuildArguments(binding.Args, payload, workDir, CapturedDirectory(payload)))
            start.ArgumentList.Add(argument);

        // A ProcessStartInfo with UseShellExecute = false is seeded with this process's whole
        // environment, which is the system and user variables. Keep that as the base: only the
        // rows the binding names are overridden or appended below.
        foreach (EnvEntry entry in EnvText.Parse(binding.Env))
        {
            if (!entry.Enabled) continue;

            string key = entry.Key;
            if (key.EndsWith('+'))
            {
                string name = key[..^1].TrimEnd();
                start.Environment.TryGetValue(name, out string? existing);
                string extra = Environment.ExpandEnvironmentVariables(entry.Value);
                start.Environment[name] = string.IsNullOrEmpty(existing) ? extra : existing + ";" + extra;
            }
            else
            {
                start.Environment[key] = Environment.ExpandEnvironmentVariables(entry.Value);
            }
        }

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

                    Win32.ShowWindow(window, ShowWindowCommand.Restore);
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

    /// <summary>
    /// The folder a binding means by {dir}: a selected folder itself, or the parent of a selected
    /// file. Empty when the capture carried no files, so the caller can fall back to WorkDir.
    /// </summary>
    private static string CapturedDirectory(Payload payload)
    {
        if (payload.Files.Length == 0) return "";

        string first = payload.Files[0];
        if (Directory.Exists(first)) return first;
        return Path.GetDirectoryName(first) ?? "";
    }

    private static List<string> BuildArguments(string template, Payload payload, string workDir, string capturedDir)
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
            {
                // A captured selection is consumed by {dir} just like {file}: substituting it
                // must not also append the raw path at the end of the argument list.
                argument = argument.Replace("{dir}",
                    capturedDir.Length > 0 ? capturedDir : workDir, StringComparison.Ordinal);
                if (capturedDir.Length > 0) usedFiles = true;
            }

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
}

// ---------------------------------------------------------------------------
// Shortcut files (.lnk): read and write the hotkey field in the header
// ---------------------------------------------------------------------------

internal sealed class ShortcutInfo
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Folder { get; init; }
    public int Mods { get; set; }
    public Key HotKey { get; set; }
    public bool Writable { get; init; }
}

internal static class ShortcutStore
{
    // The shell link header: 0x4C bytes, the CLSID at offset 4, and the hotkey field at 0x40.
    private const int HeaderSize = 0x4C;
    private const int HotKeyOffset = 0x40;
    private const uint AllModifiers = (uint)(HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Shift);

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

        if ((combo.Mods & (int)Mods.Super) != 0)
            return "Windows-key hotkeys cannot be stored in a shortcut.";

        ushort value = combo.Key == Key.None
            ? (ushort)0
            : (ushort)(((uint)KeyMap.ToWin32(combo.Mods) & AllModifiers) << 8 | (uint)virtualKey);

        try
        {
            using var stream = new FileStream(shortcut.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            stream.Seek(HotKeyOffset, SeekOrigin.Begin);
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
            if (stream.Length < HeaderSize) return false;

            Span<byte> header = stackalloc byte[HeaderSize];
            stream.ReadExactly(header);

            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != HeaderSize) return false;
            if (!header.Slice(4, 16).SequenceEqual(LinkClsid)) return false;

            ushort hotkey = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(HotKeyOffset, 2));
            int virtualKey = hotkey & 0xFF;
            if (virtualKey == 0) return false;

            key = KeyMap.FromVk(virtualKey);
            if (key == Key.None) return false;

            mods = KeyMap.FromWin32((HotkeyModifiers)((hotkey >> 8) & 0x07));
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

/// <summary>
/// The modifier bits Windows understands. They deliberately do not line up with CuiSharp's
/// <see cref="Mods"/> values, so every conversion goes through <see cref="KeyMap"/>.
/// </summary>
[Flags]
internal enum HotkeyModifiers : uint
{
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
    NoRepeat = 0x4000,
}

/// <summary>Virtual-key codes this app asks about by name, as opposed to the full <see cref="KeyMap"/> table.</summary>
internal static class VirtualKeys
{
    public const int Shift = 0x10;
    public const int Control = 0x11;
    public const int Menu = 0x12;
    public const int LeftWindows = 0x5B;
    public const int RightWindows = 0x5C;
    public const int C = 0x43;
}

internal static class KeyMap
{
    private readonly record struct KeyInfo(Key Key, string Name, int VirtualKey);

    // One table is the single source of truth for the three things a key needs: its CuiSharp
    // value, the name shown in the UI, and the Windows virtual-key code.
    private static readonly KeyInfo[] Keys = BuildKeys();

    private static readonly FrozenDictionary<Key, KeyInfo> ByKey =
        Keys.ToFrozenDictionary(info => info.Key);

    private static readonly FrozenDictionary<int, Key> ByVirtualKey =
        Keys.ToFrozenDictionary(info => info.VirtualKey, info => info.Key);

    private static readonly (int CuiSharp, HotkeyModifiers Windows, string Name)[] Modifiers =
    [
        ((int)Mods.Ctrl, HotkeyModifiers.Control, "Ctrl"),
        ((int)Mods.Alt, HotkeyModifiers.Alt, "Alt"),
        ((int)Mods.Shift, HotkeyModifiers.Shift, "Shift"),
        ((int)Mods.Super, HotkeyModifiers.Win, "Win"),
    ];

    public static string Text(int mods, Key key)
    {
        if (key == Key.None) return "None";

        var text = new StringBuilder();
        foreach (var modifier in Modifiers)
            if ((mods & modifier.CuiSharp) != 0) text.Append(modifier.Name).Append('+');

        text.Append(Name(key));
        return text.ToString();
    }

    public static string Name(Key key) =>
        ByKey.TryGetValue(key, out KeyInfo info) ? info.Name : "?";

    public static HotkeyModifiers ToWin32(int mods)
    {
        HotkeyModifiers result = 0;
        foreach (var modifier in Modifiers)
            if ((mods & modifier.CuiSharp) != 0) result |= modifier.Windows;
        return result;
    }

    public static int FromWin32(HotkeyModifiers mods)
    {
        int result = 0;
        foreach (var modifier in Modifiers)
            if ((mods & modifier.Windows) != 0) result |= modifier.CuiSharp;
        return result;
    }

    public static int ToVk(Key key) =>
        ByKey.TryGetValue(key, out KeyInfo info) ? info.VirtualKey : 0;

    public static Key FromVk(int virtualKey) =>
        ByVirtualKey.TryGetValue(virtualKey, out Key key) ? key : Key.None;

    private static KeyInfo[] BuildKeys()
    {
        var keys = new List<KeyInfo>(26 + 10 + 12 + 19);

        for (int i = 0; i < 26; i++)
            keys.Add(new((Key)((int)Key.A + i), ((char)('A' + i)).ToString(), 0x41 + i));

        for (int i = 0; i < 10; i++)
            keys.Add(new((Key)((int)Key.D0 + i), ((char)('0' + i)).ToString(), 0x30 + i));

        for (int i = 0; i < 12; i++)
            keys.Add(new((Key)((int)Key.F1 + i), "F" + (i + 1), 0x70 + i));

        keys.AddRange(
        [
            new(Key.Tab, "Tab", 0x09),
            new(Key.Enter, "Enter", 0x0D),
            new(Key.Escape, "Esc", 0x1B),
            new(Key.Backspace, "Backspace", 0x08),
            new(Key.Delete, "Delete", 0x2E),
            new(Key.Space, "Space", 0x20),
            new(Key.Left, "Left", 0x25),
            new(Key.Right, "Right", 0x27),
            new(Key.Up, "Up", 0x26),
            new(Key.Down, "Down", 0x28),
            new(Key.Home, "Home", 0x24),
            new(Key.End, "End", 0x23),
            new(Key.PageUp, "PageUp", 0x21),
            new(Key.PageDown, "PageDown", 0x22),
            new(Key.Minus, "-", 0xBD),
            new(Key.Equal, "=", 0xBB),
            new(Key.Comma, ",", 0xBC),
            new(Key.Period, ".", 0xBE),
            new(Key.Slash, "/", 0xBF),
        ]);

        return keys.ToArray();
    }
}

// ---------------------------------------------------------------------------
// Tray icon: closing the window hides it instead of quitting, so the hotkeys
// keep working; the tray brings the window back or exits for real.
// ---------------------------------------------------------------------------

internal sealed class TrayIcon : IDisposable
{
    private const int OpenCommand = 1;
    private const int ExitCommand = 2;

    private readonly nint _hwnd;
    private readonly WindowProcedure _hook;
    private nint _icon;
    private bool _added;
    private bool _exiting;

    public TrayIcon(nint hwnd)
    {
        _hwnd = hwnd;
        _hook = new WindowProcedure(hwnd, OnMessage);
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
            uFlags = Win32.NifMessage | Win32.NifIcon | Win32.NifTip,
            uCallbackMessage = Win32.TrayMessage,
            hIcon = _icon,
            szTip = "Hotkey — click to open, right-click to exit",
        };

        _added = Win32.Shell_NotifyIconW(Win32.NimAdd, ref data) != 0;
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

        Win32.Shell_NotifyIconW(Win32.NimDelete, ref data);
        _added = false;
    }

    private nint OnMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        // The window's close button hides it: the hotkeys live on their own thread and keep
        // firing, and the tray icon is the way back. Without an icon the window must close for
        // real, or the app would be left running with no way to reach it.
        if (message == Win32.WmClose && !_exiting && _added)
        {
            Win32.ShowWindow(hwnd, ShowWindowCommand.Hide);
            return 0;
        }

        if (message == Win32.TrayMessage)
        {
            int mouse = (int)(lParam & 0xFFFF);
            if (mouse is Win32.WmLButtonUp or Win32.WmLButtonDblClk) Open();
            else if (mouse == Win32.WmRButtonUp) Menu();
            return 0;
        }

        return _hook.CallPrevious(hwnd, message, wParam, lParam);
    }

    private void Open()
    {
        Win32.ShowWindow(_hwnd,
            Win32.IsIconic(_hwnd) != 0 ? ShowWindowCommand.Restore : ShowWindowCommand.Show);
        Win32.SetForegroundWindow(_hwnd);
    }

    private void Menu()
    {
        nint menu = Win32.CreatePopupMenu();
        Win32.AppendMenuW(menu, Win32.MfString, OpenCommand, "Open Hotkey");
        Win32.AppendMenuW(menu, Win32.MfSeparator, 0, null);
        Win32.AppendMenuW(menu, Win32.MfString, ExitCommand, "Exit");

        Win32.GetCursorPos(out Win32.POINT point);

        // Without foregrounding first, the menu stays open when the user clicks elsewhere.
        Win32.SetForegroundWindow(_hwnd);
        int command = Win32.TrackPopupMenu(menu, Win32.TpmRightButton | Win32.TpmReturnCmd,
            point.X, point.Y, 0, _hwnd, 0);
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
        Win32.PostMessage(_hwnd, Win32.WmClose, 0, 0);
    }

    public void Dispose()
    {
        Remove();
        _hook.Dispose();

        if (_icon != 0)
        {
            Win32.DestroyIcon(_icon);
            _icon = 0;
        }
    }
}

// ---------------------------------------------------------------------------
// Quick launcher: the built-in binding opens this second, small window.
// ---------------------------------------------------------------------------

internal sealed class LauncherItem
{
    public required string Id { get; init; }
    public required Binding Binding { get; init; }   // a clone; the launcher never edits the live model
    public required string Name { get; init; }
    public required string Combo { get; init; }
}

/// <summary>
/// The only state shared between the manager window and the quick-launcher window. The snapshot
/// is replaced, never mutated, so the launcher can read it without holding the lock while drawing.
/// </summary>
internal sealed class LauncherBridge
{
    private readonly object _gate = new();
    private LauncherItem[] _items = [];
    private readonly ConcurrentQueue<string> _triggered = new();

    public Action? WakeMain { get; set; }

    public void SetItems(IReadOnlyList<Binding> bindings)
    {
        var items = new List<LauncherItem>(bindings.Count);
        foreach (Binding b in bindings)
        {
            if (b.BuiltIn || !b.Enabled || b.Command.Trim().Length == 0) continue;
            string combo = KeyMap.Text(b.Mods, (Key)b.Key);
            items.Add(new LauncherItem
            {
                Id = b.Id,
                Binding = b.Clone(),
                Name = b.Name.Length > 0 ? b.Name : "Untitled",
                Combo = combo,
            });
        }

        // Most recently used first; bindings that never fired keep their config order at the end.
        items.Sort((a, b) => b.Binding.LastAccess.CompareTo(a.Binding.LastAccess));
        lock (_gate) _items = items.ToArray();
    }

    public LauncherItem[] Items
    {
        get { lock (_gate) return _items; }
    }

    public void EnqueueTriggered(string id)
    {
        _triggered.Enqueue(id);
        WakeMain?.Invoke();
    }

    public string[] DrainTriggered()
    {
        var ids = new List<string>();
        while (_triggered.TryDequeue(out string? id)) ids.Add(id);
        return ids.ToArray();
    }
}

internal sealed class LauncherWindow : IDisposable
{
    // Matches Win32.ColorKey: the theme background the window is keyed out against.
    private static readonly Color TransparentKey = Color.Rgb(0x0B0B0B);

    private const string EditCommand = "Edit captured text";

    private readonly LauncherBridge _bridge;
    private readonly ActivityLog _log;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly object _gate = new();

    private CuiWindow? _window;
    private nint _hwnd;
    private nint _previousForeground;
    private WindowProcedure? _hook;
    private volatile bool _armed;
    private bool _started;
    private bool _disposed;

    // Handed from the hotkey thread to the launcher thread under _gate. Draw copies them into
    // locals, then writes them back at the end of the frame.
    private string _payloadText = "";
    private bool _hadFiles;
    private int _generation;
    private LauncherView _view = LauncherView.Palette;

    private enum LauncherView { Palette, Edit }

    // The launcher owns the search and the selection. The snapshot is re-read only when the
    // bridge publishes a new array; the filter order and scores are reused frame to frame.
    private LauncherItem[] _items = [];
    private int[] _order = [];
    private int[] _scores = [];
    private string _query = "";
    private int _highlight;
    private int _drawnGeneration = -1;
    private int _viewEpoch;

    // Tab never reaches KeyPressed: CUI spends it on focus traversal first. Watching the
    // physical key gives one edge per press, so Shift+Tab switches view exactly once.
    private bool _tabHeld;

    public LauncherWindow(LauncherBridge bridge, ActivityLog log)
    {
        _bridge = bridge;
        _log = log;
        _thread = new Thread(Loop) { IsBackground = true, Name = "quick-launcher" };
        if (OperatingSystem.IsWindows()) _thread.SetApartmentState(ApartmentState.STA);
    }

    public nint Handle => _hwnd;

    /// <summary>Creates the hidden window ahead of the first hotkey so opening feels instant.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _started) return;
            _started = true;
        }
        _thread.Start();
    }

    /// <summary>
    /// Shows the window without stealing focus. This returns in a couple of milliseconds; the
    /// foreground application keeps its selection so it can still be captured afterwards.
    /// </summary>
    public void Show()
    {
        if (_disposed) return;

        // Start() runs when the main window is created; this is only a guard for a hotkey that
        // somehow arrives while that window is still coming up.
        try { _ready.Wait(TimeSpan.FromSeconds(2)); }
        catch (ObjectDisposedException) { return; }

        lock (_gate)
        {
            if (_disposed) return;

            // Clear the previous capture now; Complete fills in the real text a moment later.
            _payloadText = "";
            _hadFiles = false;
            _view = LauncherView.Palette;

            // A new generation gives the search box and the text area fresh state.
            _generation++;
            _previousForeground = Win32.GetForegroundWindow();
            _armed = false;
        }

        nint hwnd = _hwnd;
        if (hwnd == 0) return;
        CenterOnScreen(hwnd);
        Win32.ShowWindow(hwnd, ShowWindowCommand.ShowNoActivate);
        Win32.PostMessage(hwnd, 0, 0, 0); // wake the idle loop so the first frame is prompt
    }

    /// <summary>Delivers the captured selection and makes the window ready for typing.</summary>
    public void Complete(Payload payload)
    {
        nint hwnd = _hwnd;
        if (hwnd == 0) return;

        lock (_gate)
        {
            if (_disposed) return;
            _hadFiles = payload.Files.Length > 0;
            _payloadText = _hadFiles ? string.Join("\n", payload.Files) : payload.Text;
        }

        Win32.SetForegroundWindow(hwnd);
        Win32.PostMessage(hwnd, 0, 0, 0); // redraw with the captured text
    }

    private nint OnMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        // Arm on real focus, not on Show(): the window is first shown without activation so the
        // foreground application keeps its selection long enough to be captured.
        if (message == Win32.WmSetFocus)
            _armed = true;
        else if (message == Win32.WmKillFocus && _armed)
        {
            _armed = false;
            Close(restoreFocus: false);
        }

        // CUI still needs the message to keep its own focus state straight.
        return _hook?.CallPrevious(hwnd, message, wParam, lParam) ?? 0;
    }

    /// <summary>Centers the window in the work area of the monitor the cursor is on.</summary>
    private static void CenterOnScreen(nint hwnd)
    {
        if (!Win32.GetWindowRect(hwnd, out Win32.RECT rect)) return;

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return;

        Win32.GetCursorPos(out Win32.POINT cursor);
        nint monitor = Win32.MonitorFromPoint(cursor, MonitorDefault.ToNearest);
        if (monitor == 0) return;

        var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfoW(monitor, ref info)) return;

        int workWidth = info.rcWork.Right - info.rcWork.Left;
        int workHeight = info.rcWork.Bottom - info.rcWork.Top;
        if (workWidth <= 0 || workHeight <= 0) return;

        int x = info.rcWork.Left + (workWidth - width) / 2;
        int y = info.rcWork.Top + (workHeight - height) / 2;

        // Move and raise to the top of the z-order (insertAfter = HWND_TOP = 0) so the window is
        // visible right away, but do not activate: the foreground app must keep its selection
        // until the capture in Complete() has read it.
        Win32.SetWindowPos(hwnd, 0, x, y, 0, 0,
            SetWindowPosFlags.NoSize | SetWindowPosFlags.NoActivate);
    }

    private void Loop()
    {
        try
        {
            CuiApp.Run(Options(), Draw);
        }
        catch (Exception error)
        {
            _log.Add("Quick launcher", "Window failed: " + error.Message, false);
            _ready.Set();
        }
    }

    private CuiAppOptions Options() => new()
    {
        Title = "Quick launcher",
        Width = 380,
        Height = 340,   // the palette panel is ~262 high plus its top offset
        MinWidth = 240,
        MinHeight = 240,
        Theme = Theme.Dark,
        Accent = Accent.Violet,
        Hidden = true,
        WindowCreated = PrepareWindow,
    };

    private void PrepareWindow(CuiWindow window)
    {
        _window = window;
        _hwnd = window.NativeWindow;

        // Watch WM_KILLFOCUS so clicking another window dismisses the launcher.
        _hook = new WindowProcedure(_hwnd, OnMessage);
        ApplyLauncherStyle(_hwnd);

        _ready.Set();
    }

    /// <summary>
    /// Makes the window a borderless, transparent canvas: the palette draws its own panel, so the
    /// black framebuffer around it must not show as a rectangle over the desktop.
    /// </summary>
    private static void ApplyLauncherStyle(nint hwnd)
    {
        int style = Win32.GetWindowLong(hwnd, WindowLong.Style);
        style &= ~(int)(WindowStyle.Border | WindowStyle.Caption
                        | WindowStyle.ThickFrame | WindowStyle.SysMenu);
        Win32.SetWindowLongPtr(hwnd, WindowLong.Style, style);

        int exStyle = Win32.GetWindowLong(hwnd, WindowLong.ExStyle);
        Win32.SetWindowLongPtr(hwnd, WindowLong.ExStyle, exStyle | Win32.WsExLayered);
        Win32.SetLayeredWindowAttributes(hwnd, Win32.ColorKey, 0, Win32.LwaColorKey);

        Win32.SetWindowPos(hwnd, 0, 0, 0, 0, 0,
            SetWindowPosFlags.NoSize | SetWindowPosFlags.NoMove
            | SetWindowPosFlags.NoZOrder | SetWindowPosFlags.FrameChanged);
    }

    private void Draw(Ui ui)
    {
        ui.UseSystemFontFallbacks();

        // The launcher retunes the shared theme for its small window: square panel and spotlight
        // corners, the manager's control scale, and motion on for the spotlight beam.
        ThemeStyle style = ui.Style;
        style.RadiusControl = 8;
        style.RadiusPanel = 0;
        style.RadiusSurface = 12;
        style.ControlMd = 34;
        style.ControlSm = 28;
        style.Motion = 1;

        // The selected row takes a hint of the accent, so the palette reads as one piece; the
        // stock neutral overlay would leave it looking like just another grey.
        style[Token.Active] = Color.Rgb(0x2E2A3C);

        // The palette shows focus through the highlighted row and the flat fields, so a ring
        // drawn around whichever control has the keyboard is noise rather than a cue.
        style[Token.FocusRing] = Color.Transparent;

        // The spotlight paints a SURFACE background; key it out so only its glow and border show.
        // The token is restored as soon as the card begins, before the panel and its text area draw.
        Color surface = style[Token.Surface];
        style[Token.Background] = TransparentKey;

        LauncherView view;
        string payloadText;
        bool hadFiles;
        int generation;
        lock (_gate)
        {
            view = _view;
            payloadText = _payloadText;
            hadFiles = _hadFiles;
            generation = _generation;
        }

        // A hotkey press starts a fresh search and selection.
        if (generation != _drawnGeneration)
        {
            _drawnGeneration = generation;
            _query = "";
            _highlight = 0;
            _tabHeld = false;
        }

        bool tabDown = ui.KeyDown(Key.Tab);
        bool tabPressed = tabDown && !_tabHeld;
        _tabHeld = tabDown;
        bool shift = (ui.Modifiers & Mods.Shift) != 0;

        bool close = false;
        string triggeredId = "";
        bool editRequested = false;
        bool returningToPalette = false;

        // The launcher window is the palette: one spotlight and one panel fill it with a 1 point
        // inset. Both views share that box, so switching between them moves nothing.
        using (ui.Box(new Box { Bg = TransparentKey, Width = Size.Fill, Height = Size.Fill }))
        {
            // Read Enter, Esc and the view switch before a field or the text area can consume them.
            if (view == LauncherView.Edit)
            {
                if (ui.KeyPressed(Key.Escape)) close = true;
                else if (ui.KeyPressed(Key.Enter) || (tabPressed && !shift))
                {
                    view = LauncherView.Palette;
                    returningToPalette = true;
                }
            }
            else if (ui.KeyPressed(Key.Escape))
            {
                close = true;
            }
            else if (tabPressed && shift)
            {
                editRequested = true;
            }

            if (!close)
            {
                style[Token.Surface] = TransparentKey;

                using (ui.Spotlight("launcher-spotlight", new SpotlightOpts
                {
                    Width = Size.Fill,
                    Height = Size.Fill,
                    Pad = 1,
                    Beam = true,
                    Color = Color.Accent,
                }))
                {
                    style[Token.Surface] = surface;

                    using (ui.Column(new Box
                    {
                        Width = Size.Fill,
                        Height = Size.Fill,
                        Bg = Color.SurfaceRaised,
                        Border = 1,
                        BorderColor = Color.Border,
                        Radius = 0,
                        Clip = true,
                    }))
                    using (ui.Id(generation * 8 + _viewEpoch))
                    {
                        if (view == LauncherView.Edit)
                        {
                            DrawEditHeader(ui, hadFiles);
                            DrawSeparator(ui);
                            DrawEditBody(ui, ref payloadText);
                            DrawSeparator(ui);
                            DrawEditFooter(ui);
                        }
                        else
                        {
                            // The Enter that left the editor must not also select a row on the
                            // first palette frame, or the two views would bounce.
                            DrawPalette(ui, ref triggeredId, ref editRequested,
                                acceptEnter: !returningToPalette);
                        }
                    }
                }
            }

            // Leave a keyed framebuffer behind, so the next Show does not flash the last panel.
            if (close || triggeredId.Length > 0)
            {
                using (ui.Box(new Box { Width = Size.Fill, Height = Size.Fill, Bg = TransparentKey }))
                { }
            }
        }

        if (editRequested) view = LauncherView.Edit;

        lock (_gate)
        {
            if (view != _view) _viewEpoch++;
            _view = view;
            _payloadText = payloadText;
        }

        if (triggeredId.Length > 0)
        {
            Trigger(triggeredId, payloadText);
            Close(restoreFocus: false);
        }
        else if (close)
        {
            Close(restoreFocus: true);
        }
    }

    /// <summary>Search field, filtered rows and key hints: the launcher's default view.</summary>
    private void DrawPalette(Ui ui, ref string triggeredId, ref bool editRequested, bool acceptEnter)
    {
        bool up = ui.KeyPressed(Key.Up);
        bool down = ui.KeyPressed(Key.Down);
        bool enter = acceptEnter && ui.KeyPressed(Key.Enter);

        using (ui.Box(new Box { Width = Size.Fill, PadX = 10, PadY = 8 }))
        {
            // The focused field's border mixes toward the accent, and its caret and selection use
            // that same token. Point the accent at the resting border colour for this one call, so
            // focus adds no glowing outline; the rest of the launcher keeps its violet accent.
            ThemeStyle style = ui.Style;
            Color accent = style[Token.Accent];
            style[Token.Accent] = style[Token.BorderStrong];

            InputResult result = ui.Input("query", ref _query, new InputOpts
            {
                AutoFocus = true,
                Width = Size.Fill,
                Size = ControlSize.Lg,
                Placeholder = "Type a command or search",
                Icon = Icons.Search,
                Clearable = true,
            });

            style[Token.Accent] = accent;
            if (result.Changed) _highlight = 0;
        }

        DrawSeparator(ui);

        int count = Filter();
        bool hasEdit = Fuzzy(EditCommand, _query) >= 0;
        int total = count + (hasEdit ? 1 : 0);

        if (total == 0) _highlight = 0;
        else
        {
            if (up) _highlight = (_highlight + total - 1) % total;
            if (down) _highlight = (_highlight + 1) % total;
            if (_highlight >= total) _highlight = total - 1;
        }

        bool chosen = false;
        using (ui.Scroll(new ScrollOpts
        {
            Id = "rows",
            Height = Size.Fill,
            Pad = 4,
            Gap = 2,
        }))
        {
            if (total == 0)
            {
                using (ui.Box(new Box { Width = Size.Fill, Pad = 28, Align = Align.Center }))
                    ui.Muted("No results found");
            }

            for (int j = 0; j < count; j++)
            {
                LauncherItem item = _items[_order[j]];
                using (ui.Id(_order[j]))
                {
                    if (ui.ListItem(item.Name, new ListItemOpts
                    {
                        Icon = Icons.Terminal,
                        Trailing = item.Combo,
                        Selected = j == _highlight,
                    }))
                    {
                        _highlight = j;
                        triggeredId = item.Id;
                        chosen = true;
                    }
                }
            }

            if (hasEdit)
            {
                using (ui.Id("edit-captured-text"))
                {
                    if (ui.ListItem(EditCommand, new ListItemOpts
                    {
                        Icon = Icons.Pencil,
                        Trailing = "Shift+Tab",
                        Selected = count == _highlight,
                    }))
                    {
                        _highlight = count;
                        editRequested = true;
                        chosen = true;
                    }
                }
            }
        }

        if (enter && !chosen && total > 0)
        {
            if (_highlight < count) triggeredId = _items[_order[_highlight]].Id;
            else editRequested = true;
        }

        DrawSeparator(ui);
        DrawPaletteFooter(ui);
    }

    /// <summary>Takes the newest MRU snapshot and resizes the filter arrays to match it.</summary>
    private void RefreshItems()
    {
        LauncherItem[] items = _bridge.Items;
        if (ReferenceEquals(items, _items)) return;

        _items = items;
        _order = new int[items.Length];
        _scores = new int[items.Length];
    }

    /// <summary>
    /// Applies the fuzzy search to the MRU snapshot. Order and scores live in preallocated arrays,
    /// so a steady-state frame does not allocate.
    /// </summary>
    private int Filter()
    {
        RefreshItems();

        int count = 0;
        for (int i = 0; i < _items.Length; i++)
        {
            int score = Fuzzy(_items[i].Name, _query);
            if (score < 0) continue;
            _order[count] = i;
            _scores[count] = score;
            count++;
        }

        // Best match first, keeping the MRU order on ties, like the built-in palette.
        if (_query.Length > 0)
        {
            for (int a = 1; a < count; a++)
            {
                int order = _order[a];
                int score = _scores[a];
                int b = a - 1;
                while (b >= 0 && _scores[b] < score)
                {
                    _order[b + 1] = _order[b];
                    _scores[b + 1] = _scores[b];
                    b--;
                }
                _order[b + 1] = order;
                _scores[b + 1] = score;
            }
        }

        return count;
    }

    /// <summary>
    /// Subsequence match with the built-in palette's score: word starts and runs score higher.
    /// Returns -1 when the query does not match.
    /// </summary>
    private static int Fuzzy(string text, string query)
    {
        if (query.Length == 0) return 1;

        int score = 0;
        int run = 0;
        int t = 0;
        foreach (char raw in query)
        {
            char q = char.ToLowerInvariant(raw);
            if (q == ' ') continue;

            bool found = false;
            while (t < text.Length)
            {
                char c = char.ToLowerInvariant(text[t]);
                bool wordStart = t == 0 || text[t - 1] == ' ';
                t++;
                if (c == q)
                {
                    found = true;
                    run++;
                    score += 1 + run + (wordStart ? 3 : 0);
                    break;
                }
                run = 0;
            }

            if (!found) return -1;
        }

        return score;
    }

    private static void DrawEditHeader(Ui ui, bool hadFiles)
    {
        using (ui.Row(new Box
        {
            Width = Size.Fill,
            Height = Size.Px(60),
            PadX = 14,
            Gap = 10,
            Align = Align.Center,
        }))
        {
            ui.Text(hadFiles ? Icons.Grid3x3 : Icons.Pencil, new TextOpts(18, Color.Accent));
            using (ui.Column(new Box { Gap = 1 }))
            {
                ui.Text("Edit captured text", new TextOpts(15, Color.Foreground));
                ui.Muted(hadFiles
                    ? "One file or folder path per line."
                    : "Selected text, or the clipboard as it stands.");
            }
        }
    }

    private static void DrawEditBody(Ui ui, ref string payloadText)
    {
        using (ui.Box(new Box { Width = Size.Fill, Height = Size.Fill, Pad = 12 }))
        {
            // Same flat field as the search box: the accent would otherwise outline the area
            // whenever it holds the keyboard.
            ThemeStyle style = ui.Style;
            Color accent = style[Token.Accent];
            style[Token.Accent] = style[Token.BorderStrong];

            ui.TextArea("payload", ref payloadText, new InputOpts
            {
                AutoFocus = true,
                Width = Size.Fill,
                Height = -1,
                Placeholder = "Nothing captured",
            });

            style[Token.Accent] = accent;
        }
    }

    private static void DrawPaletteFooter(Ui ui)
    {
        using (ui.Row(new Box
        {
            Width = Size.Fill,
            PadX = 14,
            PadY = 10,
            Gap = 16,
            Wrap = true,
            Bg = Color.SurfaceMuted,
        }))
        {
            Hint(ui, "↑ ↓", "navigate");
            Hint(ui, "Enter", "select");
            Hint(ui, "Esc", "close");
        }
    }

    private static void DrawEditFooter(Ui ui)
    {
        using (ui.Row(new Box
        {
            Width = Size.Fill,
            PadX = 14,
            PadY = 10,
            Gap = 16,
            Wrap = true,
            Bg = Color.SurfaceMuted,
        }))
        {
            Hint(ui, "Enter", "back");
            Hint(ui, "Esc", "close");
        }
    }

    private static void Hint(Ui ui, string key, string label)
    {
        using (ui.Row(new Box { Gap = 6, Align = Align.Center }))
        {
            ui.Kbd(key);
            ui.Muted(label);
        }
    }

    private static void DrawSeparator(Ui ui)
    {
        using (ui.Box(new Box { Width = Size.Fill, Height = Size.Px(1), Bg = Color.Border }))
        { }
    }

    private void Trigger(string id, string text)
    {
        LauncherItem? item = Array.Find(_bridge.Items, candidate => candidate.Id == id);
        if (item is null) return;

        Payload payload = _hadFiles
            ? new Payload("", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            : new Payload(text, Payload.NoFiles);

        try
        {
            string result = Launcher.Run(item.Binding, payload);
            _log.Add(item.Binding.Name, result, true);
        }
        catch (Exception error)
        {
            _log.Add(item.Binding.Name, error.Message, false);
        }

        _bridge.EnqueueTriggered(id);
    }

    private void Close(bool restoreFocus)
    {
        _armed = false;
        lock (_gate) _view = LauncherView.Palette;

        nint hwnd = _hwnd;
        if (hwnd == 0) return;
        Win32.ShowWindow(hwnd, ShowWindowCommand.Hide);
        if (restoreFocus && _previousForeground != 0)
            Win32.SetForegroundWindow(_previousForeground);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _hook?.Dispose();
        _hook = null;
        if (_started) _window?.RequestClose();
        if (_started) _thread.Join(1000);
        _ready.Dispose();
    }
}

// ---------------------------------------------------------------------------
// Win32: the small slice of the platform this app needs, with the magic
// numbers named once instead of at every call site.
// ---------------------------------------------------------------------------

internal enum ShowWindowCommand
{
    Hide = 0,
    Show = 5,
    ShowNoActivate = 8,
    Restore = 9,
}

[Flags]
internal enum SetWindowPosFlags : uint
{
    NoSize = 0x0001,
    NoMove = 0x0002,
    NoZOrder = 0x0004,
    NoActivate = 0x0010,
    FrameChanged = 0x0020,
}

[Flags]
internal enum WindowStyle : int
{
    Border = 0x00800000,
    Caption = 0x00C00000,
    ThickFrame = 0x00040000,
    SysMenu = 0x00080000,
}

internal enum WindowLong
{
    WndProc = -4,
    Style = -16,
    ExStyle = -20,
}

internal enum ClipboardFormat : uint
{
    Text = 1,
    UnicodeText = 13,
    HDrop = 15,
}

internal enum MonitorDefault : uint
{
    ToNull = 0,
    ToPrimary = 1,
    ToNearest = 2,
}

internal static class Win32
{
    // ---- messages ---------------------------------------------------------

    internal const uint WmClose = 0x0010;
    internal const uint WmSetFocus = 0x0007;
    internal const uint WmKillFocus = 0x0008;
    internal const uint WmHotkey = 0x0312;
    internal const uint WmApp = 0x8000;
    internal const uint WmHookHotkey = 0x8001;
    internal const uint TrayMessage = 0x8001; // a different window from WmHookHotkey

    internal const int WmLButtonUp = 0x0202;
    internal const int WmLButtonDblClk = 0x0203;
    internal const int WmRButtonUp = 0x0205;

    internal const nint WmKeyDown = 0x0100;
    internal const nint WmKeyUp = 0x0101;
    internal const nint WmSysKeyDown = 0x0104;
    internal const nint WmSysKeyUp = 0x0105;

    // ---- layered launcher window -----------------------------------------

    internal const int WsExLayered = 0x80000;
    internal const uint LwaColorKey = 0x1;

    // The dark theme's Background token (#1F1F1F) under the palette's 70% scrim lands on #0B0B0B.
    // Keying that colour out leaves only the palette panel on the desktop.
    internal const uint ColorKey = 0x0B0B0B;

    // ---- hook and input ---------------------------------------------------

    internal const int WhKeyboardLl = 13;
    internal const uint LlkhfInjected = 0x10;
    internal const uint GmemMoveable = 0x0042;
    internal const uint InputKeyboard = 1;
    internal const uint KeyEventKeyUp = 0x0002;

    // ---- tray icon --------------------------------------------------------

    internal const uint NifMessage = 0x1;
    internal const uint NifIcon = 0x2;
    internal const uint NifTip = 0x4;
    internal const uint NimAdd = 0;
    internal const uint NimDelete = 2;

    internal const uint MfString = 0;
    internal const uint MfSeparator = 0x800;
    internal const uint TpmRightButton = 0x2;
    internal const uint TpmReturnCmd = 0x100;

    // ---- structures -------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
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
    internal struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATAW
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

    // ---- delegates --------------------------------------------------------

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint KeyboardProc(int nCode, nint wParam, nint lParam);

    internal delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

    // ---- hotkeys and messages --------------------------------------------

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int RegisterHotKey(nint hWnd, int id, HotkeyModifiers modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern int SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    internal static extern int ShowWindow(nint hWnd, ShowWindowCommand command);

    [DllImport("user32.dll")]
    internal static extern int PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int PostThreadMessage(uint idThread, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    internal static extern int GetMessage(out MSG msg, nint hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    internal static extern int PeekMessage(out MSG msg, nint hWnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int code);

    // ---- clipboard --------------------------------------------------------

    [DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int OpenClipboard(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetClipboardData(ClipboardFormat format);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetClipboardData(ClipboardFormat format, nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint GlobalFree(nint memory);

    [DllImport("kernel32.dll")]
    internal static extern nint GlobalLock(nint handle);

    [DllImport("kernel32.dll")]
    internal static extern int GlobalUnlock(nint handle);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint DragQueryFileW(nint hDrop, uint index, StringBuilder? file, uint length);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    // ---- low-level keyboard hook -----------------------------------------

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWindowsHookEx(int idHook, KeyboardProc proc, nint module, uint threadId);

    [DllImport("user32.dll")]
    internal static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    internal static extern nint CallNextHookEx(nint hook, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    internal static extern short GetKeyState(int virtualKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint GetModuleHandle(string? moduleName);

    // ---- windows, monitors and subclassing --------------------------------

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(nint hWnd, out RECT rect);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromPoint(POINT point, MonitorDefault flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool GetMonitorInfoW(nint monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    internal static extern bool SetWindowPos(nint hWnd, nint insertAfter, int x, int y, int cx, int cy,
        SetWindowPosFlags flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    internal static extern int GetWindowLong(nint hWnd, WindowLong index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint hWnd, WindowLong index, nint value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(nint hWnd, WindowLong index, int value);

    internal static nint SetWindowLongPtr(nint hWnd, WindowLong index, nint value) => nint.Size == 8
        ? SetWindowLongPtr64(hWnd, index, value)
        : SetWindowLong32(hWnd, index, (int)value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint CallWindowProcW(nint previous, nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    internal static extern int GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    internal static extern int IsIconic(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetLayeredWindowAttributes(nint hWnd, uint colorKey, byte alpha, uint flags);

    // ---- tray icon and menus ---------------------------------------------

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint ExtractIconExW(string file, int index, out nint large, out nint small, uint count);

    [DllImport("user32.dll")]
    internal static extern int DestroyIcon(nint icon);

    [DllImport("user32.dll")]
    internal static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int AppendMenuW(nint menu, uint flags, int id, string? item);

    [DllImport("user32.dll")]
    internal static extern int DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    internal static extern int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hWnd,
        nint rect);

    /// <summary>The icon the executable carries, as the shell would show it.</summary>
    internal static nint LoadAppIcon()
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

/// <summary>
/// A window-procedure subclass. The callback is kept in a field for as long as the subclass is
/// installed, and the previous procedure is restored when this object is disposed.
/// </summary>
internal sealed class WindowProcedure : IDisposable
{
    private readonly nint _hwnd;
    private readonly Win32.WndProc _callback;
    private nint _previous;

    public WindowProcedure(nint hwnd, Win32.WndProc callback)
    {
        _hwnd = hwnd;
        _callback = callback;
        _previous = Win32.SetWindowLongPtr(
            hwnd, WindowLong.WndProc, Marshal.GetFunctionPointerForDelegate(_callback));
    }

    public void Dispose()
    {
        if (_previous == 0) return;
        Win32.SetWindowLongPtr(_hwnd, WindowLong.WndProc, _previous);
        _previous = 0;
    }

    public nint CallPrevious(nint hwnd, uint message, nint wParam, nint lParam) =>
        _previous == 0 ? 0 : Win32.CallWindowProcW(_previous, hwnd, message, wParam, lParam);
}
