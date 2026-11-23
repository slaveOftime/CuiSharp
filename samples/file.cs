#:package CuiSharp@*

// ---------------------------------------------------------------------------
// File Preview — a native CuiSharp app that previews one or more files, and
// edits text files that git tracks. Pass files as arguments:
//
//   dotnet run file.cs -- <path to a file> [more files...]
//
//   The window opens centered; the arrows beside the name switch between the
//   passed files when there are several.
//
//   .md / .markdown       ui.Markdown in a scroll area — headings, lists,
//                         tables, code and links. A local PNG/JPEG that is
//                         alone on its line is drawn inline, and a table whose
//                         every cell is a picture becomes a grid of them; click
//                         one (or the Image button) to open it in the viewer,
//                         and Back returns to the page.
//
//   .csv / .tsv           a sortable grid: click a header to sort by it, click
//                         it again to reverse, drag the edge to widen a column.
//                         Edit switches to the text, where Save works.
//
//   Esc closes the window (from the viewer, it goes back instead).
//   .png/.jpg/.jpeg/...   ui.ImageViewer — zoom, pan, a checkerboard behind
//                         transparency.
//   anything else text    ui.CodeEditor — syntax highlighting, editable;
//                         Save writes the file back. CUI has no C# or F#
//                         grammar, so a .cs file uses its C/C++ tokenizer and a
//                         .fs one its JavaScript tokenizer (see LangOf).
//   video, archives,
//   executables, missing
//   paths and folders     a metadata page: format, size, dates, and for an
//                         MP4/MOV its duration and resolution. Nothing is read
//                         into a string, so a 6 GB movie opens at once.
//   tracked file with
//   local changes         the git HEAD version against the working tree,
//                         shown by default; Code switches back to editing.
//
// Files open behind a spinner while the pool reads them; the CSV is parsed
// there too, and the grid and the editor draw only the rows and lines in view,
// so a huge file scrolls like a small one. Decoded images are freed when their
// view closes; a long document decodes only the pictures on screen.
//
// Also: `dotnet publish file.cs -c Release -o .` — one native executable that
// needs no .NET runtime on the machine it runs on. Replace file.ico to change
// the window icon.
// ---------------------------------------------------------------------------

#:property PublishAot=true
#:property OutputType=WinExe
#:property ApplicationIcon=file.ico
#:property OptimizationPreference=Size
#:property InvariantGlobalization=true
#:property StripSymbols=true
#:property DebugType=none

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Unicode;
using CuiSharp;
using CuiSharp.Hosting;

using var app = new FileApp(PickPaths(args));
return CuiApp.Run(app.Options, app.Draw);

/// Every argument, made absolute and de-duplicated. A path that is missing or
/// a folder is kept: the info page says so, which beats a silent drop.
static string[] PickPaths(string[] args)
{
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var paths = new List<string>(args.Length);

    foreach (var argument in args)
    {
        if (string.IsNullOrWhiteSpace(argument)) continue;

        string full;
        try
        {
            full = Path.GetFullPath(argument);
        }
        catch
        {
            continue;
        }

        if (seen.Add(full)) paths.Add(full);
    }

    return [.. paths];
}

/// What the file is. <see cref="ViewMode"/> is what is on screen; a text file
/// can also be shown as a diff.
internal enum FileKind
{
    Text,
    Markdown,
    Image,
    Csv,
    Video,
    Binary,     // anything whose first bytes are not text; metadata only
}

internal enum ViewMode
{
    Text,
    Markdown,
    Image,
    Diff,
    Csv,
    Info,       // no preview: size, dates, format, and whatever the container tells us
}

internal sealed class FileApp : IDisposable
{
    private const int CapMargin = 4096;
    private const string MarkdownScrollId = "md-scroll";

    private readonly string[] _paths;
    private readonly MarkdownPreview _markdown = new();
    private readonly ImageTexture _viewer = new();

    private int _index;
    private bool _started;
    private FileKind _kind;
    private ViewMode _mode;
    private ViewMode? _returnMode;      // where Back goes from the viewer
    private string _dir = Directory.GetCurrentDirectory();
    private string _lang = "text";
    private string? _text;              // the loaded text; the editor buffer in text mode
    private int _cap;                   // the editor's buffer capacity, in UTF-8 bytes
    private bool _dirty;
    private string? _notice;            // "Saved"/"Reloaded"; drawn once, then cleared
    private string? _error;
    private string? _footer;            // the status line; rebuilt only when it changes
    private FileFacts? _facts;          // size, dates and format, for the info page
    private GitInfo _gitInfo = GitInfo.None;
    private string? _head;              // the file's text at HEAD, read only when a diff is shown
    private CodeEditorResult _editor;
    private DiffResult _diff;
    private int _diffCursor;
    private ImageView _view;            // the viewer's zoom and pan, kept between frames
    private string? _imgPath;
    private CuiWindow? _window;

    // The CSV grid: the parsed table, the row order the header sort produced,
    // and the grid's own selection/scroll state.
    private CsvTable? _csv;
    private int[]? _csvOrder;
    private GridState? _csvGrid;
    private GridColumn[] _csvColumns = [];
    private TableSort _csvSort = new(-1, false);
    private string _csvId = "csv";
    private bool _tableFailed;         // a parse that threw is not retried every frame

    // A texture is referenced by the frame that drew it until that frame is
    // flushed, so it can only be destroyed before the next frame draws.
    // The preview queues its own handles; the viewer holds one, released here.
    private bool _releaseViewer;

    private Mutation<FileContent>? _load;
    private Mutation<GitInfo>? _gitLoad;
    private Mutation<string>? _headLoad;
    private Mutation<CsvTable>? _tableLoad;

    public FileApp(string[] paths)
    {
        _paths = paths;
        Options = new CuiAppOptions
        {
            Title = paths.Length > 0 ? Path.GetFileName(paths[0]) : "File Preview",
            Width = 720,
            Height = 960,
            Theme = Theme.Dark,
            Accent = Accent.Violet,
            LiveAttach = true,
            Renderer = Renderer.Software,
        };
        Options.WindowCreated += OnWindowCreated;
    }

    public CuiAppOptions Options { get; }

    /// The file on display, from the arguments.
    private string? _path => _paths.Length > 0 ? _paths[_index] : null;

    public void Dispose()
    {
        _load?.Dispose();
        _gitLoad?.Dispose();
        _headLoad?.Dispose();
        _tableLoad?.Dispose();
    }

    public void Draw(Ui ui)
    {
        if (!_started)
        {
            _started = true;
            LoadFor(0);
        }

        // Names outside the bundled faces draw nothing; install the OS faces
        // for CJK and friends once per context.
        ui.UseSystemFontFallbacks();

        if (ui.KeyPressed(Key.Escape))
        {
            if (_mode is ViewMode.Image && _returnMode is { } back) ReturnTo(back);
            else if (_window is { NativeWindow: not 0 } window) window.RequestClose();
        }

        // Last frame is flushed, so the pictures it drew can go now.
        _markdown.Collect(ui);
        if (_releaseViewer)
        {
            _releaseViewer = false;
            _viewer.Release(ui);
        }

        _editor = default;
        _diff = default;
        PollLoad();
        PollGit();
        PollHead();
        PollTable();

        using (ui.Column(new Box { Pad = 8, Gap = 8, Height = Size.Fill }))
        {
            DrawHeader(ui);
            DrawBody(ui);
            DrawFooter(ui);
        }
    }

    /// Keeps the window for Esc, and centers it in the work area once it exists.
    public void OnWindowCreated(CuiWindow window)
    {
        _window = window;

        // Centering is three Win32 calls: CUI hands out the handle but has no
        // position API, and the other backends have no window this could move.
        if (!OperatingSystem.IsWindows()) return;

        var hwnd = window.NativeWindow;
        if (hwnd == 0) return;
        if (Native.GetWindowRect(hwnd, out var rect) == 0) return;

        var workW = Native.GetSystemMetrics(Native.SmCxWorkarea);
        var workH = Native.GetSystemMetrics(Native.SmCyWorkarea);
        if (workW == 0 || workH == 0)
        {
            workW = Native.GetSystemMetrics(Native.SmCxScreen);
            workH = Native.GetSystemMetrics(Native.SmCyScreen);
        }

        var x = Math.Max(0, (workW - (rect.Right - rect.Left)) / 2);
        var y = Math.Max(0, (workH - (rect.Bottom - rect.Top)) / 2);
        Native.SetWindowPos(hwnd, 0 /* HWND_TOP */, x, y, 0, 0,
            Native.SwpNosize | Native.SwpNoactivate);
    }

    // ---- loading ----------------------------------------------------------

    /// Starts one background read for the file: its text, its git state, and
    /// HEAD when a text file actually needs a diff. Nothing here blocks a frame.
    private void LoadFor(int index)
    {
        if ((uint)index >= (uint)_paths.Length) return;

        _index = index;
        var path = _paths[index];

        _markdown.Reset();
        DropTable();
        _load?.Dispose();
        _load = null;
        _gitLoad?.Dispose();
        _gitLoad = null;
        _headLoad?.Dispose();
        _headLoad = null;

        _text = null;
        _cap = 0;
        _dirty = false;
        _notice = null;
        _error = null;
        _footer = null;
        _editor = default;
        _diff = default;
        _diffCursor = 0;
        _head = null;
        _gitInfo = GitInfo.None;
        _returnMode = null;
        _imgPath = null;
        _view = default;
        _kind = FileKind.Text;
        _mode = ViewMode.Text;
        _facts = null;

        var info = new FileInfo(path);
        _dir = info.DirectoryName ?? Directory.GetCurrentDirectory();
        Options.Title = NameOf(path);

        // A folder or a path that no longer exists still opens: the info page
        // says what it is, instead of leaving the window on a spinner.
        if (Directory.Exists(path))
        {
            _mode = ViewMode.Info;
            _facts = FileFacts.ForFolder(path);
            return;
        }

        if (!info.Exists)
        {
            _mode = ViewMode.Info;
            _facts = FileFacts.ForMissing(path);
            return;
        }

        _kind = Classify(path);
        _mode = _kind switch
        {
            FileKind.Markdown => ViewMode.Markdown,
            FileKind.Image => ViewMode.Image,
            FileKind.Csv => ViewMode.Csv,
            FileKind.Video => ViewMode.Info,
            _ => ViewMode.Text,
        };
        _lang = LangOf(path);
        _facts = FileFacts.ForFile(info);

        // A grid keeps its column widths per id, so a second file with other
        // columns does not inherit the first one's widths.
        _csvId = $"csv-{index}";

        // A picture the viewer holds belongs to the file being left. When the
        // new file is a picture too, Load frees it as it decodes the new one.
        if (_kind is FileKind.Image) _imgPath = path;
        else _releaseViewer = true;

        var kind = _kind;
        _load = new Mutation<FileContent>(token => FileContent.LoadAsync(path, kind, token));
        _load.Run();
    }

