namespace CuiSharp;

// One owner per native context. Never key this by a native pointer: freed pointers can be reused
// by a later window, and a cached font handle belongs only to the context that loaded it.
internal sealed class SystemFontFallbackState
{
    private readonly Dictionary<string, Font> _samples = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Font> _loaded = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private string[]? _probes;
    private bool _all;

    internal int EnsureAll(Ui ui)
    {
        if (!_all)
        {
            foreach (string sample in SystemFonts.Samples) Ensure(ui, sample);
            _all = true;
        }

        return _loaded.Count;
    }

    internal Font Ensure(Ui ui, string sample)
    {
        if (_samples.TryGetValue(sample, out Font cached)) return cached;

        Font face = Find(ui, sample);
        _samples.Add(sample, face);
        return face;
    }

    private Font Find(Ui ui, string sample)
    {
        if (!FontProbe.HasVisibleScalars(sample) || FontProbe.PaintsThemeFace(sample)) return 0;

        // Reuse files installed by either helper, even when two different samples need them.
        foreach (var (path, face) in _loaded)
            if (FontProbe.Paints(path, sample)) return face;

        foreach (string path in _probes ??= [.. SystemFonts.Probes])
        {
            if (_loaded.ContainsKey(path) || !FontProbe.Paints(path, sample)) continue;

            using var scope = Utf8.Scope();
            Font face = ui.LoadFont(path);
            if ((int)face <= (int)Font.Icons) continue;

            foreach (Font role in SystemFonts.Roles) ui.AddFontFallback(role, face);
            _loaded.Add(path, face);
            return face;
        }

        return 0;
    }
}
