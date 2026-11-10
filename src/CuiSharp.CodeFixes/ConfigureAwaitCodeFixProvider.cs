using System.Collections.Immutable;
using System.Composition;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CuiSharp.CodeFixes;

/// <summary>Removes ConfigureAwait from a component's continuation without changing the awaited work.</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ConfigureAwaitCodeFixProvider)), Shared]
public sealed class ConfigureAwaitCodeFixProvider : CodeFixProvider
{
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => ["CUI005"];

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;
        var invocation = root.FindNode(context.Span, getInnermostNodeForTie: true).FirstAncestorOrSelf<InvocationExpressionSyntax>();
        if (invocation?.Expression is not MemberAccessExpressionSyntax member) return;
        // A look-alike user method may have different return semantics. Only rewrite the BCL
        // awaitables, and only the awaited form: a configured-awaitable local has another type.
        if (invocation.Parent is not AwaitExpressionSyntax) return;
        SemanticModel? model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (model?.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method ||
            method.ContainingNamespace.ToDisplayString() != "System.Threading.Tasks" ||
            method.ContainingType.Name is not ("Task" or "ValueTask") ||
            method.Parameters.Length != 1 || method.Parameters[0].Type.SpecialType != SpecialType.System_Boolean) return;
        // ConfigureAwaitOptions can suppress exceptions; removing that overload changes the work's
        // outcome, not just continuation scheduling. Only the bool overload is safe to remove.

        context.RegisterCodeFix(CodeAction.Create("Remove ConfigureAwait from the await", _ =>
            Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(invocation, member.Expression.WithTriviaFrom(invocation)))),
            equivalenceKey: "CUI005"), context.Diagnostics);
    }
}