    private void PollLoad()
    {
        if (_load is not { } load) return;

        if (load.Latest is { } content)
        {
            Apply(content);
            _load = null;
            load.Dispose();
        }
        else if (load.Error is { } error)
        {
            _error = Describe(error);
            _load = null;
            load.Dispose();
        }
    }

    private void PollGit()
    {
        if (_gitLoad is not { } load) return;

        if (load.Latest is { } git)
        {
            _gitInfo = git;
            _footer = null;
            _gitLoad = null;
            load.Dispose();
        }
        else if (load.Error is not null)
        {
            _gitLoad = null;
            load.Dispose();
        }
    }

    private void PollHead()
    {
        if (_headLoad is not { } load) return;

        if (load.Latest is { } head)
        {
            _head = head;
            _footer = null;
            _headLoad = null;
            load.Dispose();
        }
        else if (load.Error is not null)
        {
            _head = string.Empty;
            _headLoad = null;
            load.Dispose();
        }
    }

    private void PollTable()
    {
        if (_tableLoad is not { } load) return;

        if (load.Latest is { } table)
        {
            ApplyTable(table);
            _tableLoad = null;
            load.Dispose();
        }
        else if (load.Error is { } error)
        {
            _error = Describe(error);
            _tableFailed = true;
            _tableLoad = null;
            load.Dispose();
        }
    }

    /// Parses the CSV on the pool, once per version of the text. A table that
    /// is already parsed is kept until the view that shows it is left.
    private void EnsureTable()
    {
        if (_csv is not null || _tableLoad is not null || _tableFailed
            || _kind is not FileKind.Csv || _text is not { } text) return;

        _tableLoad = new Mutation<CsvTable>(token => Task.FromResult(CsvTable.Parse(text, token)));
        _tableLoad.Run();
    }

    private void ApplyTable(CsvTable table)
    {
        _csv = table;
        _csvColumns = table.ToGridColumns();
        _csvSort = new TableSort(-1, false);   // no arrow until a header is clicked

        // The grid asks for one selection flag per row; a fresh table is a
        // fresh grid, and the old scroll offset and cursor must not survive.
        _csvGrid = new GridState(table.RowCount) { Sort = _csvSort };

        _csvOrder = new int[table.RowCount];
        for (var row = 0; row < _csvOrder.Length; row++) _csvOrder[row] = row;

        _error = null;
        _tableFailed = false;
        _footer = null;
    }

    private void DropTable()
    {
        _tableLoad?.Dispose();
        _tableLoad = null;
        _tableFailed = false;
        _csv = null;
        _csvOrder = null;
        _csvGrid = null;
        _csvColumns = [];
        _csvSort = new TableSort(-1, false);
    }

    private void Apply(FileContent content)
    {
        _gitInfo = content.Status;
        _head = content.Head;
        _facts = content.Facts;
        _error = null;
        _footer = null;

        // The extension was only a guess. The first bytes can turn a text file
        // into a binary one, and a file too large for the editor arrives with
        // no text at all; both belong on the info page.
        if (content.Kind != _kind) _kind = content.Kind;

        if (content.Text is not { } text)
        {
            if (_kind is not FileKind.Image) _mode = ViewMode.Info;
            return;
        }

        _text = text;
        _cap = ComputeCap(text);

        // A tracked file with local changes opens as a diff, but only while the
        // frame is still showing the mode LoadFor chose: a click on Code/Edit
        // while the read was in flight must not be undone.
        if (_kind is FileKind.Text && _mode is ViewMode.Text
            && _gitInfo is { Tracked: true, Changed: true } && _head is not null)
        {
            _mode = ViewMode.Diff;
        }
    }

    /// Reads HEAD only for a diff that is about to be drawn. The result is
    /// dropped when Code closes the diff, so a huge file is not kept twice.
    private void EnsureHead()
    {
        if (_head is not null || _headLoad is not null || _path is not { } path) return;

        _headLoad = new Mutation<string>(async token => await Git.ReadHeadAsync(path, token) ?? string.Empty);
        _headLoad.Run();
    }

    private void RefreshGit()
    {
        if (_path is not { } path || _kind is FileKind.Image or FileKind.Video or FileKind.Binary) return;

        _gitLoad?.Dispose();
        _gitLoad = new Mutation<GitInfo>(token => Git.ReadAsync(path, token));
        _gitLoad.Run();
    }

    // ---- header -----------------------------------------------------------

    private void DrawHeader(Ui ui)
    {
        using (ui.Row(new Box { Gap = 4, Align = Align.Center, Wrap = false, Height = Size.Fit }))
        {
            if (_paths.Length > 1)
            {
                if (ui.IconButton(Icons.ArrowLeft, new()
                {
                    Size = ControlSize.Sm,
                    Disabled = _paths.Length < 2 || _index <= 0,
                    Tooltip = "Previous file",
                }))
                {
                    LoadFor(_index - 1);
                }
            }
            // A movie's name is often a paragraph. Leave room for the arrows,
            // badges and buttons, and let the path ellipsize in what is left.
            var pathWidth = MathF.Max(120f, ui.ViewportSize().X - 360f);
            ui.Text(_path ?? "File Preview", new TextOpts
            {
                Size = 12,
                NoWrap = true,
                Width = Size.Px(pathWidth),
            });
            if (_paths.Length > 1)
            {
                if (ui.IconButton(Icons.ArrowRight, new()
                {
                    Size = ControlSize.Sm,
                    Disabled = _paths.Length < 2 || _index >= _paths.Length - 1,
                    Tooltip = "Next file",
                }))
                {
                    LoadFor(_index + 1);
                }
            }

            if (_gitInfo.Tracked) ui.Badge("tracked", new() { Tone = Tone.Success });
            if (_dirty) ui.Badge("unsaved changes", new() { Tone = Tone.Warning });
            if (_notice is { } notice)
            {
                ui.Badge(notice, new() { Tone = notice == "Saved" ? Tone.Success : Tone.Neutral });
                _notice = null;
            }
            if (_error is { } error) ui.Badge(Trim(error, 100), new() { Tone = Tone.Danger });

            using (ui.Row(new Box { Gap = 0, Align = Align.Center, Wrap = false, Height = Size.Fit }))
            {
                if (_mode is ViewMode.Image && _returnMode is { } back
                    && ui.Button("Back", new() { Size = ControlSize.Sm, Variant = ButtonVariant.Ghost }))
                    ReturnTo(back);

                if (_mode is ViewMode.Markdown or ViewMode.Csv
                    && ui.Button("Edit", new() { Size = ControlSize.Sm, Variant = ButtonVariant.Ghost }))
                    ShowText();

                if (_mode is ViewMode.Text && (_kind is FileKind.Markdown or FileKind.Csv)
                    && ui.Button("Preview", new() { Size = ControlSize.Sm, Variant = ButtonVariant.Ghost }))
                    ShowPreview();

                if (_mode is ViewMode.Text or ViewMode.Csv
                    && (_kind is FileKind.Text or FileKind.Csv) && _gitInfo.Tracked
                    && ui.Button("Diff", new() { Size = ControlSize.Sm, Variant = ButtonVariant.Ghost }))
                {
                    EnsureHead();
                    _mode = ViewMode.Diff;
                    _footer = null;
                }

                if (_mode is ViewMode.Diff
                    && ui.Button(_kind is FileKind.Csv ? "Grid" : "Code", new() { Size = ControlSize.Sm, Variant = ButtonVariant.Ghost }))
                {
                    _mode = _kind is FileKind.Csv ? ViewMode.Csv : ViewMode.Text;
                    _head = null;
                    _headLoad?.Dispose();
                    _headLoad = null;
                    _footer = null;
                    EnsureTable();
                }

                if (_mode is ViewMode.Markdown && _text is not null)
                {
                    _markdown.EnsureParsed(_text, _dir);
                    if (_markdown.FirstImagePath is { } first
                        && ui.IconButton(Icons.Image, new() { Size = ControlSize.Sm, Variant = ButtonVariant.Ghost }))
                    {
                        OpenImage(first);
                    }
                }

                if (_mode is ViewMode.Text && _path is not null
                    && ui.IconButton(Icons.Save, new() { Variant = ButtonVariant.Ghost, Size = ControlSize.Sm, }))
                {
                    Save();
                }

                if (ui.IconButton(Icons.RefreshCw, new() { Size = ControlSize.Sm, Variant = ButtonVariant.Ghost }))
                {
                    Reload();
                }
            }
        }
    }

    // ---- body -------------------------------------------------------------

    private void DrawBody(Ui ui)
    {
        switch (_mode)
        {
            case ViewMode.Text:
                DrawText(ui);
                break;
            case ViewMode.Markdown:
                DrawMarkdown(ui);
                break;
            case ViewMode.Image:
                DrawImage(ui);
                break;
            case ViewMode.Diff:
                DrawDiff(ui);
                break;
            case ViewMode.Csv:
                DrawCsv(ui);
                break;
            case ViewMode.Info:
                DrawInfo(ui);
                break;
        }
    }

    /// <summary>
    /// What a file looks like when it cannot be shown: one card with its name,
    /// its format, and every fact the app can get without decoding it. A video
    /// contributes its duration and resolution from the container header; a
    /// missing path or a folder contributes only what the file system knows.
    /// </summary>
    private void DrawInfo(Ui ui, string? note = null)
    {
        if (_facts is not { } facts)
        {
            DrawEmpty(ui);
            return;
        }

        using (ui.Column(new Box
        {
            Height = Size.Fill,
            Align = Align.Center,
            Justify = Justify.Center,
            Gap = 12,
        }))
        {
            using (ui.Card(new Card
            {
                Width = Size.Fill,
                MinWidth = 300,
                MaxWidth = 520,
                Pad = 24,
                Gap = 10,
                Align = Align.Center,
            }))
            {
                ui.Icon(IconFor(facts), 44, Color.TextMuted);
                ui.Text(NameOf(_path ?? "File"), new TextOpts
                {
                    Size = 16,
                    Width = Size.Fill,
                    Align = TextAlign.Center,
                    NoWrap = true,
                });
                ui.Secondary(facts.Format);

                var hasDetails = facts.Length is not null || facts.Duration is not null
                    || facts.Width > 0 || facts.Created is not null || facts.Modified is not null;
                if (hasDetails) ui.Separator();

                if (facts.Length is { } length) InfoRow(ui, "Size", HumanSize(length));
                if (facts.Width > 0 && facts.Height > 0)
                    InfoRow(ui, "Resolution", $"{facts.Width} x {facts.Height}");
                if (facts.Duration is { } duration)
                {
                    InfoRow(ui, "Duration", FormatDuration(duration));
                    if (facts.Length is { } bytes && duration.TotalSeconds > 0)
                        InfoRow(ui, "Bit rate", $"{bytes * 8 / duration.TotalSeconds / 1_000_000:0.#} Mbps");
                }
                if (facts.Created is { } created) InfoRow(ui, "Created", created.ToString("g"));
                if (facts.Modified is { } modified) InfoRow(ui, "Modified", modified.ToString("g"));
                if (_path is { } path) InfoRow(ui, "Location", FolderOf(path));

                if ((note ?? facts.Note) is { } reason)
                {
                    ui.Separator();
                    ui.Secondary(reason);
                }
                if (_error is { } error) ui.Secondary(error);
            }

            // The sample has no player, but the file is one click from the app
            // the user already has for it.
            if (_path is { } open && facts.Length is not null
                && ui.Button("Open with default app", new() { Size = ControlSize.Sm }))
            {
                OpenExternally(open);
            }
        }
    }

