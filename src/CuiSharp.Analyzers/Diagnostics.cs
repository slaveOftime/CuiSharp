using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CuiSharp.Analyzers;

/// <summary>
/// The one place the analyzer ids, categories and messages live, so a rule and its
/// documentation cannot drift. Every message says what to do, in one sentence, because the
/// reader is often a build log an assistant is trying to fix.
/// </summary>
internal static class Diagnostics
{
    internal const string Category = "CuiSharp";

    /// <summary>A blocking call inside a frame: it freezes the window.</summary>
    internal static readonly DiagnosticDescriptor BlockingCall = new(
        id: "CUI001",
        title: "Do not block inside a frame",
        messageFormat: "'{0}' blocks the UI thread inside a frame; run the work in a Mutation or Query instead, so the frame keeps drawing",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A frame builds the whole window. Blocking in a Component's Draw or in any method that takes a Ui stalls input, paint and animation.");

    /// <summary>A scope-returning call whose result is thrown away.</summary>
    internal static readonly DiagnosticDescriptor ScopeNotDisposed = new(
        id: "CUI002",
        title: "Scope results belong in a using statement",
        messageFormat: "'{0}' returns a scope; put it in a 'using' statement or declaration, so CUI's stack is popped even when the body throws",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Containers open a native scope that has to be closed exactly once.");

    /// <summary>An OpenScope whose Open flag is never checked.</summary>
    internal static readonly DiagnosticDescriptor OpenScopeNotChecked = new(
        id: "CUI003",
        title: "Check Open before drawing into a conditional container",
        messageFormat: "'{0}' returns an OpenScope that this code never checks; guard the body with 'if ({1})' so it only draws when the container is open",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A Popover, Sheet, Dialog or Menu lays out its body only when it opens; drawing unconditionally builds a widget nothing shows.");

    /// <summary>The same literal widget id in a loop: the rows share one state.</summary>
    internal static readonly DiagnosticDescriptor ConstantIdInLoop = new(
        id: "CUI004",
        title: "Give every repeated widget its own id",
        messageFormat: "A constant id '{0}' inside a loop gives every row the same widget state; wrap the body in 'using (ui.Id(index))' or use 'ui.Keyed(items, key)'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Ids key hover, focus, scroll and text buffers; reusing one in a loop makes the rows fight over a single state.");

    /// <summary>ConfigureAwait makes no sense when the continuation must be on the UI thread.</summary>
    internal static readonly DiagnosticDescriptor ConfigureAwaitInComponent = new(
        id: "CUI005",
        title: "Do not leave the UI thread inside a component",
        messageFormat: "Remove 'ConfigureAwait(false)' from this await: a component's continuation has to resume on the UI thread to touch component state",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Components resume on CUI's synchronization context, which is what makes 'state changed, then redraw' safe without locking.");

    /// <summary>async void: nothing can observe the failure and nothing tracks the work.</summary>
    internal static readonly DiagnosticDescriptor AsyncVoidInComponent = new(
        id: "CUI006",
        title: "Use a Mutation instead of async void",
        messageFormat: "'{0}' is 'async void'; return Task and run it through a Mutation, so the frame knows work is pending and errors surface in the page",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "An async void method cannot be awaited, cannot report its exception and never tells the host to keep drawing.");

    /// <summary>An allocation in a method that runs every frame.</summary>
    internal static readonly DiagnosticDescriptor AllocationInFrame = new(
        id: "CUI007",
        title: "Avoid allocating in a frame",
        messageFormat: "This {0} allocates once per frame; hoist it into a field or draw it with a widget that takes a span, so the frame stays at 0 B",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "The frame loop runs sixty times a second; anything allocated in it becomes garbage at that rate. Reported as info because a one-off allocation at startup is fine.");

    /// <summary>
    /// Reflection-based serialization under Native AOT: the types it needs are trimmed away, so the
    /// app publishes cleanly and fails at run time on the machine that cannot run a debugger.
    /// </summary>
    internal static readonly DiagnosticDescriptor ReflectionUnderAot = new(
        id: "CUI008",
        title: "Reflection cannot see trimmed metadata",
        messageFormat: "'{0}' reflects over types at run time, which Native AOT trims away: use the source-generated JsonSerializerContext, a JsonTypeInfo, or read the value directly",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "CuiSharp is built for NativeAOT: an app that serializes with reflection publishes with IL2026/IL3050 warnings at best, and fails at run time when the metadata is gone.");
}

/// <summary>Facts about the CuiSharp types the rules match on, resolved once per compilation.</summary>
internal sealed class KnownTypes
{
    internal KnownTypes(Compilation compilation)
    {
        Ui = compilation.GetTypeByMetadataName("CuiSharp.Ui");
        Component = compilation.GetTypeByMetadataName("CuiSharp.Hosting.Component");
        LayoutScope = compilation.GetTypeByMetadataName("CuiSharp.LayoutScope");
        OpenScope = compilation.GetTypeByMetadataName("CuiSharp.OpenScope");
        IdScope = compilation.GetTypeByMetadataName("CuiSharp.IdScope");
        UiText = compilation.GetTypeByMetadataName("CuiSharp.UiText");
        KeyedScopeDefinition = compilation.GetTypeByMetadataName("CuiSharp.KeyedScope`1");
    }

    internal INamedTypeSymbol? Ui { get; }
    internal INamedTypeSymbol? Component { get; }
    internal INamedTypeSymbol? LayoutScope { get; }
    internal INamedTypeSymbol? OpenScope { get; }
    internal INamedTypeSymbol? IdScope { get; }
    internal INamedTypeSymbol? UiText { get; }
    internal INamedTypeSymbol? KeyedScopeDefinition { get; }

    /// <summary>True when the type is one the caller has to dispose: a scope the frame stack owns.</summary>
    internal bool IsScope(ITypeSymbol? type) => type is not null && (
        SymbolEquals(type, LayoutScope) || SymbolEquals(type, OpenScope) || SymbolEquals(type, IdScope) ||
        (KeyedScopeDefinition is not null && type is INamedTypeSymbol { IsGenericType: true } named &&
         SymbolEquals(named.ConstructedFrom, KeyedScopeDefinition)));

    /// <summary>True for the scope kinds whose body only makes sense when they opened.</summary>
    internal bool IsConditionalScope(ITypeSymbol? type) => SymbolEquals(type, OpenScope);

    internal bool IsUiMethod(IMethodSymbol method) =>
        method.ContainingType is not null && SymbolEquals(method.ContainingType, Ui) && method.IsExtensionMethod;

    /// <summary>True for the frame-local <c>Ui</c> value itself.</summary>
    internal bool IsUi(ITypeSymbol type) => SymbolEquals(type, Ui);

    /// <summary>True inside a component, which is what the hosting rules are about.</summary>
    internal bool IsComponent(INamedTypeSymbol? type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEquals(current, Component)) return true;
        }

        return false;
    }

    private static bool SymbolEquals(ITypeSymbol? symbol, INamedTypeSymbol? expected) =>
        symbol is not null && expected is not null && SymbolEqualityComparer.Default.Equals(
            symbol is INamedTypeSymbol named ? named.OriginalDefinition : symbol, expected.OriginalDefinition);
}

/// <summary>Small syntax helpers shared by the rules.</summary>
internal static class SyntaxHelpers
{
    /// <summary>True when this invocation's result is thrown away by an expression statement.</summary>
    internal static bool ResultDiscarded(SyntaxNode invocation)
    {
        var parent = invocation.Parent;
        // ui.Button("x");        -> discarded
        // var s = using ...;     -> kept
        return parent is Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionStatementSyntax;
    }

    /// <summary>True when the node sits inside a loop body, at any depth.</summary>
    internal static bool InLoop(SyntaxNode node) =>
        node.Ancestors().Any(a => a is
            Microsoft.CodeAnalysis.CSharp.Syntax.ForStatementSyntax or
            Microsoft.CodeAnalysis.CSharp.Syntax.ForEachStatementSyntax or
            Microsoft.CodeAnalysis.CSharp.Syntax.WhileStatementSyntax or
            Microsoft.CodeAnalysis.CSharp.Syntax.DoStatementSyntax);
}
