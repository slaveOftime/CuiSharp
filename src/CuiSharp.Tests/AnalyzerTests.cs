using System.Collections.Immutable;
using CuiSharp.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CuiSharp.Tests;

/// <summary>
/// The analyzer rules: each one fires on the shape it describes and stays quiet on the
/// idiomatic version of the same code. A rule that only ever fires is as useless as one that
/// never does, so every case checks both directions.
/// </summary>
public class AnalyzerTests
{
    private static readonly MetadataReference[] References = BuildReferences();

    private static MetadataReference[] BuildReferences()
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        var references = new List<MetadataReference>();
        foreach (string path in paths)
        {
            try
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
            catch (BadImageFormatException)
            {
                // Native images and the like: not a managed reference.
            }
        }

        string cuiSharp = Path.Combine(AppContext.BaseDirectory, "CuiSharp.dll");
        references.Add(MetadataReference.CreateFromFile(cuiSharp));
        return [.. references];
    }

    private static async Task<string[]> AnalyzeAsync(string source)
    {
        var compilation = CSharpCompilation.Create(
            "AnalyzerProbe",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14))],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(
            new FrameAnalyzer(), new ScopeAnalyzer(), new WidgetIdAnalyzer());

        var diagnostics = await compilation.WithAnalyzers(analyzers).GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics
            .Where(d => d.Id.StartsWith("CUI", StringComparison.Ordinal))
            .OrderBy(d => d.Id, StringComparer.Ordinal)
            .Select(d => d.Id + ": " + d.GetMessage())];
    }

    private const string Prologue = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using CuiSharp;
        using CuiSharp.Hosting;

        """;

    // ---- CUI001 -------------------------------------------------------------

    [Fact]
    public async Task ABlockingCallInADrawMethodIsReported()
    {
        string[] found = await AnalyzeAsync(Prologue + """
            public sealed class Page : Component
            {
                private Task<int> _work = Task.FromResult(1);

                public override void Draw(Ui ui)
                {
                    int value = _work.Result;
                    _work.Wait();
                    Thread.Sleep(10);
                }
            }
            """);

        // .Result, .Wait() and Thread.Sleep: three blocking calls, three reports.
        Assert.Equal(3, found.Count(f => f.StartsWith("CUI001", StringComparison.Ordinal)));
        Assert.All(found.Where(f => f.StartsWith("CUI001", StringComparison.Ordinal)),
            f => Assert.Contains("Mutation", f, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AwaitingTheSameWorkIsNotReported()
    {
        string[] found = await AnalyzeAsync(Prologue + """
            public sealed class Page : Component
            {
                private Task<int> _work = Task.FromResult(1);

                public override async void Draw(Ui ui)
                {
                    int value = await _work;
                    _ = value;
                }
            }
            """);

        Assert.DoesNotContain(found, f => f.StartsWith("CUI001", StringComparison.Ordinal));
    }

    // ---- CUI002 -------------------------------------------------------------

    [Fact]
    public async Task ADiscardedScopeIsReported()
    {
        string[] found = await AnalyzeAsync(Prologue + """
            public static class Page
            {
                public static void Draw(Ui ui)
                {
                    ui.Card(new Card());
                    ui.Column(new Box { Gap = 4 });
                }
            }
            """);

        Assert.Equal(2, found.Count(f => f.StartsWith("CUI002", StringComparison.Ordinal)));
        Assert.All(found.Where(f => f.StartsWith("CUI002", StringComparison.Ordinal)),
            f => Assert.Contains("using", f, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AScopeInAUsingIsNotReported()
    {
        string[] found = await AnalyzeAsync(Prologue + """
            public static class Page
            {
                public static void Draw(Ui ui)
                {
                    using (ui.Card(new Card()))
                    using (ui.Column(new Box { Gap = 4 }))
                    {
                        ui.Text("hello");
                    }
                }
            }
            """);

        Assert.DoesNotContain(found, f => f.StartsWith("CUI002", StringComparison.Ordinal));
    }

    // ---- CUI003 -------------------------------------------------------------

    [Fact]
    public async Task AnOpenScopeWhoseOpenIsNeverCheckedIsReported()
    {
        string[] found = await AnalyzeAsync(Prologue + """
            public static class Page
            {
                private static bool _open = true;

                public static void Draw(Ui ui)
                {
                    using (var sheet = ui.Sheet("details", ref _open))
                    {
                        ui.Text("body that never shows");
                    }
                }
            }
            """);

        Assert.Single(found, f => f.StartsWith("CUI003", StringComparison.Ordinal));
        Assert.Contains("if (sheet)", found.Single(f => f.StartsWith("CUI003", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGuardedOpenScopeIsNotReported()
    {
        string[] found = await AnalyzeAsync(Prologue + """
            public static class Page
            {
                private static bool _open = true;

                public static void Draw(Ui ui)
                {
                    using (var sheet = ui.Sheet("details", ref _open))
                    {
                        if (sheet.Open) ui.Text("body");
                    }
                }
            }
            """);

        Assert.DoesNotContain(found, f => f.StartsWith("CUI003", StringComparison.Ordinal));
    }

    // ---- CUI004 -------------------------------------------------------------

    [Fact]
    public async Task AConstantIdInALoopIsReported()
    {
        string[] found = await AnalyzeAsync(Prologue + """
            public static class Page
            {
                public static void Draw(Ui ui, string[] rows)
                {
                    foreach (string row in rows)
                    {
                        ui.Text(row);
                        if (ui.Button("Open")) { }
                    }
                }
            }
            """);

        // The id argument of each widget call: ui.Text takes text, the button takes its label,
        // so only the widgets whose first parameter is an id are candidates.
        Assert.DoesNotContain(found, f => f.StartsWith("CUI004", StringComparison.Ordinal) && f.Contains("row", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSameIdInsideAnIdScopeIsNotReported()
    {
        string[] found = await AnalyzeAsync(Prologue + """
            public static class Page
            {
                public static void Draw(Ui ui, string[] rows)
                {
                    for (int i = 0; i < rows.Length; i++)
                    {
                        using (ui.Id(i)) ui.Input("name", ref rows[i]);
                    }
                }
            }
            """);

        Assert.DoesNotContain(found, f => f.StartsWith("CUI004", StringComparison.Ordinal));
    }

    // ---- CUI005 / CUI006 ----------------------------------------------------

    [Fact]
    public async Task ConfigureAwaitAndAsyncVoidInsideAComponentAreReported()
    {
        string[] found = await AnalyzeAsync(Prologue + """
            public sealed class Page : Component
            {
                public override async void Draw(Ui ui)
                {
                    await Task.Delay(1).ConfigureAwait(false);
                    ui.Text("drawn");
                }
            }
            """);

        Assert.Single(found, f => f.StartsWith("CUI005", StringComparison.Ordinal));
        Assert.Single(found, f => f.StartsWith("CUI006", StringComparison.Ordinal));
        Assert.Contains("Mutation", found.Single(f => f.StartsWith("CUI006", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AComponentAwaitingNormallyIsNotReported()
    {
        string[] found = await AnalyzeAsync(Prologue + """
            public sealed class Page : Component
            {
                public override void Draw(Ui ui) => ui.Text("drawn");

                private async Task LoadAsync()
                {
                    await Task.Delay(1);
                }
            }
            """);

        Assert.DoesNotContain(found, f => f.StartsWith("CUI005", StringComparison.Ordinal));
        Assert.DoesNotContain(found, f => f.StartsWith("CUI006", StringComparison.Ordinal));
    }

    // ---- CUI007 -------------------------------------------------------------

    [Fact]
    public async Task AnAllocationInADrawMethodIsReportedAsInfo()
    {
        string[] found = await AnalyzeAsync(Prologue + """
            public static class Page
            {
                public static void Draw(Ui ui, int count)
                {
                    var values = new float[count];
                    var label = "rows: " + count;
                    ui.Text(label);
                    _ = values;
                }
            }
            """);

        Assert.Equal(2, found.Count(f => f.StartsWith("CUI007", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AllocationOutsideAFrameIsNotReported()
    {
        string[] found = await AnalyzeAsync(Prologue + """
            public static class Model
            {
                public static float[] Build(int count) => new float[count];

                public static string Describe(int count) => "rows: " + count;
            }
            """);

        Assert.DoesNotContain(found, f => f.StartsWith("CUI007", StringComparison.Ordinal));
    }
}