    private static void InfoRow(Ui ui, string label, string value)
    {
        using (ui.Row(new Box { Gap = 12, Align = Align.Center, Height = Size.Fit }))
        {
            ui.Text(label, new TextOpts
            {
                Size = 12,
                Width = Size.Px(88),
                NoWrap = true,
                Color = Color.TextMuted,
            });
            ui.Text(value, new TextOpts { Size = 12 });
        }
    }

    private static string IconFor(FileFacts facts) => facts.Format switch
    {
        "Folder" => Icons.Folder,
        "MP4 video" or "QuickTime video" or "Matroska video" or "WebM video"
            or "AVI video" or "Windows Media video" or "MPEG video"
            or "MPEG transport stream" or "Flash video" or "Ogg video" => Icons.Video,
        "ZIP archive" or "7-Zip archive" or "RAR archive" or "Gzip archive"
            or "Tar archive" or "Disc image" => Icons.Archive,
        "Windows executable" or "Windows library" or "Windows installer" => Icons.Package,
        _ when facts.Format.EndsWith("image", StringComparison.OrdinalIgnoreCase) => Icons.Image,
        _ => Icons.File,
    };

    private void OpenExternally(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            _notice = "Opened";
        }
        catch (Exception error)
        {
            _error = Describe(error);
        }
    }

    private void DrawCsv(Ui ui)
    {
        if (_csv is not { } table || _csvGrid is not { } grid || _csvOrder is not { } order)
        {
            EnsureTable();

            using (ui.Column(new Box { Gap = 8, Align = Align.Center }))
            {
                ui.Spinner(32);
                ui.Secondary(_error ?? (_text is null ? "Loading..." : "Reading CSV..."));
            }
            return;
        }

        if (table.Columns == 0)
        {
            ui.Secondary("This CSV has no columns");
            return;
        }

        using (var scope = ui.DataGrid(_csvId, _csvColumns, table.RowCount, grid, new GridOpts
        {
            RowHeight = 28,
            Striped = true,
            HideCheckboxes = true,
        }))
        {
            if (!scope.Open) return;

            // DataGridBegin has already applied this frame's header click, so
            // the rows are re-ordered in the same frame the arrow turns.
            var sort = grid.Sort;
            if (sort.Column != _csvSort.Column || sort.Descending != _csvSort.Descending)
            {
                table.Sort(order, sort);
                _csvSort = sort;
            }

            // Only the rows the grid says are on screen are built; the order
            // array is what the sort moved, so a row shows its source record.
            for (var row = grid.First; row < grid.Last; row++)
            {
                ui.DataGridRow(row);

                var source = order[row];
                for (var column = 0; column < table.Columns; column++)
                    ui.DataGridCellText(table.Cell(source, column));
            }
        }
    }

    private void DrawText(Ui ui)
    {
        if (_text is null)
        {
            DrawEmpty(ui);
            return;
        }

        // The editor keeps the text in its own buffer and draws only the lines
        // in view, so a large file scrolls like a small one.
        _editor = ui.CodeEditor("text", ref _text, _cap, new CodeEditorOpts { Height = -1, Lang = _lang });

        if (_editor.Changed)
        {
            _dirty = true;
            _footer = null;
        }
    }

    private void DrawMarkdown(Ui ui)
    {
        if (_text is null)
        {
            DrawEmpty(ui);
            return;
        }

        _markdown.EnsureParsed(_text, _dir);

        // The rect is last frame's, which is what a size decision needs: the
        // scroll is laid out at the end of the frame that opened it.
        var viewport = ui.RectOf(MarkdownScrollId);
        _markdown.SetViewport(viewport);

        string? open;
        using (ui.Scroll(new ScrollOpts
        {
            Id = MarkdownScrollId,
            Height = Size.Fill,
            Width = Size.Fill,
            FadeEdges = true,
        }))
        {
            open = _markdown.Draw(ui);
        }

        if (_markdown.LoadVisible(ui, viewport)) ui.RequestRedraw();
        if (open is not null) OpenLink(open);
    }

    private void DrawImage(Ui ui)
    {
        if (_imgPath is null)
        {
            DrawEmpty(ui);
            return;
        }

        var decoded = _viewer.Loaded;
        _viewer.Load(ui, _imgPath);
        if (_viewer.Loaded && !decoded) _footer = null;   // the size is part of it now

        if (_viewer.Loaded)
        {
            ui.ImageViewer("image", _viewer.Handle, _viewer.Width, _viewer.Height, ref _view,
                new ImageViewerOpts { Height = Size.Fill, Checkerboard = true });
        }
        else if (_viewer.Failed)
        {
            // An image with no decoder (SVG, HEIC, ICO…) gets the same page a
            // video does: what it is, how big, where it lives.
            DrawInfo(ui, "This image format has no decoder (PNG and JPEG are supported).");
        }
    }

    private void DrawDiff(Ui ui)
    {
        if (_text is null)
        {
            DrawEmpty(ui);
            return;
        }

        if (_head is null)
        {
            // Reloading a diff, or opening one whose HEAD read failed, still
            // needs the old text: the same path that Code released is loaded again.
            EnsureHead();

            using (ui.Column(new Box { Gap = 8, Align = Align.Center }))
            {
                ui.Spinner(24);
                ui.Secondary("Loading diff...");
            }
            return;
        }

        // A plain-text language makes CUI tokenize the whole line as one token, and the
        // painter skips tokens that start with a space — so every indented line would
        // render empty. No language means no highlighting, which is what "text" is.
        _diff = ui.DiffView("diff", _head, _text, ref _diffCursor,
            new DiffOpts
            {
                Height = -1,
                Language = _lang == "text" ? null : _lang,
                OldTitle = "HEAD",
                NewTitle = "working tree",
            });
    }

    private void DrawEmpty(Ui ui)
    {
        using (ui.Column(new Box
        {
            Gap = 8,
            Align = Align.Center,
            Height = Size.Fill,
            Justify = Justify.Center,
        }))
        {
            if (_error is null && _load is { IsRunning: true })
            {
                ui.Spinner(32);
                ui.Secondary("Loading...");
                return;
            }

            ui.Icon(_path is null ? Icons.File : Icons.CircleAlert, 32, Color.TextMuted);
            ui.Secondary(_path is null
                ? "Pass a file: dotnet run file.cs -- <path>"
                : _error ?? "Nothing to show");
        }
    }

    // ---- footer -----------------------------------------------------------

    private void DrawFooter(Ui ui)
    {
        _footer ??= BuildFooter();

        using (ui.Row(new Box { Gap = 12, Align = Align.Center, Height = Size.Fit }))
        {
            ui.Secondary(_footer);
            if (_mode is ViewMode.Text && _editor.Line > 0)
                ui.Secondary($"Ln {_editor.Line}, Col {_editor.Column}");
            else if (_mode is ViewMode.Diff && _head is not null)
                ui.Secondary($"+{_diff.Added} -{_diff.Removed}");
        }
    }

    private string BuildFooter()
    {
        // An image with no decoder shows the info page too, so its footer
        // should read like one.
        if (_mode is ViewMode.Info || (_mode is ViewMode.Image && _viewer.Failed))
        {
            if (_facts is not { } facts) return "No file";
            if (facts.Length is null && facts.Modified is null) return $"Not found - {facts.Format}";

            var parts = new List<string>(3);
            if (facts.Length is { } length) parts.Add(HumanSize(length));
            if (facts.Modified is { } modifiedAt) parts.Add(modifiedAt.ToString());
            parts.Add(facts.Format);
            return string.Join(" - ", parts);
        }

        var shown = _mode is ViewMode.Image ? _imgPath ?? _path : _path;
        if (shown is null) return "No file";

        var info = new FileInfo(shown);
        if (!info.Exists) return $"{shown} - not found";

        var modified = info.LastWriteTimeUtc.ToLocalTime().ToString();

        return _mode switch
        {
            ViewMode.Text => $"{info.Length} B - {modified} - {LineCount()} lines",
            ViewMode.Markdown => $"{info.Length} B - {modified} - markdown - {LineCount()} lines{ImageSuffix()}",
            ViewMode.Csv => $"{info.Length} B - {modified} - csv - {CsvSummary()}",
            // A viewer that has not decoded its picture yet has no size to report.
            ViewMode.Image when _viewer.Loaded =>
                $"{info.Length} B - {modified} - {_viewer.Width}x{_viewer.Height} px",
            _ => $"{info.Length} B - {modified}",
        };
    }

    private string CsvSummary() => _csv is { } table ? table.Describe() : "reading...";

    private string ImageSuffix()
    {
        var count = _markdown.ImageCount;
        return count switch
        {
            0 => string.Empty,
            1 => " - 1 image",
            _ => $" - {count} images",
        };
    }

    private int LineCount() => _editor.LineCount > 0 ? _editor.LineCount : CountLines(_text ?? "");

    // ---- actions ----------------------------------------------------------

    private void Save()
    {
        if (_path is not { } path || _text is not { } text) return;

        try
        {
            File.WriteAllText(path, text);
            _dirty = false;
            _error = null;
            _notice = "Saved";
            _footer = null;
            RefreshGit();
        }
        catch (Exception error)
        {
            _error = Describe(error);
            _notice = null;
        }
    }

    private void Reload()
    {
        if (_path is null) return;

        // From the viewer, Reload re-decodes the picture. Reloading the file the
        // viewer was opened from would drop the picture and go back to the page.
        if (_mode is ViewMode.Image && _imgPath is not null)
        {
            _releaseViewer = true;
            _view = default;
            _notice = "Reloaded";
            _footer = null;
            return;
        }

        var mode = _mode;
        LoadFor(_index);

        if (mode is ViewMode.Text && _kind is not FileKind.Image) _mode = ViewMode.Text;
        else if (mode is ViewMode.Diff && (_kind is FileKind.Text or FileKind.Csv)) _mode = ViewMode.Diff;
        else if (mode is ViewMode.Info) _mode = ViewMode.Info;

        _notice = "Reloaded";
    }

    private void ShowText()
    {
        _mode = ViewMode.Text;
        _returnMode = null;
        _markdown.Reset();
        DropTable();
        _footer = null;
    }

    private void ShowPreview()
    {
        _mode = _kind is FileKind.Csv ? ViewMode.Csv : ViewMode.Markdown;
        _returnMode = null;
        EnsureTable();
        _footer = null;
    }

    private void ReturnTo(ViewMode mode)
    {
        _mode = mode;
        _returnMode = null;
        _releaseViewer = true;
        _footer = null;
    }

    /// A click on a link or on an inline image: a local PNG/JPEG opens in the
    /// viewer, anything else (a web URL, a source file) is left alone.
    private void OpenLink(string link)
    {
        if (_mode is ViewMode.Image) return;

        if (ImageFiles.TryResolve(link, _dir, out var full) && ImageFiles.IsPreviewable(full))
            OpenImage(full);
    }

    private void OpenImage(string path)
    {
        if (!string.Equals(_imgPath, path, StringComparison.OrdinalIgnoreCase))
            _view = default;

        _returnMode = _mode is ViewMode.Image ? _returnMode ?? ViewMode.Markdown : _mode;
        _mode = ViewMode.Image;
        _imgPath = path;

        // The page is not on screen while the picture is: drop its chunks and
        // its decoded images, and parse/draw it again on Back. The viewer frees
        // the handle it held, if any, when Load decodes this one.
        _markdown.Reset();
        _notice = null;
        _footer = null;
    }

    // ---- helpers ----------------------------------------------------------

    private static FileKind Classify(string path) =>
        ImageFiles.IsImage(path) ? FileKind.Image
        : ImageFiles.IsMarkdown(path) ? FileKind.Markdown
        : IsCsv(path) ? FileKind.Csv
        : MediaFiles.IsVideo(path) ? FileKind.Video
        : FileKind.Text;

    private static bool IsCsv(string path)
    {
        var ext = Path.GetExtension(path.AsSpan());
        return ext.Equals(".csv", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".tsv", StringComparison.OrdinalIgnoreCase);
    }

    /// The language CUI's tokenizer knows for this file, or "text" for none.
    /// <para>
    /// CUI has no C# grammar. Its C list is the C/C++ one — <c>class</c>,
    /// <c>public</c>, <c>namespace</c>, <c>using</c>, <c>virtual</c>,
    /// <c>override</c>, <c>new</c>, <c>this</c> are all in it — and C# shares
    /// the comment, string, character and number rules, so a .cs file colors
    /// correctly everywhere except the handful of keywords C does not have
    /// (<c>var</c>, <c>async</c>, <c>await</c>, <c>record</c>, <c>foreach</c>…).
    /// </para>
    /// <para>
    /// F# has no grammar either, and the JS one is its nearest neighbour: it
    /// has <c>let</c>, <c>function</c>, <c>type</c>, <c>interface</c>,
    /// <c>async</c>, <c>yield</c>, <c>in</c>, <c>try</c>, <c>this</c>,
    /// <c>class</c> and <c>new</c>, and the same <c>//</c> comment, quoted
    /// string and number rules. Its upper-case rule also lands on the right
    /// things, since F# spells modules, types and union cases in PascalCase.
    /// What it misses is F#'s own vocabulary (<c>match</c>, <c>with</c>,
    /// <c>module</c>, <c>open</c>, <c>member</c>, <c>rec</c>, <c>mutable</c>…),
    /// <c>(* *)</c> block comments, and it colors a backtick as a string.
    /// </para>
    private static string LangOf(string path)
    {
        var ext = Path.GetExtension(path.AsSpan());

        if (ext.Equals(".c", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".h", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cs", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".csx", StringComparison.OrdinalIgnoreCase)) return "c";
        if (ext.Equals(".cpp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".hpp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cc", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cxx", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".hh", StringComparison.OrdinalIgnoreCase)) return "cpp";
        if (ext.Equals(".js", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mjs", StringComparison.OrdinalIgnoreCase)) return "js";
        if (ext.Equals(".ts", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".tsx", StringComparison.OrdinalIgnoreCase)) return "ts";
        if (ext.Equals(".fs", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".fsi", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".fsx", StringComparison.OrdinalIgnoreCase)) return "js";
        if (ext.Equals(".py", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".pyw", StringComparison.OrdinalIgnoreCase)) return "python";
        if (ext.Equals(".rs", StringComparison.OrdinalIgnoreCase)) return "rust";
        if (ext.Equals(".go", StringComparison.OrdinalIgnoreCase)) return "go";
        if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jsonc", StringComparison.OrdinalIgnoreCase)) return "json";
        if (ext.Equals(".sh", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bash", StringComparison.OrdinalIgnoreCase)) return "shell";
        if (ext.Equals(".sql", StringComparison.OrdinalIgnoreCase)) return "sql";

        return "text";
    }

    /// The editor buffer holds UTF-8 bytes, so the hint has to count bytes, not
    /// chars: a CJK page is three times its char count and used to grow the
    /// buffer on its first frame.
    private static int ComputeCap(string text) => Encoding.UTF8.GetByteCount(text) + CapMargin;

    private static int CountLines(string text) =>
        text.Length == 0 ? 0 : text.AsSpan().Count('\n') + (text[^1] == '\n' ? 0 : 1);

    /// The name a path shows, for a file or a folder given with a trailing slash.
    private static string NameOf(string path) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(path));

    private static string FolderOf(string path) =>
        Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) ?? path;

    /// A byte count a person can read, in the largest unit that keeps it above 1.
    private static string HumanSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024):0.#} MB",
        _ => $"{bytes / (1024d * 1024 * 1024):0.#} GB",
    };

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";

    private static string Trim(string text, int max)
    {
        if (text.Length <= max) return text;
        return text[..(max - 3)] + "...";
    }

    private static string Describe(Exception error)
    {
        var cause = error.GetBaseException();
        return string.IsNullOrWhiteSpace(cause.Message) ? cause.GetType().Name : cause.Message;
    }
}

/// <summary>
/// The markdown preview: CUI draws <c>![alt](url)</c> as a link to the image,
/// so a README's screenshots show their alt text and nothing else. An image
/// that is alone on its line is lifted out here and drawn as a real image, and
/// a table whose every cell is a picture becomes a grid of them; images inside
/// a sentence or a list stay links, which is what the renderer does with them
/// anyway.
/// <para>
/// Textures are decoded only when the block is on screen, one per frame, and
/// are released when the preview is left. A long page therefore costs the
/// pictures the reader actually looked at, not every file it mentions.
/// </para>
/// </summary>
internal sealed class MarkdownPreview
{
    private const float MaxImageHeight = 520f;
    private const float PlaceholderHeight = 160f;
    private const float CardPad = 8f;
    private const float CardGap = 8f;

    private readonly MarkdownOpts _options = new() { Width = Size.Fill };
    private readonly List<Block> _blocks = [];
    private readonly Dictionary<string, ImageTexture> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ImageTexture> _stale = [];   // released on the next frame

    private string? _source;
    private string? _firstImagePath;
    private int _imageCount;
    private float _contentWidth = 720f;

    public string? FirstImagePath => _firstImagePath;

    public int ImageCount => _imageCount;

    /// Parses only when the text is a different instance than the one already
    /// parsed: a frame that draws the same page costs a reference compare.
    public void EnsureParsed(string text, string dir)
    {
        if (!ReferenceEquals(_source, text)) SetText(text, dir);
    }

    public void SetText(string text, string dir)
    {
        Reset();
        _source = text;

        var textStart = 0;
        var position = 0;
        var fenced = false;
        var fence = '\0';
        var lifted = false;

        while (position < text.Length)
        {
            var lineEnd = text.IndexOf('\n', position);
            if (lineEnd < 0) lineEnd = text.Length;

            var line = text.AsSpan(position, lineEnd - position).TrimEnd('\r');
            var trimmed = line.TrimStart();
            var next = lineEnd + 1;

            if (fenced)
            {
                if (StartsFence(trimmed, fence)) fenced = false;
            }
            else if (StartsFence(trimmed, '`'))
            {
                fenced = true;
                fence = '`';
            }
            else if (StartsFence(trimmed, '~'))
            {
                fenced = true;
                fence = '~';
            }
            else if (!IsIndented(line) && IsTableRow(trimmed)
                && ReadImageTable(text, dir, position, out var tableEnd) is { } table)
            {
                AddText(text[textStart..position]);
                _blocks.Add(table);
                textStart = tableEnd;
                next = tableEnd;
                lifted = true;
            }
            else if (!IsIndented(line)
                && TryParseImageLine(line, out var alt, out var url)
                && ImageFiles.TryResolve(url, dir, out var full)
                && ImageFiles.IsPreviewable(full))
            {
                AddText(text[textStart..position]);
                AddImage(alt, full);
                textStart = lineEnd + 1;
                lifted = true;
            }

            position = next;
        }

        // With nothing lifted out the renderer takes the text as it is; a copy
        // would be the whole document again, which for a big README is the
        // largest allocation the preview makes.
        if (!lifted) AddText(text);
        else if (textStart < text.Length) AddText(text[textStart..]);
    }

    /// Drops the parsed blocks and unlinks the decoded pictures, queueing their
    /// handles for <see cref="Collect"/>. Safe to call while the frame that
    /// drew them is still being built.
    public void Reset()
    {
        foreach (var texture in _images.Values) _stale.Add(texture);

        _images.Clear();
        _blocks.Clear();
        _source = null;
        _firstImagePath = null;
        _imageCount = 0;
    }

    /// Destroys the handles a <see cref="Reset"/> queued. Call it once per
    /// frame before anything draws: the frame that recorded those pictures has
    /// been flushed by then, so no draw list refers to them any more.
    public void Collect(Ui ui)
    {
        if (_stale.Count == 0) return;

        foreach (var texture in _stale) texture.Release(ui);
        _stale.Clear();
    }

    public string? Draw(Ui ui)
    {
        string? open = null;
        var previousImage = false;

        foreach (var block in _blocks)
        {
            if (block is TextBlock text)
            {
                var result = ui.Markdown(text.Id, text.Text, _options);
                if (result.Link is { } link) open ??= link;
            }
            else if (block is ImageBlock image)
            {
                // A blank line between two pictures becomes an empty text
                // chunk, so the cards would otherwise touch.
                if (previousImage)
                {
                    using (ui.Box(new Box { Width = Size.Fill, Height = Size.Px(8) })) { }
                }

                if (DrawCard(ui, image.Id, image.Path, ref image.Texture, image.Alt, _contentWidth))
                    open ??= image.Path;
            }
            else if (block is TableBlock table)
            {
                open ??= DrawTable(ui, table);
            }

            previousImage = block is ImageBlock;
        }

        return open;
    }

    public void SetViewport(RectF viewport)
    {
        if (viewport.W > 0) _contentWidth = MathF.Max(120f, viewport.W - 24f);
    }

    /// Decodes the images that are on screen, one per frame. Returns true when
    /// another frame is needed (a picture was just decoded, or one is still
    /// waiting), because a sleeping window is not woken by a texture.
    public bool LoadVisible(Ui ui, RectF viewport)
    {
        var pending = false;
        var decoded = 0;

        foreach (var block in _blocks)
        {
            if (block is ImageBlock image)
            {
                if (Decode(ui, viewport, image.Id, image.Path, ref image.Texture, ref decoded)) pending = true;
            }
            else if (block is TableBlock table)
            {
                foreach (var row in table.Rows)
                    foreach (var cell in row)
                        if (Decode(ui, viewport, cell.Id, cell.Path, ref cell.Texture, ref decoded)) pending = true;
            }
        }

        return pending;
    }

    /// A table of pictures is a gallery, and a gallery is a grid of cards. The
    /// grid keeps the table's columns and wraps when the window is narrow.
    private string? DrawTable(Ui ui, TableBlock table)
    {
        string? open = null;

        var columns = Math.Max(1, table.Columns);
        var cellWidth = MathF.Max(96f, (_contentWidth - CardGap * (columns - 1)) / columns);

        using (ui.Box(new Box
        {
            Dir = Dir.Grid,
            Columns = columns,
            MinColumnWidth = 160,
            Gap = CardGap,
            Width = Size.Fill,
        }))
        {
            foreach (var row in table.Rows)
                foreach (var cell in row)
                    if (DrawCard(ui, cell.Id, cell.Path, ref cell.Texture, cell.Label, cellWidth - CardPad * 2))
                        open ??= cell.Path;
        }

        return open;
    }

    /// One picture card: the image fitted to <paramref name="maxWidth"/>, its
    /// caption, and a click that opens it. Returns true when it was clicked.
    private bool DrawCard(Ui ui, string id, string path, ref ImageTexture? slot, string caption, float maxWidth)
    {
        var texture = EnsureTexture(path, ref slot);
        var size = texture.Loaded ? Fit(texture.Width, texture.Height, maxWidth) : default;

        bool clicked;
        using (ui.Card(new Card
        {
            Id = id,
            Width = Size.Fill,
            Hoverable = true,
            Muted = true,
            Pad = CardPad,
            Gap = 6,
            Align = Align.Center,
        }, out clicked))
        {
            if (texture.Loaded && size.Width > 0)
            {
                ui.Image(texture.Handle, new ImageOpts
                {
                    Width = Size.Px(size.Width),
                    Height = Size.Px(size.Height),
                    Radius = 8,
                });
            }
            else
            {
                DrawPlaceholder(ui, path, texture);
            }

            if (caption.Length > 0) ui.Secondary(caption);
        }

        return clicked && texture.Loaded;
    }

    /// Decodes one picture if it is the next visible one that still needs it.
    /// Returns true while anything on screen is waiting, so the caller can ask
    /// for another frame.
    private bool Decode(Ui ui, RectF viewport, string id, string path, ref ImageTexture? slot, ref int decoded)
    {
        if (slot is not { } texture || texture.Loaded || texture.Failed) return false;
        if (!IsVisible(ui.RectOf(id), viewport)) return false;

        if (decoded < 1)
        {
            texture.Load(ui, path);
            decoded++;
        }

        return true;
    }

    private ImageTexture EnsureTexture(string path, ref ImageTexture? slot)
    {
        if (slot is { } existing) return existing;

        if (!_images.TryGetValue(path, out var texture))
        {
            texture = new ImageTexture();
            _images[path] = texture;
        }

        slot = texture;
        return texture;
    }

    private (float Width, float Height) Fit(int width, int height, float maxWidth)
    {
        if (width <= 0 || height <= 0) return (0, 0);

        var scale = MathF.Min(1f, MathF.Min(maxWidth / width, MaxImageHeight / height));
        return (MathF.Max(1f, width * scale), MathF.Max(1f, height * scale));
    }

    private static void DrawPlaceholder(Ui ui, string path, ImageTexture texture)
    {
        using (ui.Box(new Box
        {
            Width = Size.Fill,
            Height = Size.Px(PlaceholderHeight),
            Align = Align.Center,
            Justify = Justify.Center,
        }))
        {
            if (texture.Failed) ui.Secondary($"Couldn't load {Path.GetFileName(path)}");
            else ui.Spinner(24);
        }
    }

    private static bool IsVisible(RectF rect, RectF viewport)
    {
        // A block whose rectangle the layout has not published yet is decoded
        // rather than hidden forever; the per-frame limit still bounds the work.
        if (rect.W <= 0 || rect.H <= 0) return true;
        if (viewport.W <= 0 || viewport.H <= 0) return true;

        const float margin = 200f;
        return rect.Y < viewport.Y + viewport.H + margin
            && rect.Y + rect.H > viewport.Y - margin
            && rect.X < viewport.X + viewport.W
            && rect.X + rect.W > viewport.X;
    }

    private void AddText(string text)
    {
        if (text.AsSpan().Trim().IsEmpty) return;
        _blocks.Add(new TextBlock($"md-text-{_blocks.Count}", text));
    }

    private void AddImage(string alt, string path)
    {
        _blocks.Add(new ImageBlock($"md-image-{_blocks.Count}", alt, path));
        _imageCount++;
        _firstImagePath ??= path;
    }

    /// A line that is one picture: <c>![alt](path)</c>, or the
    /// <c>[![alt](path)](target)</c> a README uses to link a thumbnail to the
    /// full picture. A destination may be wrapped in <c>&lt;...&gt;</c> and may
    /// carry a title after it.
    private static bool TryParseImageLine(ReadOnlySpan<char> line, out string alt, out string url)
    {
        alt = string.Empty;
        url = string.Empty;

        line = line.Trim();
        if (line.Length < 5) return false;

        if (line.StartsWith("[![") && line[^1] == ')')
        {
            var outer = LastIndexOfPair(line, ']', '(');
            if (outer > 2) line = line[1..outer];
        }

        return TryParseImage(line, out alt, out url, out var used) && used == line.Length;
    }

    /// One picture anywhere in <paramref name="span"/>, starting at <c>![</c>.
    /// <paramref name="used"/> is how much of the span it consumed, so a caller
    /// can see whether anything follows it.
    private static bool TryParseImage(ReadOnlySpan<char> span, out string alt, out string url, out int used)
    {
        alt = string.Empty;
        url = string.Empty;
        used = 0;

        if (!span.StartsWith("![")) return false;

        var close = span.IndexOf(']');
        if (close < 0 || close + 1 >= span.Length || span[close + 1] != '(') return false;

        var rest = span[(close + 1)..];
        var end = FindClosingParen(rest);
        if (end < 0) return false;

        var destination = rest[1..end].Trim();
        if (destination.Length == 0) return false;

        if (destination[0] == '<')
        {
            var closeAngle = destination.IndexOf('>');
            if (closeAngle < 0) return false;
            destination = destination[1..closeAngle];
        }
        else
        {
            var title = destination.IndexOfAny(' ', '\t');
            if (title > 0) destination = destination[..title];
        }

        if (destination.Length == 0) return false;

        alt = span[2..close].ToString();
        url = destination.ToString();
        used = close + 1 + end + 1;
        return true;
    }

    /// A table whose every cell is a picture is the gallery a README writes for
    /// its screenshots. Returns the block it makes, or null when the rows are
    /// not that: a table of text stays with the markdown renderer.
    private TableBlock? ReadImageTable(string text, string dir, int position, out int end)
    {
        end = position;

        var id = $"md-table-{_blocks.Count}";
        var rows = new List<List<TableCell>>();
        var lineStart = position;

        while (lineStart < text.Length)
        {
            var lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = text.Length;

            var row = text.AsSpan(lineStart, lineEnd - lineStart).TrimEnd('\r').Trim();
            if (!IsTableRow(row)) break;

            if (!IsSeparatorRow(row) && !IsEmptyRow(row))
            {
                var cells = new List<TableCell>();
                if (!TryParseImageRow(row, dir, id, rows.Count, cells)) return null;
                rows.Add(cells);
            }

            lineStart = lineEnd + 1;
            end = lineStart;
        }

        if (rows.Count == 0) return null;

        foreach (var row in rows)
        {
            _imageCount += row.Count;
            if (_firstImagePath is null && row.Count > 0) _firstImagePath = row[0].Path;
        }

        return new TableBlock(id, rows);
    }

    /// One row of a table: every cell must be a picture, or the row is text.
    private static bool TryParseImageRow(
        ReadOnlySpan<char> row, string dir, string tableId, int index, List<TableCell> cells)
    {
        row = row.Trim();
        if (row.Length > 0 && row[0] == '|') row = row[1..];
        if (row.Length > 0 && row[^1] == '|') row = row[..^1];

        while (true)
        {
            var bar = row.IndexOf('|');
            var cell = bar < 0 ? row : row[..bar];

            if (!TryParseImageCell(cell, out var alt, out var url, out var label)) return false;
            if (!ImageFiles.TryResolve(url, dir, out var full) || !ImageFiles.IsPreviewable(full)) return false;

            cells.Add(new TableCell($"{tableId}-{index}-{cells.Count}", alt, label, full));

            if (bar < 0) return true;
            row = row[(bar + 1)..];
        }
    }

    /// A cell: a picture, or a link around one, and the label a gallery puts
    /// beside it — <c>[![Overview](shot.png)](shot.png) **Overview**</c>.
    private static bool TryParseImageCell(ReadOnlySpan<char> cell, out string alt, out string url, out string label)
    {
        alt = string.Empty;
        url = string.Empty;
        label = string.Empty;

        cell = cell.Trim();
        var start = cell.IndexOf("![");
        if (start < 0) return false;

        if (!TryParseImage(cell[start..], out alt, out url, out var used)) return false;

        var tail = cell[(start + used)..];

        // The wrapper's target: the same picture, or the full one it links to.
        if (tail.StartsWith("]("))
        {
            var close = FindClosingParen(tail[1..]);
            if (close >= 0) tail = tail[(close + 2)..];
        }

        label = TrimEmphasis(tail);
        if (label.Length == 0) label = alt;
        return true;
    }

    /// <c>**Overview**</c> and <c>`Overview`</c> are labels, not markdown.
    private static string TrimEmphasis(ReadOnlySpan<char> span)
    {
        span = span.Trim();

        while (span.Length > 1 && span[0] is '*' or '_' or '`') span = span[1..];
        while (span.Length > 0 && span[^1] is '*' or '_' or '`') span = span[..^1];

        return span.Trim().ToString();
    }

    private static bool IsTableRow(ReadOnlySpan<char> row)
    {
        row = row.Trim();
        if (row.Length < 2 || row[0] != '|') return false;

        return row[1..].Contains('|');
    }

    /// <c>| | | |</c>: the header of a gallery table, which has no titles.
    private static bool IsEmptyRow(ReadOnlySpan<char> row)
    {
        foreach (var c in row)
            if (c != '|' && !char.IsWhiteSpace(c)) return false;

        return true;
    }

    /// <c>|---|:--:|---|</c>: alignment only, no cell content.
    private static bool IsSeparatorRow(ReadOnlySpan<char> row)
    {
        row = row.Trim();
        if (row.Length > 0 && row[0] == '|') row = row[1..];
        if (row.Length > 0 && row[^1] == '|') row = row[..^1];

        while (true)
        {
            var bar = row.IndexOf('|');
            var cell = (bar < 0 ? row : row[..bar]).Trim();

            if (cell.Length == 0 || !cell.Contains('-')) return false;

            foreach (var c in cell)
                if (c is not ('-' or ':' or ' ')) return false;

            if (bar < 0) return true;
            row = row[(bar + 1)..];
        }
    }

    private static int FindClosingParen(ReadOnlySpan<char> span)
    {
        var depth = 0;
        var inAngle = false;
        var quote = '\0';

        for (var i = 1; i < span.Length; i++)
        {
            var c = span[i];

            if (c == '\\')
            {
                i++;
                continue;
            }

            if (inAngle)
            {
                if (c == '>') inAngle = false;
                continue;
            }

            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }

            if (c == '<') inAngle = true;
            else if (c is '"' or '\'') quote = c;
            else if (c == '(') depth++;
            else if (c == ')')
            {
                if (depth == 0) return i;
                depth--;
            }
        }

        return -1;
    }

    private static int LastIndexOfPair(ReadOnlySpan<char> span, char first, char second)
    {
        for (var i = span.Length - 2; i >= 0; i--)
            if (span[i] == first && span[i + 1] == second) return i;

        return -1;
    }

    private static bool StartsFence(ReadOnlySpan<char> line, char fence) =>
        line.Length >= 3 && line[0] == fence && line[1] == fence && line[2] == fence;

    private static bool IsIndented(ReadOnlySpan<char> line) =>
        line.Length > 0 && (line[0] == '\t' || (line.Length >= 4
            && line[0] == ' ' && line[1] == ' ' && line[2] == ' ' && line[3] == ' '));

    private abstract class Block(string id)
    {
        public string Id { get; } = id;
    }

    private sealed class TextBlock(string id, string text) : Block(id)
    {
        public string Text { get; } = text;
    }

    private sealed class ImageBlock(string id, string alt, string path) : Block(id)
    {
        public string Alt { get; } = alt;
        public string Path { get; } = path;
        public ImageTexture? Texture;
    }

    /// A table whose cells are pictures: drawn as a grid, one card per cell.
    private sealed class TableBlock(string id, List<List<TableCell>> rows) : Block(id)
    {
        public List<List<TableCell>> Rows { get; } = rows;

        /// The widest row, so a short last row does not narrow the grid.
        public int Columns { get; } = rows.Max(row => row.Count);
    }

    private sealed class TableCell(string id, string alt, string label, string path)
    {
        public string Id { get; } = id;
        public string Alt { get; } = alt;
        public string Label { get; } = label;
        public string Path { get; } = path;
        public ImageTexture? Texture;
    }
}

