using System.IO.MemoryMappedFiles;
using System.Numerics;
using CuiSharp.Interop;

namespace CuiSharp;

// One owner per native context. Never key this by a native pointer: freed pointers can be reused
// by a later window, and a cached font handle belongs only to the context that loaded it.
internal sealed class SystemFontFallbackState
{
    private readonly Dictionary<string, Font> _samples = new(StringComparer.Ordinal);

    // The candidates loaded onto this context, in the order that chose them: a file that paints one
    // script is tried again for the next before another file is loaded.
    private readonly List<string> _files = [];

    // The faces loaded onto this context, by path. A path the probe chose but that failed to load
    // is absent here, which is what makes a later search try it again rather than trust it.
    private readonly Dictionary<string, (Font Face, MemoryMappedViewAccessor? Backing)> _faces = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private string[]? _probes;
    private int _installed;
    private bool _all;

    // The search finishes on a pool thread and publishes one immutable answer; the first frame that
    // finds it installs the faces. _started stops a second frame from starting the same search.
    private SystemFontFallbackProbe? _ready;
    private int _started;

    // The method that does the work, held as one cached delegate so starting a search allocates
    // nothing on the frame that asked for it.
    private static readonly Action<object?> SearchQueued = static state => ((SystemFontFallbackState)state!).Search();

    /// <summary>
    /// Whether a search is still out: true from the call that started it until a frame has installed
    /// what it found, which is what a test pumps frames on rather than guessing a frame count.
    /// </summary>
    internal bool Searching => _started != 0 && !_all;

    /// <summary>
    /// Waits for an in-flight pool search to publish its answer, without touching the native
    /// context. A headless settle loop advances simulated time faster than the pool can probe
    /// files on disk, so it waits here before drawing the frame that installs the faces.
    /// </summary>
    internal bool Wait(int millisecondsTimeout = 5_000)
    {
        if (_all || _started == 0) return true;
        long deadline = Environment.TickCount64 + millisecondsTimeout;
        while (Volatile.Read(ref _ready) is null)
        {
            if (Environment.TickCount64 >= deadline) return false;
            Thread.Sleep(1);
        }

        return true;
    }

    internal int EnsureAll(Ui ui)
    {
        if (_all) return _installed;

        if (Volatile.Read(ref _ready) is { } probe)
        {
            Install(ui, probe);
            return _installed;
        }

        Start();

        // A frame is what installs the answer, and the loop sleeps again as soon as nothing asks for
        // one — a pool thread cannot wake a sleeping window. Asking here keeps frames coming until
        // the search lands, which is what an animating widget asks for too.
        Native.RequestRedraw(ui.Handle);
        return _installed;
    }

    internal Font Ensure(Ui ui, string sample)
    {
        // A finished search answers this too, so asking for one sample after the set costs nothing.
        if (!_all && Volatile.Read(ref _ready) is { } probe) Install(ui, probe);
        if (_samples.TryGetValue(sample, out Font cached)) return cached;

        Font face = Find(ui, sample);
        _samples.Add(sample, face);
        return face;
    }

