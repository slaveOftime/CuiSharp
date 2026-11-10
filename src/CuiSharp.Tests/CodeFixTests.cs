using System.Collections.Immutable;
using System.Reflection;
using CuiSharp.Analyzers;
using CuiSharp.CodeFixes;
using CuiSharp.Hosting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Formatting;

namespace CuiSharp.Tests;

public sealed class CodeFixTests
{
    private const string Prologue = "using System.Threading.Tasks; using CuiSharp; using CuiSharp.Hosting;\n";

    [Theory]
    [InlineData("ui.Column(new Box { Pad = 32 });")]
    [InlineData("var container = ui.Column(new Box { Pad = 32 });")]
    public async Task FixingADroppedLayoutKeepsItsChildrenInsideAndClosesIt(string scope)
    {
        string source = Prologue + "public static class Probe { public static void Draw(Ui ui) { " + scope + " ui.Button(\"body\"); } }";
        var fixedCompilation = await FixAsync(source, "CUI002");
        using var app = HeadlessApp.Start(DrawFrom(fixedCompilation), 480, 320);
        Assert.Empty(app.Faults);
        Assert.InRange(app.Find("body").Rect.X, 32, 33);
        app.Frame();
        Assert.Empty(app.Faults);
    }

    [Theory]
    [InlineData("ui.Dialog(\"details\", ref Open);", "CUI002")]
    [InlineData("var dialog = ui.Dialog(\"details\", ref Open);", "CUI002")]
    [InlineData("using var dialog = ui.Dialog(\"details\", ref Open);", "CUI003")]
    public async Task FixingAConditionalDeclarationDoesNotDrawItsClosedBody(string scope, string id)
    {
        string source = Prologue + "public static class Probe { public static bool Open; public static int Calls; " +
            "public static void Draw(Ui ui) { " + scope + " Calls++; ui.Text(\"body\"); } }";
        var fixedCompilation = await FixAsync(source, id);
        DrawFn draw = DrawFrom(fixedCompilation);
        Type probe = draw.Method.DeclaringType!;
        using var app = HeadlessApp.Start(draw, 480, 320);
        Assert.Empty(app.Faults);
        Assert.Equal(0, probe.GetField("Calls")!.GetValue(null));
        probe.GetField("Open")!.SetValue(null, true);
        app.Frame();
        Assert.Empty(app.Faults);
        Assert.Equal(1, probe.GetField("Calls")!.GetValue(null));
    }

    [Theory]
    [InlineData("dialog")]
    [InlineData("@event")]
    public async Task FixingAConditionalUsingStatementGuardsOnlyItsOwnBody(string name)
    {
        string source = Prologue + "public static class Probe { public static bool Open; public static int Calls; public static int After; " +
            "public static void Draw(Ui ui) { using (var " + name + " = ui.Dialog(\"details\", ref Open)) { Calls++; } After++; } }";
        var fixedCompilation = await FixAsync(source, "CUI003");
        DrawFn draw = DrawFrom(fixedCompilation);
        Type probe = draw.Method.DeclaringType!;
        using var app = HeadlessApp.Start(draw, 480, 320);
        Assert.Empty(app.Faults);
        Assert.Equal(0, probe.GetField("Calls")!.GetValue(null));
        Assert.Equal(1, probe.GetField("After")!.GetValue(null));
    }

    [Fact]
    public async Task AGeneratedScopeNameDoesNotShadowAnUnusedParameterOrALaterLocal()
    {
        string source = Prologue + "public static class Probe { public static void Draw(Ui ui, int scope) { " +
            "ui.Column(); int scope1 = 1; ui.Text($\"{scope1}\"); } }";
        await FixAsync(source, "CUI002");
    }

    [Theory]
    [InlineData("Task.FromResult(7)")]
    [InlineData("new ValueTask<int>(7)")]
    public async Task RemovingConfigureAwaitKeepsTheAwaitedResult(string task)
    {
        string source = Prologue + "public sealed class Probe : Component { protected override void Draw(Ui ui) {} " +
            "public async Task<int> LoadAsync() => await " + task + ".ConfigureAwait(false); }";
        var fixedCompilation = await FixAsync(source, "CUI005");
        Type probe = Emit(fixedCompilation).GetType("Probe")!;
        var instance = Activator.CreateInstance(probe);
        var work = (Task<int>)probe.GetMethod("LoadAsync")!.Invoke(instance, null)!;
        Assert.Equal(7, await work);
    }

