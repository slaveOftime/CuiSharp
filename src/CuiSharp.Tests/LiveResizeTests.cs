using System.Text.RegularExpressions;
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
/// The skill folder is the entry point a person or an assistant is pointed at, and the package ships
/// it at the same path. It holds only pages that have no other home, so nothing is maintained twice —
/// which means every link out of it has to resolve, in the repository and in the package.
/// </summary>
public class PackagedDocsTests
{
    [Fact]
    public void TheBundleHoldsOnlyItsOwnPages()
    {
        string bundle = Path.Combine(TestPaths.RepositoryRoot, "skills", "cui-dotnet");

        // SKILL.md is the entry; a second README would be the first sign of a copy creeping back in.
        Assert.True(File.Exists(Path.Combine(bundle, "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(bundle, "llms.txt")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(bundle, "api"), "*.md"));

        string[] stray = [.. Directory.EnumerateFiles(bundle, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(bundle, path))
            .Where(relative => !relative.EndsWith(".md", StringComparison.Ordinal) && !relative.EndsWith("llms.txt", StringComparison.Ordinal))
            .Where(relative => relative != "SKILL.md" && !relative.StartsWith("api" + Path.DirectorySeparatorChar, StringComparison.Ordinal))];

        Assert.True(stray.Length == 0, "the bundle holds pages of its own only: " + string.Join(", ", stray));
    }

    [Fact]
    public void EveryLinkInTheReferenceResolves()
    {
        string bundle = Path.Combine(TestPaths.RepositoryRoot, "skills", "cui-dotnet");
        string reference = File.ReadAllText(Path.Combine(bundle, "llms.txt"));

        var links = Regex.Matches(reference, @"\]\(([^)#]+\.[A-Za-z]+)\)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToArray();

        Assert.NotEmpty(links);
        var broken = links
            .Where(link => !link.Contains("://", StringComparison.Ordinal))
            .Where(link => !File.Exists(Path.GetFullPath(Path.Combine(bundle, link))))
            .ToArray();

        // Only pages travel with the reference; a link to a source file would dangle in the package.
        Assert.True(broken.Length == 0, "llms.txt links to files that are not there: " + string.Join(", ", broken));
    }

    [Fact]
    public void ThePackageShipsTheReferenceAndEverythingItLinksTo()
    {
        string csproj = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "src", "CuiSharp", "CuiSharp.csproj"));

        Assert.Contains("skills/cui-dotnet", csproj, StringComparison.Ordinal);

        // Each document the reference links to is packed from where it is written: packed, not copied.
        foreach (string packed in (string[])["HOSTING.md", "NATIVE-ABI.md", "components.md", "components-more.md"])
        {
            Assert.Contains(packed, csproj, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheWidgetPagesSayWhatTheWidgetsDo()
    {
        // CUI's own pages, shipped by reference rather than paraphrased here.
        string more = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "native", "CUI", "docs", "components-more.md"));
        string first = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "native", "CUI", "docs", "components.md"));

        Assert.Contains("Up and Down move and the selection follows", more, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("keyboard", first, StringComparison.OrdinalIgnoreCase);
    }
}