/// <summary>
/// A decoded picture. It owns the native handle, so every path that stops
/// showing it must call <see cref="Release"/> inside a frame.
/// </summary>
internal sealed class ImageTexture
{
    public ulong Handle;
    public int Width;
    public int Height;
    public string? Source;
    public bool Failed;

    public bool Loaded => Handle != 0;

    public void Load(Ui ui, string path)
    {
        if (Source == path && (Loaded || Failed)) return;

        Release(ui);
        Source = path;

        if (ui.LoadImage(path, out var texture, out var width, out var height))
        {
            Handle = texture;
            Width = width;
            Height = height;
        }
        else
        {
            Failed = true;
        }
    }

    /// Destroys the handle. Only call it from a frame that has not drawn this
    /// picture yet — the frame that did still refers to the handle.
    public void Release(Ui ui)
    {
        if (Handle != 0) ui.DestroyImage(Handle);

        Handle = 0;
        Width = 0;
        Height = 0;
        Source = null;
        Failed = false;
    }
}

/// <summary>
/// A parsed CSV: a header row and a rectangle of cells. Every row is padded to
/// the widest one, so a cell is a single index into one array — a jagged model
/// would cost an object header per row, and a CSV can have a hundred thousand.
/// <para>
/// Rows past <see cref="MaxRows"/> and columns past <see cref="MaxColumns"/> are
/// dropped: the grid keeps a width for 32 columns, and nothing off screen can
/// be reached anyway. The footer reports both limits.
/// </para>
/// </summary>
internal sealed class CsvTable
{
    /// <summary>cui_grid.c stores one width per column, 32 at most.</summary>
    private const int MaxColumns = 32;
    private const int MaxRows = 100_000;