    [Fact]
    public async Task ALookalikeConfigureAwaitIsNotRewritten()
    {
        string source = Prologue + "public sealed class Probe : Component { protected override void Draw(Ui ui) {} " +
            "public Task<int> ConfigureAwait(bool flag) => Task.FromResult(flag ? 1 : 2); " +
            "public async Task<int> LoadAsync() => await this.ConfigureAwait(false); }";
        using var workspace = new AdhocWorkspace();
        Document document = DocumentFor(workspace, source);
        var diagnostic = Assert.Single((await AnalyzeAsync(document)).Where(d => d.Id == "CUI005"));
        var actions = new List<CodeAction>();
        await new ConfigureAwaitCodeFixProvider().RegisterCodeFixesAsync(
            new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None));
        Assert.Empty(actions);
    }

    [Theory]
    [InlineData("ConfigureAwaitOptions.SuppressThrowing")]
    [InlineData("ConfigureAwaitOptions.ForceYielding")]
    public async Task ConfigureAwaitOptionsAreNotRemoved(string options)
    {
        string source = Prologue + "public sealed class Probe : Component { protected override void Draw(Ui ui) {} " +
            "public async Task LoadAsync() => await Task.CompletedTask.ConfigureAwait(" + options + "); }";
        using var workspace = new AdhocWorkspace();
        Document document = DocumentFor(workspace, source);
        var compilation = (await document.Project.GetCompilationAsync())!;
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var diagnostic = Assert.Single(await AnalyzeAsync(document), d => d.Id == "CUI005");
        var actions = new List<CodeAction>();
        await new ConfigureAwaitCodeFixProvider().RegisterCodeFixesAsync(
            new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None));
        Assert.Empty(actions);
    }

    [Fact]
    public async Task AGuardThatWouldRemoveTheOnlyReturnIsNotOffered()
    {
        string source = Prologue + "public static class Probe { public static bool Open; public static int Count(Ui ui) { " +
            "using var dialog = ui.Dialog(\"details\", ref Open); return 7; } }";
        using var workspace = new AdhocWorkspace();
        Document document = DocumentFor(workspace, source);
        var diagnostic = Assert.Single((await AnalyzeAsync(document)).Where(d => d.Id == "CUI003"));
        var actions = new List<CodeAction>();
        await new ScopeCodeFixProvider().RegisterCodeFixesAsync(
            new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None));
        Assert.Empty(actions);
    }

    private static async Task<CSharpCompilation> FixAsync(string source, string id)
    {
        using var workspace = new AdhocWorkspace();
        Document document = DocumentFor(workspace, source);
        var compilation = (await document.Project.GetCompilationAsync())!;
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var diagnostic = (await AnalyzeAsync(document)).First(d => d.Id == id);
        CodeFixProvider provider = id == "CUI005" ? new ConfigureAwaitCodeFixProvider() : new ScopeCodeFixProvider();
        var actions = new List<CodeAction>();
        await provider.RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None));
        CodeAction action = Assert.Single(actions);
        var operations = await action.GetOperationsAsync(CancellationToken.None);
        var change = Assert.Single(operations.OfType<ApplyChangesOperation>());
        Document fixedDocument = change.ChangedSolution.GetDocument(document.Id)!;
        fixedDocument = await Formatter.FormatAsync(fixedDocument);
        var fixedCompilation = (CSharpCompilation)(await fixedDocument.Project.GetCompilationAsync())!;
        Assert.Empty(fixedCompilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.DoesNotContain(await AnalyzeAsync(fixedDocument), d => d.Id is "CUI002" or "CUI003" or "CUI005");
        return fixedCompilation;
    }

    private static Document DocumentFor(AdhocWorkspace workspace, string source)
    {
        var project = workspace.AddProject("CodeFixProbe" + Guid.NewGuid().ToString("N"), LanguageNames.CSharp)
            .WithParseOptions(new CSharpParseOptions(LanguageVersion.CSharp14))
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithMetadataReferences(AnalyzerTests.References);
        return project.AddDocument("Probe.cs", source);
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(Document document) =>
        await (await document.Project.GetCompilationAsync())!.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new ScopeAnalyzer(), new FrameAnalyzer(), new WidgetIdAnalyzer())).GetAnalyzerDiagnosticsAsync();

    private static Assembly Emit(CSharpCompilation compilation)
    {
        using var image = new MemoryStream();
        var result = compilation.Emit(image);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return Assembly.Load(image.ToArray());
    }

    private static DrawFn DrawFrom(CSharpCompilation compilation) => Emit(compilation).GetType("Probe")!.GetMethod("Draw")!.CreateDelegate<DrawFn>();
}
