// Page 11 of 28: the workspace tools: terminal, file browser, image viewer and
// cropper, emoji picker and a guided tour.
// Part of the CuiSharp.Gallery sample: Tools
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    private readonly Terminal _log = new(maxLines: 400, maxChars: 32 * 1024);
    private readonly LogBuffer _events = new(maxLines: 200, maxChars: 16 * 1024);
    private bool _eventsSeeded;
    private string _browsePath = Environment.CurrentDirectory;
    private string _pickedPath = "";
    private string _recentEmoji = "🙂 🚀 🎉";
    private int _emojiTone;
    private bool _pickerOpen;
    private ImageView _image = new() { Zoom = 0 };
    private RectF _crop = new(0, 0, 0.5f, 0.5f);

    private static readonly byte[] Checkerboard = CreateCheckerboard();

    private void PageTools(Ui ui)
    {
        Section(ui, "Terminal", "The scrollback lives in a Terminal the app owns; type a command and press Enter.");
        using (ui.Card(new Card()))
        {
            if (!_logSeeded)
            {
                SeedLog();
                _logSeeded = true;
            }

            var result = ui.Terminal("log", _log, new TerminalOpts { Height = 220 });
            if (result.Interrupt) _log.WriteLine("^C");
            if (result.Command is { Length: > 0 } command) _log.WriteLine($"$ {command} (not a real shell)");
        }

        Section(ui, "Log view", "The same ring buffer shape, with the filter, search, follow and copy toolbar.");
        using (ui.Card(new Card()))
        {
            if (!_eventsSeeded)
            {
                SeedEvents();
                _eventsSeeded = true;
            }

            // A plausible wall clock, so the timestamps read like a real log.
            _events.Now = ui.Time + (9 * 3600) + (12 * 60);
            ui.LogView("events", _events, new LogViewOpts { Height = 180, Timestamps = true });
        }

        Section(ui, "File browser", "The folder is a managed string: navigating it writes the new path back.");
        using (ui.Card(new Card()))
        {
            var browsed = ui.FileBrowser("files", ref _browsePath, 1024, new FileBrowserOpts
            {
                Height = 240,
                Filter = "*.cs;*.csproj;*.md",
            });
            if (browsed.Picked is { Length: > 0 } picked) _pickedPath = picked;
            else if (browsed.Selected is { Length: > 0 } selected) _pickedPath = selected;
            if (browsed.Error) ui.Muted("That folder could not be read.");
        }

        if (_pickedPath.Length > 0) ui.Muted($"Selected: {_pickedPath}");

        using (ui.Row(new Box { Gap = 12, Wrap = true }))
        {
            if (ui.Button("Open file picker")) ui.PopoverOpen("picker");
            ui.Muted(_pickedPath.Length > 0 ? _pickedPath : "Nothing picked yet");
        }

        using (var popover = ui.Popover("picker", new PopoverOpts { Width = 520 }))
        {
            if (popover.Open)
            {
                // The picker closes itself when the dialog is confirmed or cancelled.
                ui.FilePicker("picker", ref _pickerOpen, ref _pickedPath, o: new FilePickerOpts
                {
                    Title = "Pick something",
                    Filter = "*.cs;*.md",
                    FilterLabel = "Source",
                });
            }
        }

        Section(ui, "Image viewer and cropper", "Pan by dragging, zoom with the wheel. The cropper writes its rectangle back.");
        using (ui.Box(new Box { Dir = Dir.Grid, Columns = 2, MinColumnWidth = 240, Gap = 16 }))
        {
            using (ui.Card(new Card()))
            {
                ui.ImageViewer("viewer", 0, 0, 0, ref _image, new ImageViewerOpts
                {
                    Height = 220,
                    Checkerboard = true,
                });
            }

            using (ui.Card(new Card()))
            {
                ui.Cropper("crop", 0, 0, 0, ref _crop, new CropperOpts
                {
                    Height = 220,
                    Aspect = 1,
                    Round = true,
                });
            }
        }

        Section(ui, "Emoji", "Color emoji from the system font. The picker edits the app's recent list in place.");
        using (ui.Card(new Card()))
        using (ui.Row(new Box { Gap = 16, Align = Align.Center, Wrap = true }))
        {
            ui.Emoji("🙂", 32);
            ui.Emoji("🚀", 32);
            ui.Emoji("🎉", 40);

            string? picked = ui.EmojiPicker("emoji", ref _recentEmoji, ref _emojiTone, new EmojiPickerOpts
            {
                Width = Size.Px(320),
                Height = 220,
            });
            if (picked is { Length: > 0 }) ui.Heading(picked, 3);
        }

        Section(ui, "Guided tour", "A card points at a control and steps through the list.");
        using (ui.Row(new Box { Gap = 10, Align = Align.Center }))
        {
            if (ui.Button("Start the tour")) _tourStep = 0;
            ui.Button("Export");
            uint exportId = ui.LastId;
            if (_tourSteps is null)
            {
                _tourSteps =
                [
                    new(exportId, "This is the control", "The tour card points at whatever id you give it.", Placement.Bottom),
                    new(0, "And this one", "A target of 0 puts the card in the middle of the window.", Placement.Right),
                ];
            }

            if (_tourStep >= 0)
            {
                // The widget moves the step through the ref and sets it back to -1 when the tour
                // ends. Its result is the action the user took, not the step: assigning it here
                // would replace the step with CUI_TOUR_NONE and the tour would never advance.
                _ = ui.Tour("tour", _tourSteps, ref _tourStep, new TourOpts { Width = 320 });
            }
        }
    }

    private bool _logSeeded;
    private int _tourStep = -1;   // -1: not running; the tour sets it back to -1 when it ends

    private TourStep[]? _tourSteps;

    private void SeedEvents()
    {
        // Stamped before the lines are appended: each one takes the buffer's own clock.
        _events.Now = (9 * 3600) + (12 * 60) + 4;
        _events.AppendLine("build started");
        _events.AppendLine("3 warnings in 12 files", LogLevel.Warn);
        _events.AppendLine("build finished in 4.2 s", LogLevel.Info);
    }

    private void SeedLog()
    {
        _log.WriteLine("\x1b[1mCuiSharp terminal\x1b[0m");
        _log.WriteLine("Managed scrollback, ANSI colours, tabs and wraps.");
        _log.WriteLine("Type anything below and press Enter.");
    }

    private static byte[] CreateCheckerboard()
    {
        // 32x32 RGBA checkerboard, so the viewer has something to zoom.
        const int size = 32;
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 4;
                bool dark = ((x / 4) + (y / 4)) % 2 == 0;
                byte v = dark ? (byte)0x55 : (byte)0xAA;
                pixels[i] = v;
                pixels[i + 1] = (byte)(dark ? 0x33 : 0x99);
                pixels[i + 2] = (byte)(dark ? 0x77 : 0xCC);
                pixels[i + 3] = 255;
            }
        }

        return pixels;
    }
}