    // A cell further down than this does not decide how wide a column is: one
    // long note in row 300 should not push the other columns off screen.
    private const int WidthSample = 200;
    private const int WidthLimit = 80;

    // A column is numeric when every non-empty value in its first rows is one.
    private const int NumericSample = 500;

    private readonly string[] _headers;
    private readonly string[] _cells;     // RowCount * Columns, row-major
    private readonly bool[] _numeric;

    private CsvTable(string[] headers, string[] cells, bool[] numeric,
        int rows, int extraColumns, bool truncated)
    {
        _headers = headers;
        _cells = cells;
        _numeric = numeric;

        RowCount = rows;
        Columns = headers.Length;
        ExtraColumns = extraColumns;
        Truncated = truncated;
    }

    public int RowCount { get; }
    public int Columns { get; }
    public int ExtraColumns { get; }
    public bool Truncated { get; }

    public string Cell(int row, int column) => _cells[row * Columns + column];

    /// <summary>What the footer says about the table.</summary>
    public string Describe()
    {
        var rows = RowCount == 1 ? "1 row" : $"{RowCount:N0} rows";
        if (Truncated) rows = "first " + rows;

        var columns = ExtraColumns > 0
            ? $"{Columns} of {Columns + ExtraColumns} columns"
            : Columns == 1 ? "1 column" : $"{Columns} columns";

        return $"{rows} x {columns}";
    }

