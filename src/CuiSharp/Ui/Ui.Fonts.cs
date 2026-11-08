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
            Native.FontLoadMemory(ui.Handle, UiMarshal.Values<byte>(data), (ulong)data.Length, (byte)(copy ? 1 : 0));

        /// <summary>Makes <paramref name="font"/> the face every widget using <paramref name="role"/> draws with.</summary>
        /// <example><code>
        /// var mono = ui.LoadFont("assets/JetBrainsMono.ttf");
        /// ui.SetFont(Font.Mono, mono);
        /// </code></example>
        public void SetFont(Font role, Font font) => Native.FontSetRole(ui.Handle, role, font);

        /// <summary>Adds a fallback CUI reaches for when <paramref name="font"/> has no glyph.</summary>
        public void AddFontFallback(Font font, Font fallback) =>
            Native.FontAddFallback(ui.Handle, font, fallback);
    }
}
