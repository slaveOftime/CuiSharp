using CuiSharp.Hosting;
using CuiSharp.Interop;

namespace CuiSharp.Tests;

/// <summary>
/// A window resize has to keep up with the mouse. The hook that makes it is Windows-only and needs
/// a real window, so the rule it applies is pinned here instead: which messages draw, which are
/// swallowed, and which are left alone.
/// </summary>
public class LiveResizeTests
{
    [Fact]
    public void ADragStartIsSwallowedSoSizeMessagesDoNotDraw()
    {
        // Forwarding it would let CUI's own WM_SIZE handler draw, per mouse sample, and that is the
        // duplicate frame the hook exists to remove.
        Assert.Equal(ResizeStep.Suppress, LiveResize.Decide(Win32.WmEnterSizeMove, inSizeMove: false, sizeChanged: false));
        Assert.Equal(ResizeStep.Suppress, LiveResize.Decide(Win32.WmExitSizeMove, inSizeMove: true, sizeChanged: true));

        // The size still has to reach CUI: it is what the next frame draws at.
        Assert.Equal(ResizeStep.Forward, LiveResize.Decide(Win32.WmSize, inSizeMove: true, sizeChanged: false));
    }

    [Fact]
    public void TheCoalescedPaintAfterASizeChangeDrawsOnce()
    {
        Assert.Equal(ResizeStep.RenderOnce, LiveResize.Decide(Win32.WmPaint, inSizeMove: true, sizeChanged: true));
    }

    [Fact]
    public void APaintThatChangedNoSizeDrawsNothingNew()
    {
        // Dragging the title bar moves the window; there is no new layout to draw.
        Assert.Equal(ResizeStep.Forward, LiveResize.Decide(Win32.WmPaint, inSizeMove: true, sizeChanged: false));

        // And outside a drag the paint is ordinary: the frame loop is already drawing.
        Assert.Equal(ResizeStep.Forward, LiveResize.Decide(Win32.WmPaint, inSizeMove: false, sizeChanged: false));
    }

    [Fact]
    public void EverythingElseReachesTheWindowProcedure()
    {
        foreach (uint message in (uint[])[0x0100, 0x0200, 0x0014, Win32.WmNcDestroy])
        {
            Assert.Equal(ResizeStep.Forward, LiveResize.Decide(message, inSizeMove: true, sizeChanged: true));
        }
    }
}

/// <summary>
/// The managed side names the RIDs and the glibc floor in its own error messages. If the build or
/// the docs disagree, a user is told the wrong thing at the worst moment, so all three are checked
/// against each other here.
/// </summary>
public class NativeAbiClaimTests
{
    [Fact]
    public void TheTargetsShipTheRidsTheErrorNames()
    {
        string targets = Read("src", "CuiSharp", "build", "CuiSharp.targets");

        foreach (string rid in CuiNative.RidList) Assert.Contains(rid, targets, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDocumentedGlibcFloorIsTheOneTheErrorNames()
    {
        Assert.Contains(CuiNative.LinuxFloor, Read("docs", "NATIVE-ABI.md"), StringComparison.Ordinal);
        Assert.Contains($"CUI_GLIBC_BASELINE:-{CuiNative.LinuxFloor}", Read("native", "build-native.sh"), StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepositoryRoot, .. parts]));

    private static string RepositoryRoot => TestPaths.RepositoryRoot;
}

/// <summary>
/// The bundle is the one folder a consumer reads, and the one thing the package ships. Neither is
/// worth much if the package forgets it or the copies drift from the pages they were made from.
/// </summary>
public class PackagedDocsTests
{
    [Fact]
    public void ThePackageShipsTheBundleAtThePathTheRepositoryUses()
    {
        string csproj = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "src", "CuiSharp", "CuiSharp.csproj"));

        // Same path in both, so a path in a document works wherever it is read.
        Assert.Contains("skills/cui-dotnet/", csproj, StringComparison.Ordinal);
        Assert.Contains("llms.txt", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryBundledCopyMatchesItsSource()
    {
        (string Source, string Copy)[] staged =
        [
            ("native/CUI/docs/components.md", "widgets.md"),
            ("native/CUI/docs/components-more.md", "widgets-more.md"),
            ("docs/NATIVE-ABI.md", "native-abi.md"),
            ("docs/HOSTING.md", "hosting.md"),
        ];

        foreach ((string source, string copy) in staged)
        {
            string original = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, source));
            string bundled = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "skills", "cui-dotnet", copy));

            Assert.StartsWith("<!-- Copied from " + source, bundled, StringComparison.Ordinal);
            string body = bundled[(bundled.IndexOf("-->", StringComparison.Ordinal) + 3)..].TrimStart();
            Assert.Equal(original.ReplaceLineEndings("\n").TrimEnd(), body.ReplaceLineEndings("\n").TrimEnd());
        }
    }

    [Fact]
    public void TheBundleHoldsEverythingAReaderNeeds()
    {
        string bundle = Path.Combine(TestPaths.RepositoryRoot, "skills", "cui-dotnet");

        foreach (string file in (string[])
        [
            "SKILL.md", "README.md", "llms.txt", "widgets.md", "widgets-more.md", "hosting.md", "native-abi.md",
        ])
        {
            Assert.True(File.Exists(Path.Combine(bundle, file)), $"the bundle is missing {file}");
        }

        Assert.NotEmpty(Directory.GetFiles(Path.Combine(bundle, "api"), "*.md"));
    }

    [Fact]
    public void TheWidgetPagesSayWhatTheWidgetsDo()
    {
        // CUI's own pages, not a table of links that would ship an empty promise.
        string more = Read("skills", "cui-dotnet", "widgets-more.md");
        string first = Read("skills", "cui-dotnet", "widgets.md");

        Assert.Contains("Up and Down move and the selection follows", more, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("keyboard", first, StringComparison.OrdinalIgnoreCase);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([TestPaths.RepositoryRoot, .. parts]));
}