    /// <summary>The grid's columns: content-sized, sortable, numbers on the right.</summary>
    public GridColumn[] ToGridColumns()
    {
        var columns = new GridColumn[Columns];
        for (var column = 0; column < columns.Length; column++)
        {
            columns[column] = new GridColumn(_headers[column], Measure(column),
                _numeric[column] ? TextAlign.Right : TextAlign.Left,
                Sortable: true,
                Priority: column);
        }
        return columns;
    }

    /// <summary>
    /// Re-orders <paramref name="order"/> for a header click. Ties keep the file
    /// order, and blank cells sink to the bottom in both directions — a blank
    /// row at the top of a descending sort reads as a bug.
    /// </summary>
    public void Sort(int[] order, TableSort sort)
    {
        if ((uint)sort.Column >= (uint)Columns)
        {
            for (var row = 0; row < order.Length; row++) order[row] = row;
            return;
        }

        var column = sort.Column;
        var descending = sort.Descending;
        var numbers = _numeric[column] ? Numbers(column) : null;

        Array.Sort(order, (left, right) =>
        {
            int result;

            if (numbers is null)
            {
                var a = _cells[left * Columns + column];
                var b = _cells[right * Columns + column];

                // Decided before the direction flips: a blank cell is missing
                // data, not the smallest value, so it stays at the bottom.
                if (a.Length == 0 || b.Length == 0)
                    return a.Length == b.Length ? left.CompareTo(right) : a.Length == 0 ? 1 : -1;

                result = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                var a = numbers[left];
                var b = numbers[right];

                if (double.IsNaN(a) || double.IsNaN(b))
                    return double.IsNaN(a) == double.IsNaN(b) ? left.CompareTo(right) : double.IsNaN(a) ? 1 : -1;

                result = a.CompareTo(b);
            }

            if (result == 0) return left.CompareTo(right);
            return descending ? -result : result;
        });
    }

    // ---- parsing ----------------------------------------------------------

    public static CsvTable Parse(string text, CancellationToken token)
    {
        var delimiter = DetectDelimiter(text);
        var rows = new List<string[]>(Math.Min(1024, text.Length / 32 + 1));
        var fields = new List<string>(16);
        var position = 0;
        var totalColumns = 0;
        var truncated = false;

        // A byte-order mark is not part of the first header name.
        if (text.Length > 0 && text[0] == '\uFEFF') position = 1;

        while (position < text.Length)
        {
            // Blank lines are spacing, not records.
            var c = text[position];
            if (c is '\r' or '\n')
            {
                position += c == '\r' && position + 1 < text.Length && text[position + 1] == '\n' ? 2 : 1;
                continue;
            }

            // rows[0] is the header, so this keeps MaxRows data rows.
            if (rows.Count > MaxRows)
            {
                truncated = true;
                break;
            }

            token.ThrowIfCancellationRequested();
            ParseRecord(text, ref position, delimiter, fields);

            if (fields.Count > totalColumns) totalColumns = fields.Count;

            // A cell past the grid's limit cannot be shown, so it is not kept.
            var width = Math.Min(fields.Count, MaxColumns);
            var row = new string[width];
            for (var i = 0; i < width; i++) row[i] = fields[i];
            rows.Add(row);
        }

        var columns = Math.Min(totalColumns, MaxColumns);
        var dataRows = Math.Max(0, rows.Count - 1);

        var headers = new string[columns];
        for (var column = 0; column < columns; column++)
        {
            var name = rows.Count > 0 && column < rows[0].Length ? rows[0][column] : string.Empty;
            headers[column] = name.Length > 0 ? name : $"Column {column + 1}";
        }

        // Ragged rows are padded, so a cell is one multiplication away.
        var cells = new string[dataRows * columns];
        for (var row = 0; row < dataRows; row++)
        {
            var source = rows[row + 1];
            var start = row * columns;
            for (var column = 0; column < columns; column++)
                cells[start + column] = column < source.Length ? source[column] : string.Empty;
        }

        var extra = Math.Max(0, totalColumns - MaxColumns);
        return new CsvTable(headers, cells, DetectNumeric(cells, dataRows, columns),
            dataRows, extra, truncated);
    }

    /// <summary>
    /// One record. A quoted field may hold the delimiter, a newline and a
    /// doubled quote; an unquoted one is trimmed, because <c>a, b, c</c> is how
    /// most hand-written files space their values. A quoted field keeps every
    /// space it was given.
    /// </summary>
    private static void ParseRecord(string text, ref int position, char delimiter, List<string> fields)
    {
        fields.Clear();

        while (true)
        {
            string field;

            if (position < text.Length && text[position] == '"')
            {
                position++;
                var start = position;
                StringBuilder? escaped = null;

                while (position < text.Length)
                {
                    if (text[position] != '"')
                    {
                        position++;
                        continue;
                    }

                    if (position + 1 < text.Length && text[position + 1] == '"')
                    {
                        escaped ??= new StringBuilder();
                        escaped.Append(text, start, position - start).Append('"');
                        position += 2;
                        start = position;
                        continue;
                    }

                    break;   // the closing quote
                }

                field = escaped is null
                    ? text[start..position]
                    : escaped.Append(text, start, position - start).ToString();

                if (position < text.Length) position++;   // the closing quote
                while (position < text.Length && text[position] == ' ') position++;
            }
            else
            {
                var start = position;
                while (position < text.Length
                    && text[position] != delimiter && text[position] is not ('\r' or '\n'))
                    position++;

                field = text[start..position].Trim();
            }

            fields.Add(field);

            if (position >= text.Length) return;

            if (text[position] == delimiter)
            {
                position++;
                continue;
            }

            // The record ends at the line break, which belongs to it.
            if (text[position] == '\r') position++;
            if (position < text.Length && text[position] == '\n') position++;
            return;
        }
    }

