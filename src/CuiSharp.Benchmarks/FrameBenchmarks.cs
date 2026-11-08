using BenchmarkDotNet.Attributes;

namespace CuiSharp.Benchmarks;

/// <summary>
/// What one frame costs as the amount of data grows, which is the number that
/// decides whether an immediate-mode UI is viable for your screen.
/// <para>
/// Build is what your code does (component calls, layout, the draw list).
/// Raster is what the software renderer does with it. A real GPU frame does the
/// second part on the GPU, so it is reported separately.
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class FrameBuildBenchmarks
{
    private CuiSurface _surface = null!;
    private byte[] _pixels = null!;
    private static readonly string[] RowLabels = ["Name", "Email", "Team", "Commits", "Score"];

    [Params(200, 2_000)]
    public int Rows;

    [GlobalSetup]
    public void Setup()
    {
        _surface = CuiSurface.Create(1280, 800);
        _pixels = new byte[_surface.PixelWidth * _surface.PixelHeight * 4];
    }

    [GlobalCleanup]
    public void Cleanup() => _surface.Dispose();

    /// <summary>A list screen: the shape of most real UI.</summary>
    [Benchmark(Baseline = true)]
    public void List()
    {
        var ui = _surface.Ui;
        _surface.BeginFrame();
        using (ui.Column(new Box { Pad = 16, Gap = 4 }))
        {
            ui.Heading("People", 3);
            for (int i = 0; i < Rows; i++)
            {
                using (ui.Row(new Box { Gap = 12, Height = Size.Px(36) }))
                {
                    ui.Text("row", new TextOpts { Width = Size.Fill, NoWrap = true });
                    ui.Text($"#{i}", new TextOpts { Tabular = true, NoWrap = true });
                }
            }
        }
        _surface.EndFrame();
    }

    /// <summary>Same rows with UTF-8 literals; interpolation uses UiText in both paths.</summary>
    [Benchmark]
    public void ListInterpolatedUtf8()
    {
        var ui = _surface.Ui;
        _surface.BeginFrame();
        using (ui.Column(new Box { Pad = 16, Gap = 4 }))
        {
            ui.Heading("People"u8, 3);
            for (int i = 0; i < Rows; i++)
            {
                using (ui.Row(new Box { Gap = 12, Height = Size.Px(36) }))
                {
                    ui.Text("row"u8, new TextOpts { Width = Size.Fill, NoWrap = true });
                    ui.Text($"#{i}", new TextOpts { Tabular = true, NoWrap = true });
                }
            }
        }
        _surface.EndFrame();
    }

    /// <summary>Full frame including software rasterization.</summary>
    [Benchmark]
    public void ListWithRaster()
    {
        List();
        _surface.Render(_pixels);
    }

    /// <summary>A segmented control and a table-like row of chips: many small widgets.</summary>
    [Benchmark]
    public void ManySmallWidgets()
    {
        var ui = _surface.Ui;
        _surface.BeginFrame();
        using (ui.Row(new Box { Gap = 6, Wrap = true, Pad = 16 }))
        {
            for (int i = 0; i < Rows; i++)
            {
                bool on = (i & 1) == 0;
                ui.Chip("chip", ref on);
            }
        }
        _surface.EndFrame();
    }

    /// <summary>High-frequency updates: values that change every frame, the worst case for a binding.</summary>
    [Benchmark]
    public void LiveMetrics()
    {
        var ui = _surface.Ui;
        _surface.BeginFrame();
        using (ui.Row(new Box { Gap = 16, Pad = 16 }))
        {
            for (int i = 0; i < Rows; i++)
            {
                float value = i * 0.37f;
                ui.Slider("level", ref value, 0f, 100f);
            }
        }
        _surface.EndFrame();
    }
}

/// <summary>
/// Isolated costs of the marshalling layer, with no drawing involved. These are
/// the numbers you look at when a frame is unexpectedly slow.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public unsafe class MarshallingBenchmarks
{
    private const string Label = "Save changes";

    [Benchmark(Baseline = true)]
    public int EncodeString()
    {
        Utf8.BeginFrame();
        return (int)Utf8.Get(Label);
    }

    [Benchmark]
    public int EncodeUtf8Literal()
    {
        Utf8.BeginFrame();
        return (int)Utf8.Get("Save changes"u8);
    }

    [Benchmark]
    public nint FormatWithHandler()
    {
        var h = new Utf8Handler(8, 2);
        h.AppendLiteral("rows ");
        h.AppendFormatted(1234);
        h.AppendLiteral(" of ");
        h.AppendFormatted(5000);
        return (nint)h.Ptr;
    }

    [Benchmark]
    public string FormatWithStringInterpolation() => $"rows {1234} of {5000}";

    /// <summary>Converting one options struct, including encoding its three strings.</summary>
    [Benchmark]
    public int OptionStructToNative()
    {
        Utf8.BeginFrame();
        return new ButtonOpts
        {
            Variant = ButtonVariant.Primary,
            Icon = "plus",
            Tooltip = "Add",
        }.ToNative().GetHashCode();
    }
}
