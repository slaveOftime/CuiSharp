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
    /// <param name="xLabel">Writes each x label and the readout's x, in place of <see cref="DotChartOpts.XFormat"/>; the spacing still comes from the axis units. Keep it hoisted — a closure allocates.</param>
    /// <param name="valueLabel">Writes the readout's value, in place of <see cref="DotChartOpts.Format"/>; the unit still follows it.</param>
    public DotChartResult DotChart(UiText id, scoped ReadOnlySpan<DotSeries> series, int count, DotChartOpts o = default,
        Func<double, string?>? xLabel = null, Func<double, string?>? valueLabel = null)
    {
        // Native reads `count` values from every series, so each one is copied into a block of
        // exactly that length: a short series is padded with zero rather than read past its end.
        // The clamped length is the one native is told about too, so the block it may read and the
        // number it was given can never disagree.
        int samples = Math.Max(1, count);
        var native = (NativeDotSeries*)Arena(Math.Max(1, series.Length) * sizeof(NativeDotSeries));
        for (int i = 0; i < series.Length; i++)
        {
            DotSeries s = series[i];
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
        if (xLabel is not null)
        {
            nint user = FrameCallbacks.Rent("dot-chart-x:" + MakeId(id).ToString(System.Globalization.CultureInfo.InvariantCulture), xLabel);
            opts.XLabel = FrameCallbacks.AxisLabelPointer;
            opts.XLabelUser = user;
        }
        if (valueLabel is not null)
        {
            nint user = FrameCallbacks.Rent("dot-chart-v:" + MakeId(id).ToString(System.Globalization.CultureInfo.InvariantCulture), valueLabel);
            opts.ValueLabel = FrameCallbacks.AxisLabelPointer;
            opts.ValueLabelUser = user;
        }
        var result = Native.DotChart(Handle, Utf8.Get(id), (nint)native, series.Length, samples, in opts);
        WidgetRecorder.Note(Handle, "DotChart", id);
        return DotChartResult.FromNative(result);
    }

    /// <summary>
    /// An event track: moments on a time axis, as dots you drag along it. A click on an empty spot
    /// adds an event there and a double-click on one removes it. You own the array: the track keeps
    /// it sorted, edits it in place and never grows it past its length.
    /// </summary>
    /// <param name="id">The widget id, which is also the identity of its drag state.</param>
    /// <param name="times">The event times, in seconds; sorted and edited in place, and copied back every frame.</param>
    /// <param name="count">How many of <paramref name="times"/> hold events; the track reports the new count here.</param>
    /// <param name="min">The left end of the axis, in seconds.</param>
    /// <param name="max">The right end of the axis, in seconds.</param>
    /// <param name="selected">The selected event, or -1; the track's click and keys move it.</param>
    /// <param name="o">Axis and interaction options.</param>
    /// <param name="label">Writes the axis labels and the selected event's time, in place of <see cref="EventTrackOpts.Format"/>; the spacing still comes from the axis units.</param>
    public EventTrackResult EventTrack(UiText id, scoped Span<double> times, ref int count, double min, double max,
        ref int selected, EventTrackOpts o = default, Func<double, string?>? label = null)
    {
        var __times = Utf8.AllocFrame<double>(times.Length);
        times.CopyTo(new Span<double>(__times, times.Length));
        int __timesCount = count;
        int __selected = (int)(selected);
        var __opts = o.ToNative((nint)(&__selected));
        if (label is not null)
        {
            nint user = FrameCallbacks.Rent("event-track:" + MakeId(id).ToString(System.Globalization.CultureInfo.InvariantCulture), label);
            __opts.Label = FrameCallbacks.AxisLabelPointer;
            __opts.LabelUser = user;
        }
        var __result = Native.EventTrack(Handle, Utf8.Get(id), (nint)__times, ref __timesCount, (int)(times.Length), min, max, in __opts);

        // A primitive in-place array comes back every frame: native sorts and clamps it before it
        // draws, and the selected index refers to its order.
        new Span<double>(__times, Math.Min(__timesCount, times.Length)).CopyTo(times);
        count = __timesCount;
        selected = __selected;
        WidgetRecorder.NoteValue(Handle, "EventTrack", id, (double)selected);
        return EventTrackResult.FromNative(__result);
    }
}
