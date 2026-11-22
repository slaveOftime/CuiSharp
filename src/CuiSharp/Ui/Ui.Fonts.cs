using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Text;
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
        /// <remarks>Returns -1 when the file cannot be loaded. Do not pass a failed handle to <see cref="SetFont"/>.</remarks>
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
        /// Noto CJK, …). The first call for a sample probes files and loads a face; subsequent
        /// calls on the same UI context reuse the result (including 0), without IO or allocation.
        /// Call it directly in Draw before text that needs it. The probe checks visible Unicode
        /// scalars, not shaping or complete language coverage.
        /// </para>
        /// </remarks>
        /// <returns>
        /// The installed or reused face, or 0 when the bundled faces already draw the sample,
        /// the sample has no visible scalars, or no candidate face draws it. The result is cached
        /// for this context's lifetime, so newly installed OS fonts need a new context.
        /// </returns>
        public Font UseSystemFontFallback(string sample)
        {
            ArgumentNullException.ThrowIfNull(sample);
            return ui.SystemFontFallbacks.Ensure(ui, sample);
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
        /// arrives in a script the app was not built for. Call this directly in Draw before text.
        /// </para>
        /// <para>
        /// Asking is pixels — an isolated context, a font load and a render per candidate — so the
        /// search runs on the pool rather than in a frame. The first call starts it and installs
        /// nothing; the frames that follow draw the bundled faces, so a window opens immediately
        /// instead of waiting on the search; and the first frame that finds the answer loads the
        /// files it chose and returns the count. From then on the call is a cached read — no IO, no
        /// font loading, no managed allocation — so there is no initialization flag to keep.
        /// Searches that find no usable face are cached too.
        /// </para>
        /// <para>
        /// A sample the bundled faces already paint is left alone, and so is a sample no face on
        /// this machine paints. The fallback chain holds four faces, so any further face is reached
        /// by CUI's own scan of every loaded face; that is slower, not invisible.
        /// </para>
        /// </remarks>
        /// <returns>
        /// The number of system faces these helpers have installed on this context: 0 on the frames
        /// before the search lands, then the final count, unchanged on later calls. 0 is also the
        /// answer for a machine with no suitable additional face. This is not a coverage guarantee.
        /// </returns>
        public int UseSystemFontFallbacks() => ui.SystemFontFallbacks.EnsureAll(ui);
    }
}

/// <summary>
/// Where each OS keeps the faces that cover the scripts its own UI is written in and the ones a
/// file name, a folder or a tag can arrive in. Ordered by how likely the file is to be there, not
/// by preference: the first one that paints every visible scalar wins.
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
/// Checks visible scalars in isolation. A face without the glyph still reports its <c>.notdef</c>
/// advance, so measuring text cannot answer this; painting it can. This is not a shaping test.
/// </summary>
internal static unsafe class FontProbe
{
    /// <summary>Checks the bundled faces, not a caller's remapped roles.</summary>
    internal static bool PaintsThemeFace(string sample) => Paints(path: null, sample);

    internal static bool HasVisibleScalars(string sample)
    {
        foreach (Rune rune in sample.EnumerateRunes())
            if (IsVisible(rune)) return true;
        return false;
    }

    private static bool IsVisible(Rune rune) => !Rune.IsWhiteSpace(rune)
        && Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Control or UnicodeCategory.Format);

    /// <summary>True only when every visible scalar paints, without borrowing another face's glyphs.</summary>
    internal static bool Paints(string? path, string sample)
    {
        if (string.IsNullOrEmpty(sample)) return false;
        return PaintsMask(path, new ReadOnlySpan<string>(in sample), 1u) != 0;
    }

    /// <summary>
    /// Which of <paramref name="samples"/> (indexed by the set bits of <paramref name="pending"/>)
    /// have every visible scalar painted by <paramref name="path"/>, or by the bundled faces when
    /// <paramref name="path"/> is null — all in one isolated context so a multi-megabyte font
    /// collection is loaded once instead of once per script.
    /// </summary>
    internal static uint PaintsMask(string? path, ReadOnlySpan<string> samples, uint pending)
    {
        if (pending == 0) return 0;
        if (path is not null && !File.Exists(path)) return 0;

        uint candidates = 0;
        for (uint bits = pending; bits != 0; bits &= bits - 1)
        {
            int index = BitOperations.TrailingZeroCount(bits);
            if (!string.IsNullOrEmpty(samples[index]) && HasVisibleScalars(samples[index]))
                candidates |= 1u << index;
        }

        if (candidates == 0) return 0;

        // Do not use CuiSurface.BeginFrame here: this helper runs inside Draw, and a nested managed
        // frame would reset the caller's arena, free its callbacks, drain work and skew FrameStats.
        // A file probe also must not borrow glyphs from the bundled faces via CUI's global scan.
        using var scope = Utf8.Scope();
        nint sw = Native.SwCreate();
        if (sw == 0) throw new InvalidOperationException("cui_sw_create failed.");
        nint ctx = 0;
        MemoryMappedViewAccessor? backing = null;
        try
        {
            var desc = new NativeCtxDesc { Renderer = Native.SwIface(sw), NoDefaultFonts = (byte)(path is null ? 0 : 1) };
            ctx = Native.Create(in desc);
            if (ctx == 0) throw new InvalidOperationException("cui_create failed.");
            Native.SetViewport(ctx, 128, 96, 1);

            if (path is not null)
            {
                Font face = SystemFontFile.Load(ctx, path, out backing);
                if ((int)face <= (int)Font.Icons) return 0;
                Native.FontSetRole(ctx, Font.Body, face);
            }

            byte[] blank = new byte[128 * 96 * 4];
            byte[] painted = new byte[blank.Length];
            Render(ctx, sw, null, blank);

            uint matched = 0;
            for (uint bits = candidates; bits != 0; bits &= bits - 1)
            {
                int index = BitOperations.TrailingZeroCount(bits);
                if (PaintsSample(ctx, sw, samples[index], blank, painted))
                    matched |= 1u << index;
            }

            return matched;
        }
        finally
        {
            if (ctx != 0) Native.Destroy(ctx);
            backing?.Dispose();
            Native.SwDestroy(sw);
        }
    }

    private static bool PaintsSample(nint ctx, nint sw, string sample, byte[] blank, byte[] painted)
    {
        foreach (Rune rune in sample.EnumerateRunes())
        {
            if (!IsVisible(rune)) continue;
            Render(ctx, sw, rune.ToString(), painted);
            if (painted.AsSpan().SequenceEqual(blank)) return false;
        }

        return true;
    }

    private static void Render(nint ctx, nint sw, string? text, byte[] pixels)
    {
        using var scope = Utf8.Scope();
        Native.BeginFrame(ctx, 1f / 60f);
        Native.Label(ctx, Utf8.Get(text ?? " "));
        Native.EndFrame(ctx);

        var drawData = (NativeDrawData*)Native.GetDrawData(ctx);
        fixed (byte* p = pixels)
            Native.SwRender(sw, in *drawData, (nint)p, 128, 96, 128 * 4, SwFormat.Rgba);
    }
}
