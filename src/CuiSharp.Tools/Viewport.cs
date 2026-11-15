using System.Globalization;

namespace CuiSharp.Tools;

/// <summary>
/// A headless viewport: the <c>WxH</c> value behind <c>--size</c>. Kept beside the parser so a
/// malformed size is one message, not a string compared in three places.
/// </summary>
internal readonly record struct Viewport(int Width, int Height)
{
    internal const int MaxDimension = 16_384;

    internal static bool TryParse(string? text, out Viewport viewport)
    {
        viewport = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string[] parts = text.Trim().Split(['x', 'X']);
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) return false;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int width)) return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int height)) return false;
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension) return false;

        viewport = new Viewport(width, height);
        return true;
    }

    internal static bool TryParseList(string? text, out Viewport[] viewports)
    {
        viewports = [];
        if (string.IsNullOrWhiteSpace(text)) return false;

        string[] pieces = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pieces.Length == 0) return false;

        var parsed = new Viewport[pieces.Length];
        for (int i = 0; i < pieces.Length; i++)
        {
            if (!TryParse(pieces[i], out parsed[i])) return false;
        }

        viewports = parsed;
        return true;
    }

    public override string ToString() => $"{Width}x{Height}";
}

/// <summary>A <c>KEY=VALUE</c> pair for <c>--env</c>, validated before a child process sees it.</summary>
internal static class EnvironmentText
{
    internal static bool TryParse(string? text, out string key, out string value)
    {
        key = "";
        value = "";
        if (string.IsNullOrEmpty(text)) return false;

        int equals = text.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0) return false;

        key = text[..equals];
        value = text[(equals + 1)..];
        if (key.Any(static ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '_'))) return false;
        if (key.Contains('\0') || value.Contains('\0')) return false;
        return true;
    }
}
