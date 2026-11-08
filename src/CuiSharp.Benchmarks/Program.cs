using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Running;

namespace CuiSharp.Benchmarks;

/// <summary>
/// Three modes:
/// <list type="bullet">
/// <item><c>dotnet run -c Release</c> — BenchmarkDotNet suites (throughput, allocation).</item>
/// <item><c>--fps [frames]</c> — frame-time distribution for a live chart, plus
/// how many frames fit a 144/120/60/30 fps budget.</item>
/// <item><c>--scale</c> — one-shot cost per frame from 1 000 to 1 000 000 points,
/// raw versus decimated.</item>
/// </list>
/// BenchmarkDotNet output lands in <c>BenchmarkDotNet.Artifacts/</c>; CI uploads it.
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--fps")
        {
            int frames = args.Length > 1 && int.TryParse(args[1], out int n) ? n : 600;
            return RealtimeHarness.RunFps(frames);
        }

        if (args.Length > 0 && args[0] == "--scale")
        {
            return RealtimeHarness.RunScaling();
        }

        var config = DefaultConfig.Instance
            .AddExporter(JsonExporter.Full)
            .WithOptions(ConfigOptions.DisableOptimizationsValidator);

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
        return 0;
    }
}
