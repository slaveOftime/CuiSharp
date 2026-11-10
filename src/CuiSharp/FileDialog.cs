using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>
/// Picks a file, a folder or a save path with the OS's own dialog, and returns what happened
/// instead of a bool and an empty string.
/// <para>
/// Native dialogs are modal and blocking on every platform, so call this from a button's click in a
/// frame, never from work that has to keep drawing. The default implementation asks the OS through
/// CUI; a test replaces it, so a picker flow can be exercised without a display or a dialog.
/// </para>
/// </summary>
public interface IFileDialogProvider
{
    /// <summary>Shows the dialog and returns the choice, or a cancelled result.</summary>
    FilePickResult Pick(CuiWindow window, FileDialogOpts options);
}

/// <summary>What a picker did: a path, a cancel, or a failure with a reason worth printing.</summary>
public sealed record FilePickResult
{
    private FilePickResult(bool picked, string path, string? error)
    {
        Picked = picked;
        Path = path;
        Error = error;
    }

    /// <summary>True when the user chose something.</summary>
    public bool Picked { get; }

    /// <summary>The chosen path, or an empty string.</summary>
    public string Path { get; }

    /// <summary>Cancelled, or the dialog failed before the user could choose.</summary>
    public bool Cancelled => !Picked;

    /// <summary>Why the dialog failed, or null. A cancel is not a failure.</summary>
    public string? Error { get; }

    /// <summary>The user chose <paramref name="path"/>.</summary>
    public static FilePickResult Chosen(string path) => new(path.Length > 0, path, null);

    /// <summary>The user closed the dialog without choosing.</summary>
    public static FilePickResult CancelledResult { get; } = new(false, "", null);

    /// <summary>The dialog could not be shown.</summary>
    public static FilePickResult Failed(string error) => new(false, "", error);

    /// <inheritdoc />
    public override string ToString() => Picked ? Path : Error is null ? "cancelled" : "failed: " + Error;
}

/// <summary>The OS dialog, through CUI. This is what an app gets unless it supplies its own.</summary>
public sealed class NativeFileDialogProvider : IFileDialogProvider
{
    /// <summary>Shares the one implementation, which holds no state.</summary>
    public static NativeFileDialogProvider Instance { get; } = new();

    /// <inheritdoc />
    public FilePickResult Pick(CuiWindow window, FileDialogOpts options)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (window.NativeWindow == 0) return FilePickResult.Failed("there is no window yet");

        // PATH_MAX on Linux, and Windows paths are far shorter: one buffer for both.
        const int Capacity = 4096;
        byte[] buffer = new byte[Capacity];
        var native = options.ToNative();
        try
        {
            unsafe
            {
                fixed (byte* p = buffer)
                {
                    if (Native.AppFileDialog(window.App, in native, (nint)p, Capacity) == 0)
                        return FilePickResult.CancelledResult;

                    int length = 0;
                    while (length < Capacity && p[length] != 0) length++;
                    return FilePickResult.Chosen(System.Text.Encoding.UTF8.GetString(buffer, 0, length));
                }
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw CuiSharpNativeException.From(error);
        }
        finally
        {
            // The strings were marshalled into the frame arena; nothing to free, but the buffer
            // exists so the native side has somewhere to write.
        }
    }
}
