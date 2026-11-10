using System.Collections.Immutable;
using System.Composition;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CuiSharp.CodeFixes;

/// <summary>
/// Gives a repeated widget its own id space, by wrapping the statement in <c>using (ui.Id(...))</c>
/// with the loop's own key. The literal id stays as written; nesting it under the key is what
/// makes each row keep its own hover, focus and text state.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(WidgetIdCodeFixProvider)), Shared]
public sealed class WidgetIdCodeFixProvider : CodeFixProvider
{
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => ["CUI004"];

    // Each fix wraps the statement it was raised on; merging two of them into one block would
    // have to invent a nesting order, so offer them one at a time.
    /// <inheritdoc />
    public override FixAllProvider? GetFixAllProvider() => null;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;
        var literal = root.FindNode(context.Span, getInnermostNodeForTie: true).FirstAncestorOrSelf<LiteralExpressionSyntax>();
        if (literal is null) return;
        StatementSyntax? statement = literal.FirstAncestorOrSelf<StatementSyntax>();
        if (statement is null || statement.Parent is not BlockSyntax block) return;

        // A key that is the same on every iteration would leave the rows colliding, so only a loop
        // that actually has a per-row key offers the fix. `while` has none, and is left alone.
        string? key = KeyOf(literal);
        if (key is null) return;

        SyntaxNode replacement = block.ReplaceNode(statement, Scoped(statement, key));
        Document changed = context.Document.WithSyntaxRoot(root.ReplaceNode(block, replacement.WithAdditionalAnnotations(Formatter.Annotation)));
        SemanticModel? before = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        SemanticModel? after = await changed.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (before is null || after is null) return;
        var oldErrors = new HashSet<string>(before.GetDiagnostics(cancellationToken: context.CancellationToken)
            .Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Id + d.GetMessage()), StringComparer.Ordinal);
        if (after.GetDiagnostics(cancellationToken: context.CancellationToken).Any(d =>
            d.Severity == DiagnosticSeverity.Error && !oldErrors.Contains(d.Id + d.GetMessage()))) return;

        context.RegisterCodeFix(CodeAction.Create($"Key this widget with '{key}'",
            _ => Task.FromResult(changed), equivalenceKey: "CUI004"), context.Diagnostics);
    }

    /// <summary>The innermost loop's per-iteration key, or null when the loop does not have one.</summary>
    private static string? KeyOf(SyntaxNode node)
    {
        foreach (SyntaxNode ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case ForEachStatementSyntax foreachStatement:
                    return foreachStatement.Identifier.ValueText;
                case ForStatementSyntax { Declaration.Variables.Count: 1 } forStatement:
                    return forStatement.Declaration!.Variables[0].Identifier.ValueText;
                case ForStatementSyntax forStatement:
                    // `for (i = 0; ...)` with the counter declared elsewhere: still one key per row.
                    return forStatement.Condition is BinaryExpressionSyntax condition &&
                           condition.Left is IdentifierNameSyntax counter ? counter.Identifier.ValueText : null;
                case WhileStatementSyntax or DoStatementSyntax:
                    return null;
            }
        }

        return null;
    }

    private static StatementSyntax Scoped(StatementSyntax statement, string key)
    {
        // The wrap goes around the whole statement, so any scope the widget itself opens stays
        // inside it.
        return SyntaxFactory.UsingStatement(
            declaration: null,
            expression: SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("ui"), SyntaxFactory.IdentifierName("Id")),
                SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName(key))))),
            statement: SyntaxFactory.Block(statement))
            .WithTriviaFrom(statement);
    }
}
