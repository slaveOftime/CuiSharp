namespace CuiSharp.Tests;

/// <summary>
/// The folder picker the framework opens on Windows. The dialog itself cannot be shown in a test —
/// it is modal and needs a person — but the object behind it can: the options are read back from the
/// live COM object, so a vtable slot that moved fails here instead of opening a file picker where a
/// folder was asked for.
/// </summary>
public class ShellFolderDialogTests
{
    // Windows' own FOS_ values, written out rather than shared with the code under test: a wrong
    // constant on one side of the assertion is exactly what this test is for.
    private const uint NoChangeDir = 0x00000008;
    private const uint PickFolders = 0x00000020;
    private const uint ForceFileSystem = 0x00000040;
    private const uint PathMustExist = 0x00000800;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Choose folder")]
    public void TheDialogIsBuiltToPickAFolder(string? title)
    {
        if (!OperatingSystem.IsWindows()) return;

        uint options = CuiSharp.Interop.ShellFolderDialog.ConfiguredOptions(title);

        Assert.True((options & PickFolders) != 0, $"FOS_PICKFOLDERS is not set (options 0x{options:X8})");
        Assert.True((options & ForceFileSystem) != 0, $"FOS_FORCEFILESYSTEM is not set (options 0x{options:X8})");
        Assert.True((options & PathMustExist) != 0, $"FOS_PATHMUSTEXIST is not set (options 0x{options:X8})");
        Assert.True((options & NoChangeDir) != 0, $"FOS_NOCHANGEDIR is not set (options 0x{options:X8})");
    }

    [Fact]
    public void TheDialogRunsWhenCalledFromAnStaThread()
    {
        if (!OperatingSystem.IsWindows()) return;

        uint options = 0;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                options = CuiSharp.Interop.ShellFolderDialog.ConfiguredOptions("Select folder");
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
        Assert.True((options & PickFolders) != 0, $"FOS_PICKFOLDERS is not set on STA (options 0x{options:X8})");
    }
}
