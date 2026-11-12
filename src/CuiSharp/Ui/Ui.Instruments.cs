using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>
/// One dot-matrix series: its samples, the name the readout shows beside the value, and whether
/// it is drawn in the accent color. A series carries no count of its own — the number of samples
/// is the call's, so every series reads the same number of values and they line up over the axis.
/// </summary>
public readonly struct DotSeries
{
    /// <summary>A series over <paramref name="values"/>, one sample every step from x_min to x_max.</summary>
    public DotSeries(float[] values, string? name = null, bool accent = false)
    {
        Values = values;
        Name = name;
        Accent = accent;
    }

    /// <summary>The samples. Fewer than the call asked for are padded with zero, never read past.</summary>
    public float[] Values { get; }

    /// <summary>The name the readout shows when there is more than one series.</summary>
    public string? Name { get; }

    /// <summary>Draws this series in the accent color.</summary>
    public bool Accent { get; }
}

public unsafe ref partial struct Ui
{
    /// <summary>
    /// A dot-matrix chart: values as a grid of dots, lit up to each column's value. Several series
    /// stack in their own bands, and the pointer or the arrow keys show a readout of the value
    /// under it. Returns where a click landed, so an app can add something there.
    /// </summary>
    /// <param name="id">The widget id, which is also the identity of its hover and keyboard state.</param>
    /// <param name="series">The series to stack, each in its own band.</param>
    /// <param name="count">Samples per series, spread evenly from <see cref="DotChartOpts.XMin"/> to <see cref="DotChartOpts.XMax"/>.</param>
    /// <param name="o">Axis, readout and annotation options.</param>
    public DotChartResult DotChart(UiText id, DotSeries[] series, int count, DotChartOpts o = default)
    {
        // Native reads `count` values from every series, so each one is copied into a block of
        // exactly that length: a short series is padded with zero rather than read past its end.
        int samples = Math.Max(1, count);
        var native = (NativeDotSeries*)Arena(Math.Max(1, series.Length) * sizeof(NativeDotSeries));
        for (int i = 0; i < series.Length; i++)
        {
            var s = series[i];
            float* block = Utf8.AllocFrame<float>(samples);
            var values = new Span<float>(block, samples);
            values.Clear();
            if (s.Values is { Length: > 0 } backing)
                backing.AsSpan(0, Math.Min(samples, backing.Length)).CopyTo(values);

            native[i] = new NativeDotSeries
            {
                Values = (nint)block,
                Name = Utf8.Get(s.Name),
                Accent = (byte)(s.Accent ? 1 : 0),
            };
        }

        var opts = o.ToNative();
        var result = Native.DotChart(Handle, Utf8.Get(id), (nint)native, series.Length, count, in opts);
        WidgetRecorder.Note(Handle, "DotChart", id);
        return DotChartResult.FromNative(result);
    }
}
