using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>A stack-only drawing context. Do not retain it outside the current frame.</summary>
public unsafe ref partial struct Ui
{
    internal readonly nint Handle;
    private readonly UiFrameState _frame;

    internal Ui(nint handle, UiFrameState frame) { Handle = handle; _frame = frame; }

    private byte* Arena(int bytes) => Utf8.AllocFrameBytes(bytes);
    private float* FloatBlock(ReadOnlySpan<float> values) => (float*)UiMarshal.Values<float>(values);

    // Text inputs and charts stay hand-written: inputs need the per-id buffer
    // pool (a managed string cannot be the field's storage) and charts need
    // automatic decimation. The rest of these sections is generated.

    /// <summary>
    /// A single-line text field. Assign the string; CUI edits it in place and the
    /// new value comes back through <paramref name="value"/> on the frames it changed.
    /// </summary>
    /// <example><code>if (ui.Input("name", ref _name, new() { Label = "Name" }).Changed) Save();</code></example>
    public InputResult Input(UiText id, ref string value, InputOpts o = default) =>
        TextField(id, ref value, o, TextFieldKind.Input, 256);

    /// <summary>A multi-line text field. See <see cref="Input"/>.</summary>
    public InputResult TextArea(UiText id, ref string value, InputOpts o = default) =>
        TextField(id, ref value, o, TextFieldKind.Area, 512);

    /// <summary>A search field. See <see cref="Input"/>.</summary>
    public InputResult Search(UiText id, ref string value, InputOpts o = default) =>
        TextField(id, ref value, o, TextFieldKind.Search, 256);

    /// <summary>
    /// Rents the pooled buffer for one editable string. A generated wrapper for a
    /// `char * buf, size_t cap` pair calls this, hands the bytes to CUI, and reads the
    /// text back with <see cref="DecodeText"/> on the frames the widget changed.
    /// </summary>
    internal TextFieldPool.Slot RentText(uint id, ref string value, int capacity) =>
        _frame.Text.Rent(id, value, capacity);

    /// <summary>Copies the text CUI left in a rented buffer back into a managed string.</summary>
    internal static string DecodeText(TextFieldPool.Slot slot) => TextFieldPool.Decode(slot);

    /// <summary>Whether native code changed the rented buffer, without allocating.</summary>
    internal static bool TextChanged(TextFieldPool.Slot slot) => TextFieldPool.Changed(slot);

    private enum TextFieldKind { Input, Area, Search }

    private InputResult TextField(UiText id, ref string value, InputOpts o, TextFieldKind kind, int capacity)
    {
        uint key = Native.MakeId(Handle, Utf8.Get(id));
        var slot = _frame.Text.Rent(key, value, capacity);
        var n = o.ToNative();
        int result = kind switch
        {
            TextFieldKind.Input => Native.Input(Handle, Utf8.Get(id), (nint)slot.Buffer, (nuint)slot.Capacity, in n),
            TextFieldKind.Area => Native.Textarea(Handle, Utf8.Get(id), (nint)slot.Buffer, (nuint)slot.Capacity, in n),
            _ => Native.Search(Handle, Utf8.Get(id), (nint)slot.Buffer, (nuint)slot.Capacity, in n),
        };
        WidgetRecorder.Note(Handle, kind switch
        {
            TextFieldKind.Area => "TextArea",
            TextFieldKind.Search => "Search",
            _ => "Input",
        }, id);
        var parsed = new InputResult(result);
        if (parsed.Changed) value = TextFieldPool.Decode(slot);
        return parsed;
    }

    public bool Number(UiText id, ref double value, double min, double max, NumberOpts o = default)
    {
        var n = o.ToNative();
        bool changed = Native.Number(Handle, Utf8.Get(id), ref value, min, max, in n) != 0;
        WidgetRecorder.Note(Handle, "Number", id);
        return changed;
    }

    // Charts decimate long feeds into the frame arena, so a million samples cost
    // what a few hundred do. MaxPoints defaults to twice the viewport width, an
    // upper bound on the chart's pixel width before it has been laid out.
    public void LineChart(UiText id, Series[] series, ChartOpts o = default) => Chart(id, series, o, line: true);
    public void BarChart(UiText id, Series[] series, ChartOpts o = default) => Chart(id, series, o, line: false);

    /// <summary>
    /// A line chart of one series. This is the overload most charts want: the samples
    /// are passed directly, and an empty span draws nothing at all.
    /// </summary>
    /// <example><code>
    /// ui.LineChart("prices", samples, new() { Height = 240, Area = true, Format = "%.2f" }, Color.Accent);
    /// </code></example>
    public void LineChart(UiText id, ReadOnlySpan<float> values, ChartOpts o = default, Color color = default) =>
        ChartOne(id, values, o, color, line: true);

    /// <summary>A bar chart of one series. See <see cref="LineChart(UiText, ReadOnlySpan{float}, ChartOpts, Color)"/>.</summary>
    public void BarChart(UiText id, ReadOnlySpan<float> values, ChartOpts o = default, Color color = default) =>
        ChartOne(id, values, o, color, line: false);

    private void ChartOne(UiText id, ReadOnlySpan<float> values, ChartOpts o, Color color, bool line)
    {
        // An invalid mode is a caller error, not an empty-feed state: check it before the
        // empty shortcut so a bogus mode cannot ride the "draws nothing" path.
        if (o.Normalize != NormalizeMode.None) ChartNormalizer.ValidateMode(o.Normalize);

        // An empty span is a normal state (a feed that has not arrived yet) and it
        // must draw nothing: passing it on would give the chart a zero-length series.
        if (values.IsEmpty) return;

        // Normalization validates and measures the whole clipped source before decimation
        // (a decimated-away NaN must not evade validation) and scales the retained arena
        // points after it, so the cost tracks the source once and the scale once.
        float baseline = o.Normalize == NormalizeMode.None
            ? 0f
            : ChartNormalizer.ValidateAndFindBaseline(values);

        int maxPoints = o.MaxPoints > 0 ? o.MaxPoints : Math.Max(64, (int)(ViewportSize().X * 2));
        int buckets = Math.Max(1, maxPoints / 2);
        int capacity = SeriesDecimator.RequiredCapacity(buckets);

        // Decimation needs the bucket capacity; a pass-through needs exactly its own
        // length. Reserving the smaller of the two and writing the larger walked past
        // the block whenever a pass-through count exceeded an odd maxPoints' capacity.
        bool decimate = values.Length > maxPoints;
        int count;
        float* block = Utf8.AllocFrame<float>(Math.Max(1, decimate ? capacity : values.Length));
        if (decimate)
            count = SeriesDecimator.Decimate(values, new Span<float>(block, capacity), buckets);
        else
        {
            count = values.Length;
            values.CopyTo(new Span<float>(block, count));
        }
        if (o.Normalize != NormalizeMode.None)
            ChartNormalizer.Scale(new Span<float>(block, count), baseline, o.Normalize);

        var native = (NativeSeries*)Arena(sizeof(NativeSeries));
        native[0] = new NativeSeries
        {
            // A default color is "unset", and CUI then uses the chart's own series color.
            Color = UiMarshal.ToNative(color),
            Values = (nint)block,
            Count = count,
        };

        // One unnamed series needs no legend: there is nothing to tell apart.
        var opts = o.ToNative();
        opts.NoLegend = 1;
        if (line) Native.LineChart(Handle, Utf8.Get(id), (nint)native, 1, in opts);
        else Native.BarChart(Handle, Utf8.Get(id), (nint)native, 1, in opts);
        WidgetRecorder.Note(Handle, line ? "LineChart" : "BarChart", id);
    }

    private void Chart(UiText id, Series[] series, ChartOpts o, bool line)
    {
        // Same rule as ChartOne: an invalid mode fails even when every series is empty.
        if (o.Normalize != NormalizeMode.None) ChartNormalizer.ValidateMode(o.Normalize);

        int maxPoints = o.MaxPoints > 0 ? o.MaxPoints : Math.Max(64, (int)(ViewportSize().X * 2));
        int buckets = Math.Max(1, maxPoints / 2);
        int capacity = SeriesDecimator.RequiredCapacity(buckets);
        var native = (NativeSeries*)Arena(Math.Max(1, series.Length) * sizeof(NativeSeries));
        for (int i = 0; i < series.Length; i++)
        {
            var s = series[i];

            // Count 0 means "draw the whole array"; a set Count clips it. A series with
            // no samples draws nothing rather than a zero-length line.
            ReadOnlySpan<float> values = s.Values is { Length: > 0 } backing
                ? backing.AsSpan(0, s.Count > 0 ? Math.Min(s.Count, backing.Length) : backing.Length)
                : default;
            if (values.IsEmpty) { native[i] = default; continue; }

            // Baseline and validation see the Count-clipped source, before decimation
            // hides anything; the scale below sees only the retained arena points.
            float baseline = o.Normalize == NormalizeMode.None
                ? 0f
                : ChartNormalizer.ValidateAndFindBaseline(values);

            int count = values.Length;
            // Exact length for a pass-through, bucket capacity only when decimating;
            // see ChartOne for what mixing the two did to the arena's neighbors.
            bool decimate = count > maxPoints;
            float* block = Utf8.AllocFrame<float>(Math.Max(1, decimate ? capacity : count));
            if (decimate)
                count = SeriesDecimator.Decimate(values, new Span<float>(block, capacity), buckets);
            else
                values.CopyTo(new Span<float>(block, count));
            if (o.Normalize != NormalizeMode.None)
                ChartNormalizer.Scale(new Span<float>(block, count), baseline, o.Normalize);

            native[i] = new NativeSeries
            {
                Name = Utf8.Get(s.Name),
                Values = (nint)block,
                Count = count,
                Color = UiMarshal.ToNative(s.Color),
            };
        }
        var opts = o.ToNative();
        if (line) Native.LineChart(Handle, Utf8.Get(id), (nint)native, series.Length, in opts);
        else Native.BarChart(Handle, Utf8.Get(id), (nint)native, series.Length, in opts);
        WidgetRecorder.Note(Handle, line ? "LineChart" : "BarChart", id);
    }

    public void DonutChart(UiText id, float[] values, string[]? labels = null, ChartOpts o = default)
    {
        // Slices are the values themselves; there is no series-vs-baseline relation a
        // normalization could mean. Fail loudly rather than draw an unnormalized donut
        // that the caller believes is rebased.
        if (o.Normalize != NormalizeMode.None)
            throw new ArgumentException(
                "Normalize scales line and bar series against a baseline; a donut's slices are its values - use it with LineChart or BarChart only",
                nameof(o));

        o.Labels = labels;
        var opts = o.ToNative();
        Native.DonutChart(Handle, Utf8.Get(id), (nint)FloatBlock(values), opts.Labels, values.Length, in opts);
        WidgetRecorder.Note(Handle, "DonutChart", id);
    }

    /// <summary>Requests another frame; call only from the synchronous draw path.</summary>
    public void RequestRedraw() => Native.RequestRedraw(Handle);
    public bool NeedsRedraw => Native.NeedsRedraw(Handle) != 0;
}
