using CuiSharp.Hosting;
using CuiSharp.Testing;

namespace CuiSharp.Tests;

/// <summary>
/// The pieces an app asks for on day one and used to write itself: what machine am I on, a golden
/// image, and a picker whose flow can be tested without a dialog.
/// </summary>
public class AppSupportTests
{
    [Fact]
    public void DiagnosticsNameTheRendererAndTheAbiFloor()
    {
        string report = CuiDiagnostics.Report().Report();

        Assert.Contains("CuiSharp", report, StringComparison.Ordinal);
        Assert.Contains("backend:", report, StringComparison.Ordinal);
        Assert.Contains("renderer:", report, StringComparison.Ordinal);

        // The floor is a Linux promise; printing it on another OS would be a lie.
        if (OperatingSystem.IsLinux()) Assert.Contains(CuiNative.LinuxFloor, report, StringComparison.Ordinal);
        else Assert.DoesNotContain("glibc floor", report, StringComparison.Ordinal);
    }

    [Fact]
    public void AGoldenImageAcceptsItsOwnFrameAndRejectsADifferentOne()
    {
        var page = new Counter();
        using var app = TestApp.Start(page);

        string path = Path.Combine(Path.GetTempPath(), $"cui-golden-{Guid.NewGuid():N}.png");
        string? written = null;
        try
        {
            // Nothing to compare against yet: the failure says how to make the file.
            Assert.Contains("CUI_UPDATE_GOLDENS", GoldenPng.Compare(app, path), StringComparison.Ordinal);

            written = app.SavePng();
            File.Copy(written, path);
            Assert.Null(GoldenPng.Compare(app, path));

            // The same frame is accepted, so the comparison has to reject a changed one…
            app.FindButton("Add").Click();
            Assert.NotNull(GoldenPng.Compare(app, path, tolerance: 0, channelTolerance: 0));

            // …and a 100 % tolerance accepts anything, which proves pixels are what is being compared.
            Assert.Null(GoldenPng.Compare(app, path, tolerance: 1.0, channelTolerance: 0));
        }
        finally
        {
            File.Delete(path);
            if (written is not null) File.Delete(written);
        }
    }

    [Fact]
    public void APickerUsesTheProviderItWasGiven()
    {
        var provider = new StubPicker("/tmp/chosen.csv");

        // A window with no native handle is enough: the provider is what answers.
        var window = new CuiWindow(0) { FileDialog = provider };
        FilePickResult result = window.PickFile(new FileDialogOpts { Kind = FileDialogKind.Open });

        Assert.True(result.Picked);
        Assert.Equal("/tmp/chosen.csv", result.Path);
        Assert.Equal(FileDialogKind.Open, provider.LastOptions?.Kind);
        Assert.Same(window, provider.LastWindow);
    }

    [Fact]
    public void WithNoProviderAndNoWindowThePickerSaysWhyItFailed()
    {
        // The real provider, asked before there is a window to hang a modal dialog on: a failure an
        // app can print, not an empty string that looks like a cancel.
        FilePickResult result = new CuiWindow(0).PickFile(new FileDialogOpts { Kind = FileDialogKind.Folder });

        Assert.False(result.Picked);
        Assert.True(result.Cancelled);
        Assert.NotNull(result.Error);
        Assert.Contains("no window", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void APickerResultSaysWhichOfTheThreeThingsHappened()
    {
        Assert.True(FilePickResult.Chosen("a.csv").Picked);
        Assert.Equal("a.csv", FilePickResult.Chosen("a.csv").ToString());

        Assert.True(FilePickResult.CancelledResult.Cancelled);
        Assert.Null(FilePickResult.CancelledResult.Error);
        Assert.Equal("cancelled", FilePickResult.CancelledResult.ToString());

        FilePickResult failed = FilePickResult.Failed("no window yet");
        Assert.Equal("no window yet", failed.Error);
        Assert.Contains("failed:", failed.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A page with one button, so a frame can change between comparisons.</summary>
    private sealed class Counter : Component
    {
        private int _count;

        protected override void Draw(Ui ui)
        {
            using (ui.Column(new Box { Pad = 12, Gap = 8 }))
            {
                ui.Heading("counter", 2);
                if (ui.Button("Add")) _count++;
                ui.Label($"count {_count}");
            }
        }
    }

    /// <summary>A picker that answers without a dialog.</summary>
    private sealed class StubPicker(string path) : IFileDialogProvider
    {
        internal CuiWindow? LastWindow { get; private set; }

        internal FileDialogOpts? LastOptions { get; private set; }

        public FilePickResult Pick(CuiWindow window, FileDialogOpts options)
        {
            LastWindow = window;
            LastOptions = options;
            return FilePickResult.Chosen(path);
        }
    }
}
