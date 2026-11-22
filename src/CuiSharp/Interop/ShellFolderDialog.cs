// Hand-written, not generated: the shell's own folder picker. CUI reaches the platform dialog
// through cui_app_file_dialog, and its Windows folder case is the legacy SHBrowseForFolderW tree;
// the dialog Explorer itself opens is IFileOpenDialog with FOS_PICKFOLDERS, reached here instead.
// Every COM call goes through the interface's vtable as a function pointer, because the interop
// metadata that [ComImport] would need is exactly what NativeAOT cannot trim.
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CuiSharp.Interop;

[SupportedOSPlatform("windows")]
internal static unsafe partial class ShellFolderDialog
{
    private const int SFalse = 1;
    private const int HrCancelled = unchecked((int)0x800704C7);

    private const uint CoinitApartmentThreaded = 0x2;
    private const uint ClsctxInprocServer = 0x1;

    // FOS_PICKFOLDERS picks a folder instead of a file; the other three are what the folder dialog
    // needs to behave like a folder dialog rather than a file dialog that returns directories.
    private const uint FosNoChangeDir = 0x00000008;
    private const uint FosPickFolders = 0x00000020;
    private const uint FosForceFileSystem = 0x00000040;
    private const uint FosPathMustExist = 0x00000800;

    // SIGDN_FILESYSPATH: the result as a path this process can open, not a shell identity.
    private const uint SigdnFileSysPath = 0x80058000;

    private static readonly Guid ClsidFileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
    private static readonly Guid IidFileDialog = new("42F85136-DB7E-439C-85F1-E4075D135FC8");

    // IFileDialog's vtable, after the three IUnknown slots: Show is IModalWindow's single method,
    // then SetFileTypes, SetFileTypeIndex, GetFileTypeIndex, Advise, Unadvise, SetOptions, GetOptions
    // and onwards to SetTitle and, at the end of its own block, GetResult.
    private const int ShowSlot = 3;
    private const int SetOptionsSlot = 9;
    private const int GetOptionsSlot = 10;
    private const int SetTitleSlot = 17;
    private const int GetResultSlot = 20;

    // IShellItem, after the same three: BindToHandler, GetParent, GetDisplayName.
    private const int GetDisplayNameSlot = 5;

    private const int ReleaseSlot = 2;

    /// <summary>
    /// Shows the folder picker and returns the folder the user chose, or null when they cancelled.
    /// A modal dialog blocks, so this is called from a click in a frame and nowhere else.
    /// </summary>
    internal static string? Pick(nint owner, string? title) => RunSta(() => Show(owner, title));

    /// <summary>
    /// The options the dialog is built with, read back from the live object. A cancel cannot be
    /// observed in a test, but a dialog that came up as a file picker can: the vtable slot that
    /// carries the options is the one thing here that fails silently if it moves.
    /// </summary>
    internal static uint ConfiguredOptions(string? title = null) => RunSta(() =>
    {
        nint dialog = Create(title);
        try
        {
            return Options(dialog);
        }
        finally
        {
            Release(dialog);
        }
    });

    private static uint Options(nint dialog)
    {
        uint options = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)(*(nint**)dialog)[GetOptionsSlot])(dialog, &options));
        return options;
    }

    private static string? Show(nint owner, string? title)
    {
        nint dialog = Create(title);
        nint item = 0;
        nint path = 0;
        try
        {
            int hr = ((delegate* unmanaged[Stdcall]<nint, nint, int>)(*(nint**)dialog)[ShowSlot])(dialog, owner);
            if (hr == HrCancelled) return null;
            Check(hr);

            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(nint**)dialog)[GetResultSlot])(dialog, &item));
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint**)item)[GetDisplayNameSlot])(item, SigdnFileSysPath, &path));
            return Marshal.PtrToStringUni(path);
        }
        finally
        {
            if (path != 0) CoTaskMemFree(path);
            Release(item);
            Release(dialog);
        }
    }

    /// <summary>The dialog, configured to pick a folder, or lean on its default title.</summary>
    private static nint Create(string? title)
    {
        Guid clsid = ClsidFileOpenDialog;
        Guid iid = IidFileDialog;
        nint dialog = 0;
        Check(CoCreateInstance(&clsid, 0, ClsctxInprocServer, &iid, &dialog));
        try
        {
            nint* vtbl = *(nint**)dialog;
            uint options = Options(dialog);
            options |= FosPickFolders | FosForceFileSystem | FosPathMustExist | FosNoChangeDir;
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)vtbl[SetOptionsSlot])(dialog, options));

            if (!string.IsNullOrEmpty(title))
            {
                fixed (char* text = title)
                {
                    Check(((delegate* unmanaged[Stdcall]<nint, char*, int>)vtbl[SetTitleSlot])(dialog, text));
                }
            }

            return dialog;
        }
        catch
        {
            Release(dialog);
            throw;
        }
    }

    private static void Release(nint unknown)
    {
        if (unknown == 0) return;
        _ = ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)unknown)[ReleaseSlot])(unknown);
    }

    /// <summary>A COM failure as the exception its HRESULT names, so the provider has a message.</summary>
    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    /// <summary>
    /// Runs <paramref name="action"/> on a single-threaded apartment, which the shell's file dialogs
    /// require. The thread that is drawing a frame is used when COM there is uninitialized, so the
    /// dialog's own modal loop keeps dispatching the window's messages and the app does not freeze
    /// behind it; a frame thread that is already in a multi-threaded apartment gets a dedicated STA
    /// worker instead.
    /// </summary>
    private static T RunSta<T>(Func<T> action)
    {
        int hr = CoInitializeEx(0, CoinitApartmentThreaded);
        if (hr is 0 or SFalse)
        {
            try
            {
                return action();
            }
            finally
            {
                CoUninitialize();
            }
        }

        T? result = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            int initialized = CoInitializeEx(0, CoinitApartmentThreaded);
            try
            {
                result = action();
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                if (initialized is 0 or SFalse) CoUninitialize();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result!;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint init);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* instance);

    [LibraryImport("ole32.dll")]
    private static partial void CoTaskMemFree(nint memory);
}