    /// <summary>
    /// The first line decides: the candidate that appears most often outside
    /// quotes wins, so a .tsv and the semicolon exports Excel writes in Europe
    /// both open without asking.
    /// </summary>
    private static char DetectDelimiter(string text)
    {
        int commas = 0, semicolons = 0, tabs = 0;
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (quoted) continue;
            if (c is '\r' or '\n') break;

            switch (c)
            {
                case ',': commas++; break;
                case ';': semicolons++; break;
                case '\t': tabs++; break;
            }
        }

        if (tabs > commas && tabs >= semicolons) return '\t';
        if (semicolons > commas) return ';';
        return ',';
    }

    /// <summary>
    /// A column sorts as numbers when every non-empty value in its first rows
    /// parses as one. Sampling keeps a million-cell file from paying a second
    /// full pass for a preview; a stray word further down then sorts as
    /// missing, not as zero.
    /// </summary>
    private static bool[] DetectNumeric(string[] cells, int rows, int columns)
    {
        var numeric = new bool[columns];
        var sample = Math.Min(rows, NumericSample);

        for (var column = 0; column < columns; column++)
        {
            var any = false;
            var ok = true;

            for (var row = 0; row < sample && ok; row++)
            {
                var cell = cells[row * columns + column];
                if (cell.Length == 0) continue;

                any = true;
                ok = double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            }

            numeric[column] = any && ok;
        }

        return numeric;
    }

    private double[] Numbers(int column)
    {
        var numbers = new double[RowCount];
        for (var row = 0; row < RowCount; row++)
        {
            var cell = _cells[row * Columns + column];
            numbers[row] = cell.Length > 0
                && double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : double.NaN;
        }
        return numbers;
    }

    private float Measure(int column)
    {
        var width = TextWidth(_headers[column]);
        var sample = Math.Min(RowCount, WidthSample);

        for (var row = 0; row < sample; row++)
        {
            var cell = _cells[row * Columns + column];
            if (cell.Length > WidthLimit) continue;

            var candidate = TextWidth(cell);
            if (candidate > width) width = candidate;
        }

        // Points: 7.2 for an ASCII glyph, a double for CJK, plus the cell pads
        // and the sort chevron the header reserves on the right.
        return Math.Clamp(width + 46f, 84f, 280f);
    }

    private static float TextWidth(string text)
    {
        var width = 0f;
        for (var i = 0; i < text.Length && i < WidthLimit; i++)
            width += char.IsAscii(text[i]) ? 7.2f : 13.5f;
        return width;
    }
}

/// <summary>
/// The result of one background read of a file. <see cref="Text"/> is null for
/// everything that is not text: a picture, a video, a binary, or a file too
/// large for the editor. <see cref="Facts"/> is what the info page shows.
/// </summary>
internal sealed record FileContent(
    FileKind Kind, string? Text, GitInfo Status, string? Head, FileFacts Facts)
{
    /// <summary>
    /// Above this a file is metadata only. The editor holds the text twice —
    /// the string and its UTF-8 buffer — so an unbounded read of a log or a
    /// disk image is the same bug as reading a movie as text.
    /// </summary>
    private const long MaxTextBytes = 32L * 1024 * 1024;

    /// <summary>Enough to tell text from binary and to name a format.</summary>
    private const int SniffBytes = 8 * 1024;

    public static async Task<FileContent> LoadAsync(string path, FileKind kind, CancellationToken token)
    {
        var info = new FileInfo(path);
        var facts = FileFacts.ForFile(info);

        // Video and binary files are never decoded into a string. A 6 GB movie
        // used to be read as text, which cost ~2.5x its size in memory and
        // could not even fit in a string at all.
        if (kind is FileKind.Video)
        {
            var video = facts with { Note = "Playback is not built in; open it in your player." };
            return new FileContent(kind, null, GitInfo.None, null, await MediaFacts.ReadAsync(path, video, token));
        }

        if (kind is FileKind.Image)
            return new FileContent(kind, null, await Git.ReadAsync(path, token), null, facts);

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var head = new byte[SniffBytes];
        var read = 0;
        while (read < head.Length)
        {
            var count = await stream.ReadAsync(head.AsMemory(read), token);
            if (count == 0) break;
            read += count;
        }

        // The bytes decide before the size does: a 190 MB installer is binary,
        // not a text file that happens to be too large to edit.
        var span = head.AsSpan(0, read);
        if (FileFormats.LooksBinary(span))
        {
            // The extension may have said .txt; the bytes say otherwise. Name
            // the real format when the extension is unknown and the signature
            // is one we know.
            var format = FileFormats.FromExtension(path) ?? FileFormats.FromSignature(span) ?? facts.Format;
            return new FileContent(FileKind.Binary, null, GitInfo.None, null,
                facts with { Format = format, Note = "No preview for this file type." });
        }

        if (info.Length > MaxTextBytes)
        {
            return new FileContent(kind, null, GitInfo.None, null,
                facts with { Note = "This file is too large to preview here." });
        }

        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync(token);

        var git = await Git.ReadAsync(path, token);
        string? headText = null;
        if (kind is FileKind.Text && git.Tracked && git.Changed)
            headText = await Git.ReadHeadAsync(path, token);

        return new FileContent(kind, text, git, headText, facts);
    }
}

/// <summary>Everything the info page can say without a preview.</summary>
internal sealed record FileFacts(string Format, long? Length, DateTime? Created, DateTime? Modified)
{
    /// <summary>From the container header; null when the format has none we read.</summary>
    public TimeSpan? Duration { get; init; }

    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Why there is no preview, when the reason is worth stating.</summary>
    public string? Note { get; init; }

    public static FileFacts ForFile(FileInfo info) => new(
        FileFormats.Describe(info.FullName),
        info.Length,
        info.CreationTimeUtc.ToLocalTime(),
        info.LastWriteTimeUtc.ToLocalTime());

    public static FileFacts ForFolder(string path)
    {
        var info = new DirectoryInfo(path);
        return new FileFacts("Folder", null, info.CreationTimeUtc.ToLocalTime(),
            info.LastWriteTimeUtc.ToLocalTime())
        {
            Note = "Folders are not previewed. Pass a file inside it instead.",
        };
    }

    public static FileFacts ForMissing(string path) =>
        new(FileFormats.Describe(path), null, null, null)
        {
            Note = "This path does not exist.",
        };
}

/// <summary>The extension lists that decide whether a file is a video.</summary>
internal static class MediaFiles
{
    public static bool IsVideo(string path)
    {
        var ext = Path.GetExtension(path.AsSpan());

        return ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mov", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webm", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".avi", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".wmv", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".flv", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".m2ts", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".3gp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ogv", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Format names, from the extension when it is known and the bytes otherwise.</summary>
internal static class FileFormats
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".md"] = "Markdown",
        [".markdown"] = "Markdown",
        [".txt"] = "Text file",
        [".log"] = "Log file",
        [".csv"] = "CSV table",
        [".tsv"] = "TSV table",
        [".json"] = "JSON",
        [".jsonc"] = "JSON",
        [".xml"] = "XML",
        [".yaml"] = "YAML",
        [".yml"] = "YAML",
        [".html"] = "HTML",
        [".htm"] = "HTML",
        [".cs"] = "C# source",
        [".csx"] = "C# script",
        [".js"] = "JavaScript",
        [".ts"] = "TypeScript",
        [".py"] = "Python source",
        [".rs"] = "Rust source",
        [".go"] = "Go source",
        [".sql"] = "SQL script",
        [".png"] = "PNG image",
        [".jpg"] = "JPEG image",
        [".jpeg"] = "JPEG image",
        [".gif"] = "GIF image",
        [".bmp"] = "Bitmap image",
        [".webp"] = "WebP image",
        [".tif"] = "TIFF image",
        [".tiff"] = "TIFF image",
        [".svg"] = "SVG image",
        [".ico"] = "Windows icon",
        [".heic"] = "HEIC image",
        [".mp4"] = "MP4 video",
        [".m4v"] = "MP4 video",
        [".mov"] = "QuickTime video",
        [".mkv"] = "Matroska video",
        [".webm"] = "WebM video",
        [".avi"] = "AVI video",
        [".wmv"] = "Windows Media video",
        [".flv"] = "Flash video",
        [".mpg"] = "MPEG video",
        [".mpeg"] = "MPEG video",
        [".m2ts"] = "MPEG transport stream",
        [".3gp"] = "3GPP video",
        [".ogv"] = "Ogg video",
        [".zip"] = "ZIP archive",
        [".7z"] = "7-Zip archive",
        [".rar"] = "RAR archive",
        [".tar"] = "Tar archive",
        [".gz"] = "Gzip archive",
        [".bz2"] = "Bzip2 archive",
        [".xz"] = "XZ archive",
        [".zst"] = "Zstandard archive",
        [".iso"] = "Disc image",
        [".img"] = "Disc image",
        [".exe"] = "Windows executable",
        [".dll"] = "Windows library",
        [".msi"] = "Windows installer",
        [".sys"] = "Windows driver",
        [".pdf"] = "PDF document",
        [".docx"] = "Word document",
        [".doc"] = "Word document",
        [".xlsx"] = "Excel workbook",
        [".xls"] = "Excel workbook",
        [".pptx"] = "PowerPoint deck",
        [".ppt"] = "PowerPoint deck",
        [".ttf"] = "TrueType font",
        [".otf"] = "OpenType font",
        [".woff"] = "Web font",
        [".woff2"] = "Web font",
        [".sqlite"] = "SQLite database",
        [".sqlite3"] = "SQLite database",
        [".db"] = "Database",
        [".mmdb"] = "MaxMind database",
        [".wasm"] = "WebAssembly module",
        [".class"] = "Java class",
        [".jar"] = "Java archive",
        [".apk"] = "Android package",
        [".pdb"] = "Program database",
        [".stl"] = "3D model (STL)",
        [".dxf"] = "CAD drawing (DXF)",
        [".stp"] = "CAD model (STEP)",
        [".step"] = "CAD model (STEP)",
        [".psd"] = "Photoshop document",
    };

    /// <summary>
    /// The name a known extension stands for, or null. The extension wins over
    /// the signature, because ".docx" is a better answer than the ZIP it is.
    /// </summary>
    public static string? FromExtension(string path) =>
        ByExtension.TryGetValue(Path.GetExtension(path), out var name) ? name : null;

    /// <summary>The name to show for a path, falling back to the extension itself.</summary>
    public static string Describe(string path)
    {
        if (FromExtension(path) is { } known) return known;

        var ext = Path.GetExtension(path);
        return ext.Length > 1
            ? $"{ext[1..].ToUpperInvariant()} file"
            : "File";
    }

    /// <summary>The format a file's first bytes announce, or null when they say nothing.</summary>
    public static string? FromSignature(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith("%PDF"u8)) return "PDF document";
        if (head.StartsWith("MZ"u8)) return "Windows executable";
        if (head.StartsWith("\x7fELF"u8)) return "ELF binary";
        if (head.StartsWith("SQLite format 3\0"u8)) return "SQLite database";
        if (head.StartsWith("\x1f\x8b"u8)) return "Gzip archive";
        if (head.StartsWith("7z\xbc\xaf\x27\x1c"u8)) return "7-Zip archive";
        if (head.StartsWith("Rar!"u8)) return "RAR archive";
        if (head.StartsWith("PK"u8) && head.Length > 3 && head[2] is 3 or 5 or 7) return "ZIP archive";
        if (head.StartsWith("\xd0\xcf\x11\xe0"u8)) return "Office document";
        if (head.StartsWith("\x89PNG"u8)) return "PNG image";
        if (head.StartsWith("\xff\xd8\xff"u8)) return "JPEG image";
        if (head.StartsWith("GIF8"u8)) return "GIF image";
        if (head.StartsWith("\x1a\x45\xdf\xa3"u8)) return "Matroska video";
        if (head.StartsWith("OggS"u8)) return "Ogg media";
        if (head.Length >= 12 && head[4..8].SequenceEqual("ftyp"u8)) return "MP4 video";
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8))
        {
            if (head[8..12].SequenceEqual("AVI "u8)) return "AVI video";
            if (head[8..12].SequenceEqual("WEBP"u8)) return "WebP image";
            if (head[8..12].SequenceEqual("WAVE"u8)) return "WAV audio";
        }

        return null;
    }

    /// <summary>
    /// Whether the first bytes are something other than UTF-8 text. A NUL is
    /// decisive; otherwise the bytes are validated as UTF-8, tolerating a
    /// character cut in half by the end of the window. A UTF-16 BOM is text.
    /// </summary>
    public static bool LooksBinary(ReadOnlySpan<byte> head)
    {
        if (head.Length == 0) return false;
        if (head.StartsWith("\xff\xfe"u8) || head.StartsWith("\xfe\xff"u8)) return false;
        if (head.IndexOf((byte)0) >= 0) return true;

        var chars = ArrayPool<char>.Shared.Rent(head.Length);
        try
        {
            var status = Utf8.ToUtf16(head, chars, out _, out _,
                replaceInvalidSequences: false, isFinalBlock: false);
            return status is OperationStatus.InvalidData;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }
}

