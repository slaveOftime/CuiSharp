using System.IO;
using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>
/// Loading fonts and pointing a font role at one: the theme's roles are only defaults,
/// and an app that ships its own typeface re-maps them once at startup.
/// </summary>
public static unsafe class FontUiExtensions
{
    extension(Ui ui)
    {
        /// <summary>Loads a font file (TTF/OTF) and returns a handle for <see cref="SetFont"/>.</summary>
        /// <remarks>Returns 0 when the file cannot be read, which leaves the theme's face in place.</remarks>
        public Font LoadFont(UiText path) => Native.FontLoadFile(ui.Handle, Utf8.Get(path));

        /// <summary>
        /// Loads a font from memory and returns a handle for <see cref="SetFont"/>.
        /// </summary>
        /// <param name="data">The whole font file.</param>
        /// <param name="copy">
        /// True keeps CUI's own copy (the array may be freed at once); false keeps only a
        /// pointer, so the memory has to outlive the app.
        /// </param>
        public Font LoadFont(ReadOnlySpan<byte> data, bool copy = true) =>
            Native.FontLoadMemory(ui.Handle, UiMarshal.Values<byte>(data), (nuint)data.Length, (byte)(copy ? 1 : 0));

        /// <summary>Makes <paramref name="font"/> the face every widget using <paramref name="role"/> draws with.</summary>
        /// <example><code>
        /// var mono = ui.LoadFont("assets/JetBrainsMono.ttf");
        /// ui.SetFont(Font.Mono, mono);
        /// </code></example>
        public void SetFont(Font role, Font font) => Native.FontSetRole(ui.Handle, role, font);

        /// <summary>
        /// Adds a fallback CUI reaches for when <paramref name="font"/> has no glyph.
        /// <para>
        /// The chain holds at most four fallbacks; when it is exhausted CUI scans every other
        /// loaded face before giving up. A glyph no face has draws nothing at all — not even a
        /// <c>.notdef</c> box in every font — so an untranslated screen comes up blank with no
        /// warning. <see cref="UseSystemFontFallback"/> is the check for that, and the bundled
        /// faces are Latin.
        /// </para>
        /// <para>
        /// The glyph atlas is what a large script actually costs: one texture of
        /// <c>CUI_ATLAS_MAX</c> (4096) pixels per side, with the frame's glyph cache reset between
        /// frames, so a font that makes every frame load new glyphs is the expensive case. Register
        /// the faces a language needs at startup, not per frame.
        /// </para>
        /// </summary>
        public void AddFontFallback(Font font, Font fallback) =>
            Native.FontAddFallback(ui.Handle, font, fallback);

        /// <summary>
        /// Finds a face on this machine that can draw <paramref name="sample"/> and registers it as
        /// a fallback for the app's roles, so a UI in a script the bundled faces do not cover
        /// renders instead of going blank.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The bundled faces are Latin. When a script is missing, CUI draws nothing for it — no
        /// box, no warning — so this asks the only question that has an answer: does the face put
        /// pixels on the screen for this text? A font's advance cannot answer it, because a missing
        /// glyph still has its <c>.notdef</c> width.
        /// </para>
        /// <para>
        /// The probes are the faces each OS ships for its own languages (Microsoft YaHei, PingFang,
        /// Noto CJK, …). Call it once at startup, not per frame: it reads a short list of paths,
        /// paints one sample for each face that is there, and loads the first that draws.
        /// </para>
        /// </remarks>
        /// <returns>
        /// The face it installed, or 0 when there was nothing to do: the theme's own face already
        /// draws the sample, or no face on this machine does.
        /// </returns>
        public Font UseSystemFontFallback(string sample)
        {
            // Nothing to do when the theme's own face already draws it — which is the common case,
            // and the reason this is safe to call unconditionally at startup.
            if (FontProbe.PaintsThemeFace(sample)) return 0;

            foreach (string path in SystemFonts.Probes)
            {
                if (!File.Exists(path) || !FontProbe.Paints(path, sample)) continue;

                Font face = ui.LoadFont(path);
                if (face == Font.Body) continue;

                foreach (Font role in SystemFonts.Roles) ui.AddFontFallback(role, face);
                return face;
            }

            return 0;
        }
    }
}

