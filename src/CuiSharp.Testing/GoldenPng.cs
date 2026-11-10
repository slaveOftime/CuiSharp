using System.Text;
using CuiSharp.Hosting;

namespace CuiSharp.Testing;

/// <summary>
/// Compares a frame against a checked-in PNG, allowing for the differences that are not the app's: a
/// driver that antialiases a hair differently, a font that renders a pixel off.
/// <para>
/// A golden image is worth having when the question is "does this still look right?" — a layout that
/// collapsed, a chart drawn upside down, a control that lost its label. It is the only check that
/// catches those, because a semantic tree has no opinion about pixels.
/// </para>
/// </summary>
/// <remarks>
/// Regenerate with <c>CUI_UPDATE_GOLDENS=1 dotnet test</c>: the image is rewritten instead of
/// compared, and the test passes, so a deliberate change is one command and the diff is the review.
/// </remarks>
public static class GoldenPng
{
    /// <summary>True when a run should rewrite the images rather than compare them.</summary>
    public static bool Updating => Environment.GetEnvironmentVariable("CUI_UPDATE_GOLDENS") is { Length: > 0 };

    /// <summary>How much of the image may differ before it counts as a change.</summary>
    public const double DefaultTolerance = 0.001;

    /// <summary>How different one channel must be before a pixel is counted at all.</summary>
    public const int DefaultChannelTolerance = 2;

    /// <summary>
    /// Compares the last frame with the PNG at <paramref name="path"/>, writing the file when
    /// <see cref="Updating"/> is set. The pointer is parked and the frame settled first, so a
    /// leftover tooltip or a running animation cannot be the difference.
    /// </summary>
    /// <returns>Null when they match, or a sentence saying how they differ.</returns>
    public static string? Compare(HeadlessApp app, string path,
        double tolerance = DefaultTolerance, int channelTolerance = DefaultChannelTolerance)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrEmpty(path);

        app.ParkPointer();

        if (Updating)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            app.SavePng(path, parked: false);
            return null;
        }

        if (!File.Exists(path)) return $"{path} does not exist. Run with CUI_UPDATE_GOLDENS=1 to create it.";
        if (!Png.TryDecode(File.ReadAllBytes(path), out byte[] expected, out int expectedWidth, out int expectedHeight))
            return $"{path} is not a PNG this can read.";

        int width = app.Width, height = app.Height;
        if (expectedWidth != width || expectedHeight != height)
            return $"{path} is {expectedWidth}x{expectedHeight}; the frame is {width}x{height}.";

        ReadOnlySpan<byte> actual = app.RawPixels;
        int differing = 0, first = -1;
        for (int i = 0; i < width * height; i++)
        {
            bool differs = false;
            for (int channel = 0; channel < 4; channel++)
            {
                if (Math.Abs(actual[(i * 4) + channel] - expected[(i * 4) + channel]) > channelTolerance) differs = true;
            }

            if (!differs) continue;
            differing++;
            if (first < 0) first = i;
        }

        double share = (double)differing / (width * height);
        if (share <= tolerance) return null;

        return new StringBuilder()
            .Append($"{path}: {differing} of {width * height} pixels differ ({share:P2}, tolerance {tolerance:P2}), ")
            .Append($"first at ({first % width},{first / width}). Run with CUI_UPDATE_GOLDENS=1 to accept the change.")
            .ToString();
    }
}
