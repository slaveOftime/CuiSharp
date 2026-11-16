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

                Font face = Install(ui, path);
                if (face != 0) return face;
            }

            return 0;
        }

        /// <summary>
        /// Installs the faces this machine has for the scripts a file name, a folder or a tag can be
        /// written in — CJK, Cyrillic, Greek, Arabic, Hebrew, Thai, Devanagari and emoji — so text
        /// from another OS or another locale draws instead of disappearing.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The bundled faces are Latin. A glyph no loaded face has draws nothing at all, with no box
        /// and no warning, and a folder, a download or a music tag is exactly the kind of text that
        /// arrives in a script the app was not built for. Call this once at startup; loading a face
        /// per frame would churn the glyph atlas.
        /// </para>
        /// <para>
        /// A script the theme's own face already paints is left alone, and so is a sample no face on
        /// this machine paints. The fallback chain holds four faces, so any further face is reached
        /// by CUI's own scan of every loaded face; that is slower, not invisible.
        /// </para>
        /// </remarks>
        /// <returns>
        /// How many faces it loaded, or 0 when the bundled faces cover everything the machine can
        /// draw. Call it for the side effect, not for the number.
        /// </returns>
        public int UseSystemFontFallbacks()
        {
            string[] probes = [.. SystemFonts.Probes];
            var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var installed = 0;

            foreach (string sample in SystemFonts.Samples)
            {
                if (FontProbe.PaintsThemeFace(sample)) continue;

                // A face loaded for an earlier script may already paint this one: not every OS
                // ships a separate file per script, and Arial Unicode is one file for many.
                bool covered = false;
                foreach (string path in loaded)
                {
                    if (!FontProbe.Paints(path, sample)) continue;

                    covered = true;
                    break;
                }

                if (covered) continue;

                foreach (string path in probes)
                {
                    if (loaded.Contains(path)) continue;
                    if (!File.Exists(path) || !FontProbe.Paints(path, sample)) continue;

                    Font face = Install(ui, path);
                    if (face == 0) continue;

                    loaded.Add(path);
                    installed++;
                    break;
                }
            }

            return installed;
        }

        /// <summary>Loads a face and makes it a fallback of every text role; 0 when the file did not load.</summary>
        private static Font Install(Ui surface, string path)
        {
            Font face = surface.LoadFont(path);

            // User fonts start at CUI__USER_FONT_BASE (16); 0..4 are the built-in roles, and -1 is
            // a failed load. None of them can serve as a fallback.
            if ((int)face <= (int)Font.Icons) return 0;

            foreach (Font role in SystemFonts.Roles) surface.AddFontFallback(role, face);
            return face;
        }
    }
}

/// <summary>
/// Where each OS keeps the faces that cover the scripts its own UI is written in and the ones a
/// file name, a folder or a tag can arrive in. Ordered by how likely the file is to be there, not
/// by preference: the first one that measures wins.
/// </summary>
internal static class SystemFonts
{
    /// <summary>The roles a fallback is worth registering for: everything but the icon face.</summary>
    internal static readonly Font[] Roles = [Font.Body, Font.Medium, Font.Display, Font.Mono];

