using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CuiSharp.Tests;

/// <summary>Compile actual consumer snippets: success alone cannot prove the safety contract.</summary>
public class UiCompilerTests
{
    private static readonly MetadataReference[] References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Append(typeof(Ui).Assembly.Location).Distinct()
        .Select(path => MetadataReference.CreateFromFile(path)).ToArray();

    [Theory]
    [InlineData("Ui")]
    [InlineData("LayoutScope")]
    [InlineData("OpenScope")]
    [InlineData("UiText")]
    public void StackOnlyValuesCannotBeFieldsOrBoxed(string type)
    {
        AssertError($"class Bad {{ {type} saved; }}", "CS8345");
        // OpenScope intentionally converts to bool; an object target can box
        // that bool, not the scope. An interface target checks actual scope boxing.
        string target = type == "OpenScope" ? "IDisposable" : "object";
        AssertError($"class Bad {{ {target} Box({type} value) => value; }}", "CS0029");
    }

    [Theory]
    [InlineData("Ui")]
    [InlineData("LayoutScope")]
    [InlineData("OpenScope")]
    [InlineData("UiText")]
    public void StackOnlyValuesCannotBeCapturedOrSurviveAwait(string type)
    {
        AssertError($$"""
            class Bad
            {
                Action Capture({{type}} value) => () => Consume(value);
                static void Consume({{type}} value) { }
            }
            """, "CS9108");
        AssertError($$"""
            class Bad
            {
                static {{type}} Create() => default;
                static void Consume({{type}} value) { }
                async Task DrawAsync()
                {
                    var value = Create();
                    await Task.Yield();
                    Consume(value);
                }
            }
            """, "CS4007");
    }

    [Fact]
    public void SupportedDrawingShapesCompileWithCSharp14()
    {
        var errors = Compile("""
            class Good
            {
                int count;
                void Frame(Ui ui)
                {
                    using (ui.Column())
                    {
                        ui.Text("plain", new TextOpts { Font = Font.Mono, NoWrap = true });
                        string[] items = ["one", "two"];
                        ui.Segmented("segments", items, ref count);
                        ui.Tabs("tabs", items, ref count, new TabsOpts { Variant = TabsVariant.Pill });
                        ui.Text("UTF-8"u8);
                        ui.Text($"count {count,6:D3}");
                        ui.Metric($"count {count}", $"{count:F2}");
                        if (ui.Button($"Add {count}")) count++;
                        using var scope = ui.Menu("Options");
                        if (scope) ui.MenuItem("Save");
                        ui.Menu("Other", static body => body.MenuItem("Save"));
                        ui.Canvas("chart", Size.Fill, Size.Px(40),
                            static (paint, rect) => paint.DrawText($"{rect.W:F1}", rect.X, rect.Y, Color.Accent));
                        string draft = "";
                        if (ui.Input("draft", ref draft).Changed) count++;
                        using (ui.Id(count)) ui.Text(draft);
                        foreach (string item in ui.Keyed(items, static (in string s) => s.Length))
                            ui.Text(item);
                    }
                }
                void Run() => CuiApp.Run(new(), Frame);
            }
            static class Extensions
            {
                extension(Ui ui)
                {
                    public void Custom(UiText text) => ui.Text(text);
                }
            }
            """);
        Assert.Empty(errors);
    }

    [Fact]
    public void GeneratedOptionsRejectWrongTypes()
    {
        AssertError("class Bad { void Draw(Ui ui) => ui.Text(\"x\", new TextOpts { Font = 1 }); }", "CS0266");
        AssertError("class Bad { void Draw(Ui ui) => ui.Button(\"x\", new ChartOpts()); }", "CS1503");
    }

    private static void AssertError(string source, string id)
    {
        var errors = Compile(source);
        Assert.Contains(errors, error => error.Id == id);
    }

    private static Diagnostic[] Compile(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(
            "using System; using System.Threading.Tasks; using CuiSharp;\n" + source,
            new CSharpParseOptions(LanguageVersion.CSharp14));
        var compilation = CSharpCompilation.Create("Consumer", [tree], References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        return compilation.Emit(output).Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
    }
}
