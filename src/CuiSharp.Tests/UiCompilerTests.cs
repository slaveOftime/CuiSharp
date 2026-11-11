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

    /// <summary>
    /// Issue #10: the old CuiSharp.TaskStatus made an unqualified TaskStatus ambiguous with
    /// System.Threading.Tasks.TaskStatus for every consumer that used both namespaces. Renamed
    /// CuiTaskStatus, both resolve unqualified in one consumer file; restoring the old name
    /// would make this snippet fail with CS0104 before either member could even be read.
    /// </summary>
    [Fact]
    public void BothTaskStatusTypesResolveUnqualifiedInAConsumer()
    {
        // An unrelated namespace on purpose: inside CuiSharp's own hierarchy the enclosing
        // namespace finds CuiSharp.TaskStatus before any using, so it hides the ambiguity
        // instead of reporting it - a consumer is where the collision was observable.
        var errors = Compile("""
            namespace Consumer
            {
                class Page
                {
                    void Draw(Ui ui)
                    {
                        var bcl = TaskStatus.Canceled;
                        ui.AgentStatus("working", new AgentStatusOpts { Status = CuiTaskStatus.Running });
                        ui.AgentPlan("plan", [new PlanStep("read", null, CuiTaskStatus.Done)]);
                        if (bcl == TaskStatus.RanToCompletion) ui.Text("done");
                    }
                }
            }
            """);
        Assert.Empty(errors);
    }

    /// <summary>
    /// The published guide's own snippets, compiled. A reader pastes them, so a wrong shape in
    /// SKILL.md is a build error in someone else's project — and nothing else in the suite reads
    /// the examples as code.
    /// </summary>
    [Fact]
    public void TheGuidesExamplesCompile()
    {
        string guide = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SKILL.md"));

        // The options example: a struct of fields, filled with an object initializer.
        string options = Snippet(guide, "ButtonOpts { Variant = ButtonVariant.Primary, Icon = Icons.Check }");
        Assert.Empty(Compile($"class Page {{ void Draw(Ui ui) {{ {options} }} }}"));

        // The input example: a typed result, not the C flag word.
        string input = Snippet(guide, "InputResult result = ui.Input");
        string page =
            "class Page" + (char)10 +
            "{" + (char)10 +
            "    private string _name = " + (char)34 + "Maya Okafor" + (char)34 + ";" + (char)10 +
            "    private static void Save(string value) { }" + (char)10 +
            "    void Draw(Ui ui)" + (char)10 +
            "    {" + (char)10 +
            input + (char)10 +
            "    }" + (char)10 +
            "}";
        Assert.Empty(Compile(page));

        // The custom control: an extension member on Ui, compiled as the guide shows it.
        string custom = Fence(guide, "public void Spark(float[] values)");
        Assert.Empty(Compile(custom));
    }

    /// <summary>The guide's lines from <paramref name="marker"/> to the end of its fenced block.</summary>
    private static string Snippet(string guide, string marker)
    {
        string[] lines = Lines(guide);
        int found = Where(lines, marker);
        return string.Join("\n", lines[found..Closing(lines, found)]);
    }

    /// <summary>
    /// The declaration in the fenced block that contains <paramref name="marker"/>: the block ends
    /// with a call that uses the declaration, which is not part of what it declares.
    /// </summary>
    private static string Fence(string guide, string marker)
    {
        string[] lines = Lines(guide);
        int found = Where(lines, marker);
        int start = found;
        while (start > 0 && !IsFence(lines[start])) start--;

        string[] block = lines[(start + 1)..Closing(lines, found)];
        int last = Array.FindLastIndex(block, line => line.Trim() == "}");
        if (last >= 0) block = block[..(last + 1)];

        return string.Join("\n", block);
    }

    private static string[] Lines(string guide) => guide.ReplaceLineEndings("\n").Split("\n");

    private static bool IsFence(string line) => line.TrimStart().StartsWith("```", StringComparison.Ordinal);

    private static int Where(string[] lines, string marker)
    {
        int found = Array.FindIndex(lines, line => line.Contains(marker, StringComparison.Ordinal));
        Assert.True(found >= 0, $"the guide no longer contains `{marker}`");
        return found;
    }

    private static int Closing(string[] lines, int from)
    {
        int end = from;
        while (end < lines.Length && !IsFence(lines[end])) end++;
        return end;
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
