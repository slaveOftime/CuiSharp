using System.Runtime.InteropServices;
using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>
/// One snapshot of what this process is actually running on: the renderer that was chosen, the
/// native library it loaded, the display and dialog helper it will find.
/// <para>
/// A station, a VM or a remote session fails in ways nobody can reproduce from a description, and
/// every app ends up writing this by hand. Print it at startup, or on a <c>--check</c> flag, and the
/// report says which of the two machines it came from.
/// </para>
/// </summary>
public sealed record CuiDiagnostics
{
    /// <summary>The OS and runtime: <c>win-x64</c>, <c>win-arm64</c>, <c>linux-x64</c>, <c>linux-arm64</c>, <c>osx-arm64</c>.</summary>
    public required string Platform { get; init; }

    /// <summary>The CUI backend that created the window: <c>win32</c>, <c>x11</c>, <c>cocoa</c>.</summary>
    public required string Backend { get; init; }

    /// <summary>True when OpenGL was unavailable or unhelpful and the software rasterizer draws.</summary>
    public required bool SoftwareRenderer { get; init; }

    /// <summary>The glibc floor the Linux binary promises; empty on other platforms.</summary>
    public required string GlibcFloor { get; init; }

    /// <summary>The display the window will use; empty when there is none.</summary>
    public required string Display { get; init; }

    /// <summary>The program a Linux file dialog will shell out to, or empty when none was found.</summary>
    public required string DialogHelper { get; init; }

    /// <summary>The package version whose managed and native halves are loaded together.</summary>
    public required string Version { get; init; }

    /// <summary>Everything above as the lines a person or a bug report reads.</summary>
    public string Report()
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"CuiSharp {Version} on {Platform}");
        text.AppendLine($"  backend:     {Backend}");
        text.AppendLine($"  renderer:    {(SoftwareRenderer ? "software" : "OpenGL")}");
        if (GlibcFloor.Length > 0) text.AppendLine($"  glibc floor: {GlibcFloor}");
        text.AppendLine($"  display:     {(Display.Length > 0 ? Display : "none")}");
        if (OperatingSystem.IsLinux()) text.AppendLine($"  file dialog: {(DialogHelper.Length > 0 ? DialogHelper : "none found (zenity or kdialog is needed)")}");
        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// Reads the machine, and the window when there is one. Pass the window from
    /// <see cref="CuiAppOptions.WindowCreated"/> to learn which renderer actually won.
    /// </summary>
    public static CuiDiagnostics Report(CuiWindow? window = null)
    {
        string rid = RuntimeInformation.RuntimeIdentifier;
        return new CuiDiagnostics
        {
            Platform = rid,
            Backend = window?.Backend ?? BackendOnly(),
            SoftwareRenderer = window?.IsSoftwareRenderer ?? false,
            GlibcFloor = OperatingSystem.IsLinux() ? CuiNative.LinuxFloor : "",
            Display = Environment.GetEnvironmentVariable("DISPLAY")
                ?? Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")
                ?? "",
            DialogHelper = DialogHelperFound() ?? "",
            Version = typeof(CuiDiagnostics).Assembly.GetName().Version?.ToString(3) ?? "unknown",
        };
    }

    /// <summary>The backend name CUI was built with, without needing a window.</summary>
    private static string BackendOnly()
    {
        try
        {
            // cui_app_backend ignores its argument, which is what makes this safe before a window.
            var name = Native.AppBackend(0);
            return Marshal.PtrToStringUTF8(name) ?? "unknown";
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return "unavailable";
        }
    }

    /// <summary>Which helper the Linux dialog falls back to, mirroring CUI: zenity, then kdialog.</summary>
    private static string? DialogHelperFound()
    {
        if (!OperatingSystem.IsLinux()) return null;

        foreach (string name in (string[])["zenity", "kdialog"])
        {
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
            {
                if (directory.Length > 0 && File.Exists(Path.Combine(directory, name))) return name;
            }
        }

        return null;
    }
}