    /// <summary>
    /// Starts the search on the pool. Probing is pixels — an isolated native context, a font load and
    /// a render per candidate — and it is what used to spend half a second of the first frame with
    /// nothing but a blank window to show for it.
    /// </summary>
    private void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;
        ThreadPool.UnsafeQueueUserWorkItem(SearchQueued, this, preferLocal: false);
    }

    /// <summary>The work itself, on the pool: it answers, and the frame that finds the answer loads.</summary>
    private void Search()
    {
        SystemFontFallbackProbe probe;
        try
        {
            probe = SystemFontFallbackProbe.Run();
        }
        catch (Exception)
        {
            // A search that cannot even ask is a machine with no system face to offer, which is not
            // a reason to fail a frame that is already drawing.
            probe = SystemFontFallbackProbe.None;
        }

        Volatile.Write(ref _ready, probe);
    }

    private void Install(Ui ui, SystemFontFallbackProbe probe)
    {
        _all = true;
        int before = _installed;

        foreach (string path in probe.Files)
        {
            if (!_faces.ContainsKey(path)) Load(ui, path);
        }

        foreach ((string sample, string? path) in probe.Matches)
        {
            if (path is null) _samples.TryAdd(sample, 0);
            else if (_faces.TryGetValue(path, out var loaded)) _samples.TryAdd(sample, loaded.Face);
        }

        // A widget drawn earlier in this frame measured and shaped its text before these faces
        // were on the fallback chain; one more frame draws the whole tree with them, and then
        // _all is true so the loop goes back to sleep.
        if (_installed > before) Native.RequestRedraw(ui.Handle);
    }

    private Font Find(Ui ui, string sample)
    {
        if (!FontProbe.HasVisibleScalars(sample) || FontProbe.PaintsThemeFace(sample)) return 0;

        // Reuse files installed by either helper, even when two different samples need them.
        foreach (string path in _files)
            if (FontProbe.Paints(path, sample)) return _faces[path].Face;

        foreach (string path in _probes ??= [.. SystemFonts.Probes])
        {
            if (_faces.ContainsKey(path) || !FontProbe.Paints(path, sample)) continue;

            Font face = Load(ui, path);
            if (face != 0) return face;
        }

        return 0;
    }

    private Font Load(Ui ui, string path)
    {
        using var scope = Utf8.Scope();
        Font face = SystemFontFile.Load(ui.Handle, path, out var backing);
        if ((int)face <= (int)Font.Icons) return 0;

        _faces.Add(path, (face, backing));
        foreach (Font role in SystemFonts.Roles) ui.AddFontFallback(role, face);
        _files.Add(path);
        _installed++;
        return face;
    }

    // The search owns only temporary views; it never touches the installed faces.
    internal void ReleaseAfterContextDestroyed()
    {
        foreach (var face in _faces.Values) face.Backing?.Dispose();
        _faces.Clear();
    }
}

/// <summary>
/// What a search found: the candidate files to load, in the order that chose them, and the file that
/// paints each script. Built on the pool and never mutated after the reference is published, so the
/// frame reads it with one volatile load.
/// </summary>
internal sealed class SystemFontFallbackProbe
{
    /// <summary>No search ran, or it could not ask: a machine with no system face to offer.</summary>
    internal static readonly SystemFontFallbackProbe None = new([], new(StringComparer.Ordinal));

    internal SystemFontFallbackProbe(string[] files, Dictionary<string, string?> matches)
    {
        Files = files;
        Matches = matches;
    }

    /// <summary>The files that paint something, in the order that found them.</summary>
    internal string[] Files { get; }

    /// <summary>Per sample in <see cref="SystemFonts.Samples"/>, the file that paints it, or null.</summary>
    internal Dictionary<string, string?> Matches { get; }

    /// <summary>
    /// Takes every sample's answer without loading a face onto the caller's context. Each candidate
    /// font file is opened at most once across all scripts: once a file is loaded into its probe
    /// context it answers every remaining script before that context is torn down.
    /// </summary>
    internal static SystemFontFallbackProbe Run()
    {
        ReadOnlySpan<string> samples = SystemFonts.Samples;
        var files = new List<string>();
        var matches = new Dictionary<string, string?>(samples.Length, StringComparer.Ordinal);
        uint pending = (1u << samples.Length) - 1;

        uint themed = FontProbe.PaintsMask(path: null, samples, pending);
        for (uint bits = themed; bits != 0; bits &= bits - 1)
            matches[samples[BitOperations.TrailingZeroCount(bits)]] = null;
        pending &= ~themed;

        string[] probes = [.. SystemFonts.Probes];
        uint[] masks = new uint[probes.Length];
        Array.Fill(masks, uint.MaxValue);

        while (pending != 0)
        {
            int first = BitOperations.TrailingZeroCount(pending);
            uint firstBit = 1u << first;
            uint hit = 0;
            string? chosen = null;

            for (int i = 0; i < probes.Length; i++)
            {
                string path = probes[i];
                if (files.Contains(path)) continue;

                uint mask = masks[i];
                if (mask == uint.MaxValue)
                    masks[i] = mask = FontProbe.PaintsMask(path, samples, pending);

                if ((mask & firstBit) == 0) continue;

                files.Add(path);
                chosen = path;
                hit = mask & pending;
                break;
            }

            if (chosen is null)
            {
                matches[samples[first]] = null;
                pending &= ~firstBit;
                continue;
            }

            for (uint bits = hit; bits != 0; bits &= bits - 1)
                matches[samples[BitOperations.TrailingZeroCount(bits)]] = chosen;
            pending &= ~hit;
        }

        return new SystemFontFallbackProbe([.. files], matches);
    }
}