    /// <summary>
    /// One string per script a file name, a folder or a tag is likely to be written in. Latin is
    /// left out: the bundled faces cover it, so the probe has nothing to load for it.
    /// </summary>
    internal static readonly string[] Samples =
    [
        "中文",       // Chinese
        "日本語",     // Japanese
        "한국어",     // Korean
        "Русский",    // Cyrillic
        "Ελληνικά",   // Greek
        "العربية",    // Arabic
        "עברית",      // Hebrew
        "ไทย",        // Thai
        "हिन्दी",      // Devanagari
        "😀",         // emoji
    ];

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
                "Nirmala.ttc", "Nirmala.ttf",                             // Devanagari
                "LeelawUI.ttf", "leelawad.ttf",                           // Thai
                "segoeui.ttf", "tahoma.ttf",                              // Arabic, Hebrew, Thai, Greek, Cyrillic
                "seguiemj.ttf",                                           // emoji, with outlines behind its colour
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
                "/System/Library/Fonts/PingFang.ttc",                     // Chinese
                "/System/Library/Fonts/Hiragino Sans GB.ttc",
                "/System/Library/Fonts/ヒラギノ角ゴシック W3.ttc",          // Japanese
                "/System/Library/Fonts/AppleSDGothicNeo.ttc",             // Korean
                "/System/Library/Fonts/Apple Color Emoji.ttc",            // emoji
                "/System/Library/Fonts/Supplemental/GeezaPro.ttc",        // Arabic
                "/System/Library/Fonts/Supplemental/Arial Hebrew.ttf",    // Hebrew
                "/System/Library/Fonts/Supplemental/Thonburi.ttc",        // Thai
                "/System/Library/Fonts/Supplemental/Devanagari Sangam MN.ttc",
                "/System/Library/Fonts/Supplemental/Arial Unicode.ttf",   // one file that covers many
                "/Library/Fonts/Arial Unicode.ttf",                       // older macOS location
                "/System/Library/Fonts/Supplemental/Arial.ttf",           // a last resort
            ])
            {
                yield return path;
            }

            yield break;
        }

        // Linux has no fixed answer, so the common distributions' package layouts are probed.
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (string path in (string[])
        [
            "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",     // Debian, Ubuntu
            "/usr/share/fonts/truetype/noto/NotoSansCJK-Regular.ttc",
            "/usr/share/fonts/noto/NotoSansCJK-Regular.ttc",              // Arch
            "/usr/share/fonts/noto-cjk/NotoSansCJK-Regular.ttc",
            "/usr/share/fonts/google-noto-cjk/NotoSansCJK-Regular.ttc",   // Fedora
            "/usr/local/share/fonts/NotoSansCJK-Regular.ttc",
            home + "/.local/share/fonts/NotoSansCJK-Regular.ttc",
            "/usr/share/fonts/truetype/wqy/wqy-zenhei.ttc",
            "/usr/share/fonts/wqy-zenhei/wqy-zenhei.ttc",
            "/usr/share/fonts/truetype/wqy/wqy-microhei.ttc",
            "/usr/share/fonts/truetype/droid/DroidSansFallbackFull.ttf",
            "/usr/share/fonts/truetype/noto/NotoSansArabic-Regular.ttf",
            "/usr/share/fonts/google-noto/NotoSansArabic-Regular.ttf",
            "/usr/share/fonts/truetype/noto/NotoSansHebrew-Regular.ttf",
            "/usr/share/fonts/google-noto/NotoSansHebrew-Regular.ttf",
            "/usr/share/fonts/truetype/noto/NotoSansThai-Regular.ttf",
            "/usr/share/fonts/google-noto/NotoSansThai-Regular.ttf",
            "/usr/share/fonts/truetype/noto/NotoSansDevanagari-Regular.ttf",
            "/usr/share/fonts/google-noto/NotoSansDevanagari-Regular.ttf",
            "/usr/share/fonts/truetype/noto/NotoEmoji-Regular.ttf",       // monochrome, so text can use it
            "/usr/share/fonts/noto/NotoEmoji-Regular.ttf",
            "/usr/share/fonts/truetype/noto/NotoColorEmoji.ttf",          // colour, for the emoji picker
            "/usr/share/fonts/noto/NotoColorEmoji.ttf",
            "/usr/share/fonts/google-noto-emoji/NotoColorEmoji.ttf",
            "/usr/share/fonts/noto-emoji/NotoColorEmoji.ttf",
            "/usr/local/share/fonts/NotoColorEmoji.ttf",
            home + "/.local/share/fonts/NotoColorEmoji.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",            // last resort for many scripts
            "/usr/share/fonts/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/TTF/DejaVuSans.ttf",
        ])
        {
            yield return path;
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
            if ((int)face <= (int)Font.Icons) return [];
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