/// <summary>
/// Where each OS keeps the faces that cover the scripts its own UI is written in. Ordered by how
/// likely the file is to be there, not by preference: the first one that measures wins.
/// </summary>
internal static class SystemFonts
{
    /// <summary>The roles a fallback is worth registering for: everything but the icon face.</summary>
    internal static readonly Font[] Roles = [Font.Body, Font.Medium, Font.Display, Font.Mono];

    /// <summary>Candidate font files, most likely first, for the OS this build runs on.</summary>
    internal static IEnumerable<string> Probes => Probes_();

    private static IEnumerable<string> Probes_()
    {
        if (OperatingSystem.IsWindows())
        {
            string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            foreach (string name in (string[])
            [
                "msyh.ttc", "msyh.ttf", "simsun.ttc", "simhei.ttf",       // Chinese
                "YuGothR.ttc", "meiryo.ttc", "msgothic.ttc",              // Japanese
                "malgun.ttf", "gulim.ttc",                                // Korean
                "seguisym.ttf", "arial.ttf",                              // symbols, and a last resort
            ])
            {
                yield return Path.Combine(fonts, name);
            }

            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            foreach (string path in (string[])
            [
                "/System/Library/Fonts/PingFang.ttc",
                "/System/Library/Fonts/Hiragino Sans GB.ttc",
                "/System/Library/Fonts/ヒラギノ角ゴシック W3.ttc",
                "/System/Library/Fonts/AppleSDGothicNeo.ttc",
                "/System/Library/Fonts/Supplemental/Arial Unicode.ttf",
            ])
            {
                yield return path;
            }

            yield break;
        }

        // Linux has no fixed answer, so the usual distributions' package layouts are probed.
        foreach (string directory in (string[])
        [
            "/usr/share/fonts",
            "/usr/local/share/fonts",
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/.local/share/fonts",
            "/usr/share/fonts/truetype",
            "/usr/share/fonts/opentype",
        ])
        {
            foreach (string name in (string[])
            [
                "noto/NotoSansCJK-Regular.ttc",
                "opentype/noto/NotoSansCJK-Regular.ttc",
                "noto-cjk/NotoSansCJK-Regular.ttc",
                "truetype/noto/NotoSansCJK-Regular.ttc",
                "wqy-zenhei/wqy-zenhei.ttc",
                "truetype/wqy/wqy-zenhei.ttc",
                "truetype/wqy/wqy-microhei.ttc",
                "droid/DroidSansFallbackFull.ttf",
                "truetype/droid/DroidSansFallbackFull.ttf",
                "dejavu/DejaVuSans.ttf",
            ])
            {
                yield return Path.Combine(directory, name);
            }
        }
    }
}

/// <summary>
/// Asks a font file whether it can actually draw something. A face without the glyph still reports
/// its <c>.notdef</c> advance, so measuring text cannot answer this; painting it can.
/// </summary>
internal static class FontProbe
{
    /// <summary>
    /// True when the theme's own face — the bundled Latin one, unless the app replaced it — puts
    /// pixels on the screen for <paramref name="sample"/>.
    /// </summary>
    internal static bool PaintsThemeFace(string sample) => Paints(path: null, sample);

    /// <summary>True when <paramref name="path"/> puts pixels on the screen for <paramref name="sample"/>.</summary>
    internal static bool Paints(string? path, string sample)
    {
        if (string.IsNullOrEmpty(sample)) return false;

        // Two throwaway surfaces, the same in every way except the string: whatever the background
        // paints is subtracted, so only the glyphs are being compared.
        byte[] with = Render(path, sample);
        byte[] without = Render(path, " ");
        if (with.Length == 0 || without.Length == 0) return false;

        for (int i = 0; i < with.Length; i++)
        {
            if (with[i] != without[i]) return true;
        }

        return false;
    }

    private static byte[] Render(string? path, string text)
    {
        using CuiSurface probe = CuiSurface.Create(128, 48);
        if (path is not null)
        {
            Font face = probe.Ui.LoadFont(path);
            if (face == Font.Body) return [];
            probe.Ui.SetFont(Font.Body, face);
        }

        probe.BeginFrame();
        probe.Ui.Label(text);
        probe.EndFrame();

        byte[] pixels = new byte[probe.PixelWidth * probe.PixelHeight * 4];
        probe.Render(pixels, PixelFormat.Rgba);
        return pixels;
    }
}
