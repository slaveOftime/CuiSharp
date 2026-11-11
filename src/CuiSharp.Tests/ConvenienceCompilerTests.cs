using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CuiSharp.Tests;

/// <summary>
/// Issue #9 chose option constructors for the concise authoring shapes. These snippets compile
/// consumer code with the real compiler: the concise shapes and the existing call shapes,
/// together with the symbol each one binds to, so a change that quietly redirects an existing
/// call to another member is a red test rather than a silent break.
/// </summary>
public class ConvenienceCompilerTests
{
    private static readonly MetadataReference[] References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Append(typeof(Ui).Assembly.Location).Distinct()
        .Select(path => MetadataReference.CreateFromFile(path)).ToArray();

    [Fact]
    public void TheConciseAuthoringShapesCompile()
    {
        AssertNoErrors("""
            class Page
            {
                void Draw(Ui ui)
                {
                    using (ui.Row(new(gap: 12, align: Align.Center))) { ui.Button("Cancel"); ui.Button("Save"); }
                    using (ui.Column(new(pad: 18, gap: 12, height: Size.Fill))) ui.Text("note");
                    ui.Text("Live", new(size: 12, color: Color.TextSecondary));
                    ui.Badge("Updating", new(Tone.Danger));
                    using (ui.Card(new(pad: 16, gap: 12, dir: Dir.Column))) ui.Text("body");
                }
            }
            """);
    }

    [Fact]
    public void TheConciseArgumentsBindToTheOptionConstructors()
    {
        AssertConstructorBinding("Box", ["Single", "Single", "Align", "Size", "Size"],
            "void Draw(Ui ui) { using (ui.Row(new(gap: 12, align: Align.Center))) ui.Button(\"a\"); }");
        AssertConstructorBinding("Box", ["Single", "Single", "Align", "Size", "Size"],
            "void Draw(Ui ui) { using (ui.Column(new(pad: 18, gap: 12, height: Size.Fill))) ui.Text(\"a\"); }");
        AssertConstructorBinding("Card", ["Single", "Single", "Dir", "Size", "Size"],
            "void Draw(Ui ui) { using (ui.Card(new(pad: 16, gap: 12, dir: Dir.Column))) ui.Text(\"a\"); }");
        AssertConstructorBinding("TextOpts", ["Single", "Color"],
            "void Draw(Ui ui) => ui.Text(\"Live\", new(size: 12, color: Color.TextSecondary));");
        AssertConstructorBinding("BadgeOpts", ["Tone"],
            "void Draw(Ui ui) => ui.Badge(\"Updating\", new(Tone.Danger));");
    }

    [Fact]
    public void EveryExistingCallShapeStillBindsToTheGeneratedWrapper()
    {
        AssertCallBinding("void Draw(Ui ui) { ui.Row(); }", ["Box"]);
        AssertCallBinding("void Draw(Ui ui) { ui.Row(default); }", ["Box"]);
        AssertCallBinding("void Draw(Ui ui) { ui.Row(new()); }", ["Box"]);
        AssertCallBinding("void Draw(Ui ui) { ui.Row(new Box { Gap = 4 }); }", ["Box"]);
        AssertCallBinding("void Draw(Ui ui) { ui.Column(); }", ["Box"]);
        AssertCallBinding("void Draw(Ui ui) { ui.Card(new()); }", ["Card"]);
        AssertCallBinding("void Draw(Ui ui) { ui.Text(\"x\"); }", ["UiText", "TextOpts"]);
        AssertCallBinding("void Draw(Ui ui) { ui.Text(\"x\", default); }", ["UiText", "TextOpts"]);
        AssertCallBinding("void Draw(Ui ui) { ui.Badge(\"x\", default); }", ["UiText", "BadgeOpts"]);
        AssertCallBinding("void Draw(Ui ui) { ui.Badge(\"x\", new BadgeOpts()); }", ["UiText", "BadgeOpts"]);
    }

    [Fact]
    public void TheOptionsKeepTheirObjectInitializerApi()
    {
        AssertNoErrors("""
            class Page
            {
                void Draw(Ui ui)
                {
                    using (ui.Row(new Box { Gap = 12, Id = "r" })) ui.Text("a");
                    using (ui.Card(new Card { Pad = 16, Hoverable = true })) ui.Text("b");
                    ui.Text("c", new TextOpts { Size = 12, NoWrap = true });
                    ui.Badge("d", new BadgeOpts { Dot = true });
                }
            }
            """);
    }

    private static void AssertNoErrors(string source) => Assert.Empty(Errors(source));

    /// <summary>The single invocation in the snippet binds to a method with exactly these parameter types.</summary>
    private static void AssertCallBinding(string member, string[] parameterTypes)
    {
        var (compilation, tree) = Compile("class Page { " + member + " }");
        Assert.Empty(Errors(compilation));
        var node = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var symbol = (IMethodSymbol?)compilation.GetSemanticModel(tree).GetSymbolInfo(node).Symbol;
        Assert.NotNull(symbol);
        // A receiver-form extension call binds to the reduced form: the Ui receiver is the instance.
        Assert.Equal(parameterTypes, symbol!.Parameters.Select(p => p.Type.Name).ToArray());
    }

    /// <summary>The snippet's new(...) binds to a constructor of this type with exactly these parameter types.</summary>
    private static void AssertConstructorBinding(string owner, string[] parameterTypes, string member)
    {
        var (compilation, tree) = Compile("class Page { " + member + " }");
        Assert.Empty(Errors(compilation));
        var node = tree.GetRoot().DescendantNodes().OfType<ImplicitObjectCreationExpressionSyntax>().Single();
        var symbol = (IMethodSymbol?)compilation.GetSemanticModel(tree).GetSymbolInfo(node).Symbol;
        Assert.NotNull(symbol);
        Assert.Equal(owner, symbol!.ContainingType.Name);
        Assert.Equal(parameterTypes, symbol.Parameters.Select(p => p.Type.Name).ToArray());
    }

    private static Diagnostic[] Errors(string source) => Errors(Compile(source).compilation);

    private static Diagnostic[] Errors(Compilation compilation)
    {
        using var output = new MemoryStream();
        return compilation.Emit(output).Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
    }

    private static (Compilation compilation, SyntaxTree tree) Compile(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(
            "using System; using CuiSharp;\n" + source,
            new CSharpParseOptions(LanguageVersion.CSharp14));
        return (CSharpCompilation.Create("Consumer", [tree], References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)), tree);
    }
}
