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
        Assert.Contains(CuiNative.LinuxFloor, Read("skills", "cui-dotnet", "native-abi.md"), StringComparison.Ordinal);
        Assert.Contains($"CUI_GLIBC_BASELINE:-{CuiNative.LinuxFloor}", Read("native", "build-native.sh"), StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepositoryRoot, .. parts]));

    private static string RepositoryRoot => TestPaths.RepositoryRoot;
}

/// <summary>
/// The skill folder is the whole of what an app needs, and the package ships it at the same path. It
/// is self-contained on purpose: a reader who lands in it never has to leave to answer a question,
/// and nothing in it may point at a file it does not carry.
/// </summary>
public class PackagedDocsTests
{
    private static readonly string[] Expected =
    [
        "SKILL.md", "llms.txt", "hosting.md", "native-abi.md", "widgets.md", "widgets-more.md",
        "api/Options.md", "api/Records.md",
    ];

    [Fact]
    public void TheFolderHoldsEverythingAReaderNeeds()
    {
        string bundle = Bundle;

        var missing = Expected.Where(file => !File.Exists(Path.Combine(bundle, file))).ToArray();
        Assert.True(missing.Length == 0, "the folder is missing: " + string.Join(", ", missing));

        // SKILL.md is the entry; a second README would compete with it.
        Assert.False(File.Exists(Path.Combine(bundle, "README.md")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(bundle, "api"), "*.md"));
    }

    [Fact]
    public void NothingInTheFolderPointsOutsideIt()
    {
        string bundle = Bundle;
        var escapes = new List<string>();

        foreach (string page in Directory.EnumerateFiles(bundle, "*.*", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(page);
            foreach (Match link in Regex.Matches(text, @"\]\(([^)#]+\.(?:md|txt|png|jpg|svg))\)"))
            {
                string target = link.Groups[1].Value;
                if (target.Contains("://", StringComparison.Ordinal)) continue;

                string resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(page)!, target));
                if (!resolved.StartsWith(bundle, StringComparison.Ordinal) || !File.Exists(resolved))
                    escapes.Add($"{Path.GetRelativePath(bundle, page)} -> {target}");
            }
        }

        Assert.True(escapes.Count == 0, "links that leave the folder or dangle: " + string.Join(", ", escapes));
    }

    [Fact]
    public void TheWidgetPagesAreCuisOwnWithThePicturesTakenOut()
    {
        foreach ((string source, string copy) in ((string Source, string Copy)[])
        [
            ("native/CUI/docs/components.md", "widgets.md"),
            ("native/CUI/docs/components-more.md", "widgets-more.md"),
        ])
        {
            string original = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, source));
            string bundled = File.ReadAllText(Path.Combine(Bundle, copy));

            Assert.StartsWith("<!-- Copied from " + source, bundled, StringComparison.Ordinal);
            string body = bundled[(bundled.IndexOf("-->", StringComparison.Ordinal) + 3)..].TrimStart();

            // The only difference is the one this folder cannot carry: the screenshots, and the
            // names the two pages use for each other.
            string expected = Regex.Replace(original, "<picture>.*?</picture>\\s*", "", RegexOptions.Singleline);
            expected = Regex.Replace(expected, "<img[^>]*>\\s*", "");
            expected = Regex.Replace(expected, @"!\[[^\]]*\]\(images/[^)]*\)\s*", "");
            expected = expected.Replace("](components-more.md)", "](widgets-more.md)").Replace("](components.md)", "](widgets.md)");

            Assert.Equal(expected.ReplaceLineEndings("\n").TrimEnd(), body.ReplaceLineEndings("\n").TrimEnd());
            Assert.DoesNotContain("images/", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ThePackageShipsTheFolderAndNothingElseIsNeeded()
    {
        string csproj = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "src", "CuiSharp", "CuiSharp.csproj"));

        Assert.Contains("skills/cui-dotnet/", csproj, StringComparison.Ordinal);

        // The pages a reader needs travel inside the folder; nothing else has to be packed for it.
        Assert.DoesNotContain(@"..\docs\HOSTING.md", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain(@"..\docs\NATIVE-ABI.md", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWidgetPagesSayWhatTheWidgetsDo()
    {
        Assert.Contains("Up and Down move and the selection follows",
            File.ReadAllText(Path.Combine(Bundle, "widgets-more.md")), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("keyboard",
            File.ReadAllText(Path.Combine(Bundle, "widgets.md")), StringComparison.OrdinalIgnoreCase);
    }

    private static string Bundle => Path.Combine(TestPaths.RepositoryRoot, "skills", "cui-dotnet");
}
