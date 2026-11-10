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
        string targets = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "CuiSharp", "build", "CuiSharp.targets"));

        foreach (string rid in CuiNative.RidList)
        {
            Assert.Contains(rid, targets, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheDocumentedGlibcFloorIsTheOneTheErrorNames()
    {
        string abi = File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "NATIVE-ABI.md"));
        string script = File.ReadAllText(Path.Combine(RepositoryRoot, "native", "build-native.sh"));

        Assert.Contains(CuiNative.LinuxFloor, abi, StringComparison.Ordinal);
        Assert.Contains($"CUI_GLIBC_BASELINE:-{CuiNative.LinuxFloor}", script, StringComparison.Ordinal);
    }

    private static string RepositoryRoot
    {
        get
        {
            string? directory = AppContext.BaseDirectory;
            while (directory is not null && !File.Exists(Path.Combine(directory, "CuiSharp.slnx")))
                directory = Path.GetDirectoryName(directory);
            return directory ?? throw new InvalidOperationException("the repository root was not found");
        }
    }
}

/// <summary>
/// A package has to explain itself: the guide, the reference, the ABI promise and the widget
/// behaviour pages are what an agent or a person reads instead of cloning the repository, and the
/// report from a real app was that none of it was in the package.
/// </summary>
public class PackagedDocsTests
{
    [Fact]
    public void ThePackageCarriesTheGuides()
    {
        string csproj = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "CuiSharp", "CuiSharp.csproj"));

        foreach (string file in (string[])
        [
            "llms.txt", "SKILL.md", "NATIVE-ABI.md", "components.md", "components-more.md",
        ])
        {
            Assert.Contains(file, csproj, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheWidgetPagesSayWhatTheWidgetsDo()
    {
        // The pages are upstream's; this asserts they are the ones with behaviour in them rather
        // than a table of links that would ship an empty promise.
        string widgets = File.ReadAllText(Path.Combine(RepositoryRoot, "native", "CUI", "docs", "components-more.md"));
        string widgets2 = File.ReadAllText(Path.Combine(RepositoryRoot, "native", "CUI", "docs", "components.md"));

        Assert.Contains("Up and Down move and the selection follows", widgets, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("keyboard", widgets2, StringComparison.OrdinalIgnoreCase);
    }

    private static string RepositoryRoot
    {
        get
        {
            string? directory = AppContext.BaseDirectory;
            while (directory is not null && !File.Exists(Path.Combine(directory, "CuiSharp.slnx")))
                directory = Path.GetDirectoryName(directory);
            return directory ?? throw new InvalidOperationException("the repository root was not found");
        }
    }
}
