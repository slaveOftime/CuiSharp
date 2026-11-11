// Page 28 of 28: a file browser, open and save dialogs, and a small shell over
// the same folder.
// Part of the CuiSharp.Gallery sample: Files
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    private string _folder = ".";
    private string _chosenFile = "";
    private bool _openDialog, _saveDialog;
    private Terminal? _shell;

    private void PageFiles(Ui ui)
    {
        Section(ui, "File browser", "Double-click to open; type to jump; Backspace goes up.");
        FileBrowserResult picked = ui.FileBrowser("files", ref _folder, 1024, new FileBrowserOpts { Height = Size.Px(380) });
        if (picked.Picked is not null) _chosenFile = picked.Picked;
        using (ui.Row(new Box { Gap = 8, Align = Align.Center, Wrap = true }))
        {
            if (ui.Button("Open…", new ButtonOpts { Icon = Icons.FolderOpen })) _openDialog = true;
            if (ui.Button("Save as…", new ButtonOpts { Icon = Icons.Download })) _saveDialog = true;
            if (_chosenFile.Length > 0) ui.Muted(_chosenFile);
        }
        ui.FilePicker("open", ref _openDialog, ref _chosenFile, 1024);
        ui.FilePicker("save", ref _saveDialog, ref _chosenFile, 1024, new FilePickerOpts { Save = true, Name = "untitled.txt" });

        // A small shell over the same folder; the buffer outlives the frame.
        _shell ??= NewShell();

        Section(ui, "Terminal", "Colors, \\r progress, history on Up and Down, Ctrl+C, Ctrl+L, drag to select.");
        TerminalResult result = ui.Terminal("shell", _shell, new TerminalOpts { Height = 300, Prompt = "> " });
        if (result.Command is { Length: > 0 } command) RunCommand(command);
        if (result.Interrupt) ui.Toast("Interrupted");
    }

    private static Terminal NewShell()
    {
        var shell = new Terminal(maxLines: 1024);
        shell.Write("Type \u001b[1mhelp\u001b[0m for the commands.\n");
        return shell;
    }

    private void RunCommand(string command)
    {
        Terminal shell = _shell!;
        switch (command)
        {
            case "help":
                shell.Write(
                    "  \u001b[36mls\u001b[0m      the folder above\n" +
                    "  \u001b[36mcolors\u001b[0m  the 16 colors\n" +
                    "  \u001b[36mbuild\u001b[0m   a progress bar\n" +
                    "  \u001b[36mecho\u001b[0m    prints its words\n" +
                    "  \u001b[36mclear\u001b[0m   empties the screen\n");
                return;
            case "ls":
                foreach (FileEntry entry in CuiFiles.ListDirectory(_folder))
                {
                    shell.WriteLine(entry.Dir ? $"\u001b[1;34m{entry.Name}/\u001b[0m" : entry.Name);
                }
                return;
            case "colors":
                for (int k = 0; k < 16; k++)
                {
                    shell.Write($"\u001b[{(k < 8 ? 30 + k : 82 + k)}m██\u001b[0m{(k is 7 or 15 ? "\n" : " ")}");
                }
                return;
            case "build":
                for (int k = 0; k <= 20; k++)
                {
                    shell.Write($"\r\u001b[32m{new string('█', k)}{new string('░', 20 - k)}\u001b[0m {k * 5,3}%");
                }
                shell.Write("\n\u001b[32mdone\u001b[0m\n");
                return;
            case "clear":
                shell.Clear();
                return;
        }

        if (command.StartsWith("echo", StringComparison.Ordinal) && (command.Length == 4 || command[4] == ' '))
        {
            shell.WriteLine(command.Length > 5 ? command[5..] : "");
            return;
        }

        if (command.Length > 0) shell.WriteLine($"\u001b[31m{command}: command not found\u001b[0m");
    }
}
