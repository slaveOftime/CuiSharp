using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CuiSharp.CodeFixes;

/// <summary>
/// Gives a repeated widget its own id space, using an unambiguous loop key and the widget's
/// receiver. A using declaration's children stay inside both the container and the id scope.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(WidgetIdCodeFixProvider)), Shared]
public sealed class WidgetIdCodeFixProvider : CodeFixProvider
{
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => ["CUI004"];

    // Body edits can overlap, so offer them one at a time until sequential fix-all is supported.
    /// <inheritdoc />
    public override FixAllProvider? GetFixAllProvider() => null;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;
        var literal = root.FindNode(context.Span, getInnermostNodeForTie: true).FirstAncestorOrSelf<LiteralExpressionSyntax>();
        var invocation = literal?.FirstAncestorOrSelf<InvocationExpressionSyntax>();
        // Evaluating an arbitrary receiver again could have side effects. Locals and parameters
        // are safe, and preserve the actual Ui rather than assuming its name is "ui".
        if (invocation?.Expression is not MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax receiver }) return;
        StatementSyntax? statement = literal!.FirstAncestorOrSelf<StatementSyntax>();
        if (statement is null || statement.Parent is not BlockSyntax block) return;
        SemanticModel? before = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (before is null || before.GetSymbolInfo(receiver, context.CancellationToken).Symbol is not (ILocalSymbol or IParameterSymbol)) return;

        SyntaxToken? key = KeyOf(literal, before, context.CancellationToken);
        if (key is null) return;

        BlockSyntax replacement;
        if (statement is LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 })
        {
            // A using declaration owns the rest of its block. Wrapping only the declaration
            // disposes the container before its children draw, even though that compiles.
            int index = block.Statements.IndexOf(statement);
            var scoped = Scoped(block.Statements.Skip(index), receiver, key.Value).WithTriviaFrom(statement);
            replacement = block.WithStatements(SyntaxFactory.List(block.Statements.Take(index).Append(scoped)));
        }
        else
        {
            replacement = block.ReplaceNode(statement, Scoped([statement], receiver, key.Value).WithTriviaFrom(statement));
        }

        Document changed = context.Document.WithSyntaxRoot(root.ReplaceNode(block, replacement.WithAdditionalAnnotations(Formatter.Annotation)));
        SemanticModel? after = await changed.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (after is null) return;
        var oldErrors = new HashSet<string>(before.GetDiagnostics(cancellationToken: context.CancellationToken)
            .Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Id + d.GetMessage()), StringComparer.Ordinal);
        if (after.GetDiagnostics(cancellationToken: context.CancellationToken).Any(d =>
            d.Severity == DiagnosticSeverity.Error && !oldErrors.Contains(d.Id + d.GetMessage()))) return;

        context.RegisterCodeFix(CodeAction.Create($"Key this widget with '{key.Value.ValueText}'",
            _ => Task.FromResult(changed), equivalenceKey: "CUI004"), context.Diagnostics);
    }

    private static SyntaxToken? KeyOf(SyntaxNode node, SemanticModel model, CancellationToken token)
    {
        var loops = node.Ancestors().Where(a => a is ForStatementSyntax or ForEachStatementSyntax or
            ForEachVariableStatementSyntax or WhileStatementSyntax or DoStatementSyntax).ToArray();
        // The inner index repeats across outer iterations. Do not pretend it fixes the whole
        // grid; nested loops need an explicit composite key or per-level id scopes.
        if (loops.Length != 1) return null;
        if (loops[0] is ForEachStatementSyntax each) return each.Identifier;
        if (loops[0] is not ForStatementSyntax loop ||
            loop.Condition is not BinaryExpressionSyntax condition ||
            condition.Kind() is not (SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression or
                SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression) ||
            condition.Left is not IdentifierNameSyntax counter || loop.Incrementors.Count != 1) return null;

        ExpressionSyntax? incremented = loop.Incrementors[0] switch
        {
            PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.PostIncrementExpression) ||
                postfix.IsKind(SyntaxKind.PostDecrementExpression) => postfix.Operand,
            PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.PreIncrementExpression) ||
                prefix.IsKind(SyntaxKind.PreDecrementExpression) => prefix.Operand,
            _ => null,
        };
        ISymbol? symbol = model.GetSymbolInfo(counter, token).Symbol;
        if (symbol is not ILocalSymbol { Type.SpecialType: SpecialType.System_Int32 } || incremented is null ||
            !SymbolEqualityComparer.Default.Equals(symbol, model.GetSymbolInfo(incremented, token).Symbol)) return null;
        if (loop.Declaration is { } declaration && (declaration.Variables.Count != 1 ||
            !SymbolEqualityComparer.Default.Equals(symbol, model.GetDeclaredSymbol(declaration.Variables[0], token)))) return null;
        return counter.Identifier;
    }

    private static StatementSyntax Scoped(IEnumerable<StatementSyntax> statements, IdentifierNameSyntax receiver, SyntaxToken key) =>
        SyntaxFactory.UsingStatement(
            declaration: null,
            expression: SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                    receiver.WithoutTrivia(), SyntaxFactory.IdentifierName("Id")),
                SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName(key.WithoutTrivia()))))),
            statement: SyntaxFactory.Block(statements));
}
