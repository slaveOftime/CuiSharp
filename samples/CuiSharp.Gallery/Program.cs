using CuiSharp;

namespace CuiSharp.Gallery;

/// <summary>
/// The whole host. CUI creates the window, owns the message loop and the GL
/// context; this file only supplies a draw callback.
/// </summary>
internal static class Program
{
    private static readonly GalleryApp Gallery = new();
    private static string? _failure;

    public static int Main(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith("--page=", StringComparison.Ordinal)
                && int.TryParse(arg.AsSpan(7), out int page))
            {
                Gallery.Page = page;
            }
        }

        return CuiApp.Run(
            new CuiAppOptions
            {
                Title = "CUI Gallery - C# / NativeAOT",
                Width = 1240,
                Height = 820,
                MinWidth = 720,
                MinHeight = 480,
                Accent = Accent.Violet,
                // The theme is the window's to start with and the app's to change: the sidebar
                // switch and Ctrl+J assign `ui.Theme`, and CUI animates the change.
                Theme = string.Equals(Environment.GetEnvironmentVariable("CUI_THEME"), "dark", StringComparison.OrdinalIgnoreCase)
                    ? Theme.Dark
                    : Theme.Light,
            },
            Draw);
    }

    // Kept separate from Main so `dotnet watch` can hot reload the UI.
    private static void Draw(Ui ui)
    {
        if (_failure is not null) return;

        try
        {
            Gallery.Frame(ui);
        }
        catch (Exception ex)
        {
            // A throw here would take down the window; report it and stop
            // drawing so the message stays readable in the watch console.
            _failure = ex.ToString();
            Console.Error.WriteLine(_failure);
        }
    }
}