/// <summary>
/// Reads the container header of an MP4/MOV to get its duration and picture
/// size. It seeks over the media data, so the cost is one small read of the
/// <c>moov</c> box, not the size of the film.
/// </summary>
internal static class MediaFacts
{
    /// <summary>A movie header is a few KB; anything bigger is not one.</summary>
    private const int MaxMoovBytes = 16 * 1024 * 1024;

    public static async Task<FileFacts> ReadAsync(string path, FileFacts facts, CancellationToken token)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);

            if (await FindMovieBoxAsync(stream, token) is not { } moov) return facts;

            TimeSpan? duration = null;
            var width = 0;
            var height = 0;
            ReadMovieHeader(moov, ref duration, ref width, ref height);

            return facts with { Duration = duration, Width = width, Height = height };
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            // A truncated or non-standard container is still a file with a
            // size and a date; those facts stand.
            return facts;
        }
    }

    private static async Task<byte[]?> FindMovieBoxAsync(FileStream stream, CancellationToken token)
    {
        var header = new byte[16];
        var position = 0L;

        // A sane file has a handful of top-level boxes. The cap stops a corrupt
        // size field from turning this into an endless walk.
        for (var boxes = 0; boxes < 64 && position + 8 <= stream.Length; boxes++)
        {
            stream.Position = position;
            if (await ReadFullAsync(stream, header.AsMemory(0, 8), token) < 8) return null;

            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(header);
            var headerSize = 8L;

            if (size == 1)
            {
                if (await ReadFullAsync(stream, header.AsMemory(8, 8), token) < 8) return null;
                var large = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                if (large > long.MaxValue) return null;
                size = (long)large;
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = stream.Length - position;
            }

            if (size < headerSize || position + size > stream.Length) return null;

            if (header.AsSpan(4, 4).SequenceEqual("moov"u8))
            {
                var payload = size - headerSize;
                if (payload > MaxMoovBytes) return null;

                var box = new byte[(int)payload];
                return await ReadFullAsync(stream, box, token) == payload ? box : null;
            }

            position += size;
        }

        return null;
    }

    private static void ReadMovieHeader(ReadOnlySpan<byte> moov, ref TimeSpan? duration, ref int width, ref int height)
    {
        var position = 0;

        while (position + 8 <= moov.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(moov[position..]);
            if (size < 8 || position + size > moov.Length) break;

            var type = moov.Slice(position + 4, 4);
            var payload = moov.Slice(position + 8, size - 8);

            if (type.SequenceEqual("mvhd"u8)) ReadDuration(payload, ref duration);
            else if (type.SequenceEqual("trak"u8)) ReadTrackSize(payload, ref width, ref height);

            position += size;
        }
    }

    private static void ReadDuration(ReadOnlySpan<byte> mvhd, ref TimeSpan? duration)
    {
        if (mvhd.Length < 20) return;

        uint timescale;
        ulong length;

        if (mvhd[0] == 1)
        {
            if (mvhd.Length < 32) return;
            timescale = BinaryPrimitives.ReadUInt32BigEndian(mvhd[20..]);
            length = BinaryPrimitives.ReadUInt64BigEndian(mvhd[24..]);
        }
        else
        {
            timescale = BinaryPrimitives.ReadUInt32BigEndian(mvhd[12..]);
            length = BinaryPrimitives.ReadUInt32BigEndian(mvhd[16..]);
            if (length == uint.MaxValue) return;   // "unknown", per the spec
        }

        if (timescale == 0 || length == 0) return;
        duration = TimeSpan.FromSeconds((double)length / timescale);
    }

    /// <summary>
    /// A track's picture size lives in its <c>tkhd</c>, as 16.16 fixed point.
    /// Audio tracks report zero, so the largest non-zero track is the video.
    /// </summary>
    private static void ReadTrackSize(ReadOnlySpan<byte> trak, ref int width, ref int height)
    {
        var position = 0;

        while (position + 8 <= trak.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(trak[position..]);
            if (size < 8 || position + size > trak.Length) break;

            if (trak.Slice(position + 4, 4).SequenceEqual("tkhd"u8))
            {
                var tkhd = trak.Slice(position + 8, size - 8);
                var offset = tkhd.Length > 0 && tkhd[0] == 1 ? 88 : 76;
                if (tkhd.Length < offset + 8) return;

                var w = (int)(BinaryPrimitives.ReadUInt32BigEndian(tkhd[offset..]) >> 16);
                var h = (int)(BinaryPrimitives.ReadUInt32BigEndian(tkhd[(offset + 4)..]) >> 16);

                if (w is > 0 and <= 16384 && h is > 0 and <= 16384
                    && (long)w * h > (long)width * height)
                {
                    width = w;
                    height = h;
                }
                return;
            }

            position += size;
        }
    }

    private static async Task<int> ReadFullAsync(FileStream stream, Memory<byte> buffer, CancellationToken token)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], token);
            if (read == 0) break;
            total += read;
        }

        return total;
    }
}

internal sealed record GitInfo(bool Tracked, bool Changed)
{
    public static readonly GitInfo None = new(false, false);
}

/// <summary>
/// git, off the frame. Spawning a process in Draw freezes the window for as
/// long as it runs, so the status and the HEAD blob are read on the pool and
/// published as one value.
/// </summary>
internal static class Git
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// One process for both questions: is the file tracked, and has it changed?
    /// An empty porcelain line is a clean tracked file; ?? and !! are untracked
    /// and ignored; anything else is a tracked file with local changes.
    /// </summary>
    public static async Task<GitInfo> ReadAsync(string path, CancellationToken token)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is null) return GitInfo.None;

        var (ok, output) = await RunAsync(
            ["-C", dir, "status", "--porcelain", "--ignored=matching", "--", path], token);
        if (!ok) return GitInfo.None;

        var line = output.TrimEnd();
        if (line.Length == 0) return new GitInfo(true, false);
        if (line.StartsWith("??", StringComparison.Ordinal) || line.StartsWith("!!", StringComparison.Ordinal))
            return new GitInfo(false, false);

        return new GitInfo(true, true);
    }

    /// <summary>
    /// The file's text at HEAD. HEAD:./name resolves from the path passed to
    /// git -C, so no second process is needed to find the repository root.
    /// </summary>
    public static async Task<string?> ReadHeadAsync(string path, CancellationToken token)
    {
        var dir = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (dir is null || name.Length == 0) return null;

        var (ok, output) = await RunAsync(["-C", dir, "cat-file", "blob", $"HEAD:./{name}"], token);
        return ok ? output : null;
    }

    private static async Task<(bool Ok, string Output)> RunAsync(string[] args, CancellationToken token)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        foreach (var argument in args) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start()) return (false, string.Empty);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(Timeout);

            // Drain both pipes while waiting: a full stderr pipe would stop git
            // before it exits, and WaitForExit would then wait forever.
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token));

            return (process.ExitCode == 0, await stdout);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (false, string.Empty);
        }
        catch
        {
            return (false, string.Empty);
        }
    }
}

internal static class ImageFiles
{
    public static bool IsPreviewable(string path)
    {
        var ext = Path.GetExtension(path.AsSpan());
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsImage(string path)
    {
        if (IsPreviewable(path)) return true;

        var ext = Path.GetExtension(path.AsSpan());
        return ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".tif", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".svg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ico", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".heic", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsMarkdown(string path)
    {
        var ext = Path.GetExtension(path.AsSpan());
        return ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A link's destination as a file on disk, relative to the document that
    /// named it. Web and data URLs have no local file and answer false.
    /// </summary>
    public static bool TryResolve(string url, string dir, out string full)
    {
        full = string.Empty;

        var value = url.Trim();
        if (value.Length == 0) return false;

        if (value.Length >= 2 && value[0] == '<' && value[^1] == '>')
            value = value[1..^1].Trim();

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (!uri.IsFile) return false;
            value = uri.LocalPath;
        }

        var fragment = value.IndexOf('#');
        if (fragment >= 0) value = value[..fragment];

        try { value = Uri.UnescapeDataString(value); } catch (UriFormatException) { }
        try { full = Path.GetFullPath(value, dir); } catch { return false; }

        return File.Exists(full);
    }
}

/// The handful of window calls the sample needs on top of CUI's own window APIs.
[SupportedOSPlatform("windows")]
internal static class Native
{
    internal const int SmCxScreen = 0;
    internal const int SmCyScreen = 1;
    internal const int SmCxWorkarea = 78;
    internal const int SmCyWorkarea = 79;

    internal const uint SwpNosize = 0x0001;
    internal const uint SwpNoactivate = 0x0010;

    [DllImport("user32.dll")]
    internal static extern int SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    internal static extern int GetWindowRect(nint hWnd, out Rect rect);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    internal struct Rect
    {
        internal int Left, Top, Right, Bottom;
    }
}
