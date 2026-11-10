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

/// <summary>Closes leaked scopes and guards the body of a conditional container.</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ScopeCodeFixProvider)), Shared]
public sealed class ScopeCodeFixProvider : CodeFixProvider
{
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => ["CUI002", "CUI003"];

    // These edits can own the rest of a block. Batch merging overlapping scope edits would lose
    // a sibling's changes, so offer individual fixes until a sequential fix-all is implemented.
    /// <inheritdoc />
    public override FixAllProvider? GetFixAllProvider() => null;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;
        var statement = root.FindNode(context.Span, getInnermostNodeForTie: true).FirstAncestorOrSelf<StatementSyntax>();
        if (statement is null) return;
        Diagnostic diagnostic = context.Diagnostics[0];
        bool dispose = diagnostic.Id == "CUI002";
        if (dispose && statement is not (LocalDeclarationStatementSyntax or ExpressionStatementSyntax)) return;
        if (!dispose && statement is not (LocalDeclarationStatementSyntax or UsingStatementSyntax)) return;
        if (statement is not UsingStatementSyntax && statement.Parent is not BlockSyntax) return;
        VariableDeclarationSyntax? variables = statement switch
        {
            LocalDeclarationStatementSyntax local => local.Declaration,
            UsingStatementSyntax scoped => scoped.Declaration,
            _ => null,
        };
        // The analyzer currently diagnoses single initializers only. Keep the provider bounded
        // too, including while the editor is updating a declaration under an existing diagnostic.
        if (statement is not ExpressionStatementSyntax &&
            (variables?.Variables.Count != 1 || variables.Variables[0].Initializer is null)) return;

        Document changed = await FixAsync(context.Document, statement, dispose, context.CancellationToken).ConfigureAwait(false);
        SemanticModel? before = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        SemanticModel? after = await changed.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (before is null || after is null) return;
        var oldErrors = new HashSet<string>(before.GetDiagnostics(cancellationToken: context.CancellationToken)
            .Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Id + d.GetMessage()), StringComparer.Ordinal);
        // Adding a guard can change definite assignment or leave a value-returning method without
        // a return. Do not offer a rewrite that creates a compiler error the user must repair.
        if (after.GetDiagnostics(cancellationToken: context.CancellationToken).Any(d =>
            d.Severity == DiagnosticSeverity.Error && !oldErrors.Contains(d.Id + d.GetMessage()))) return;

        context.RegisterCodeFix(CodeAction.Create(
            dispose ? "Dispose the scope with using" : "Guard the conditional scope's body",
            _ => Task.FromResult(changed), equivalenceKey: diagnostic.Id), diagnostic);
    }

    private static async Task<Document> FixAsync(Document document, StatementSyntax statement, bool dispose, CancellationToken token)
    {
        SyntaxNode root = (await document.GetSyntaxRootAsync(token).ConfigureAwait(false))!;
        SemanticModel model = (await document.GetSemanticModelAsync(token).ConfigureAwait(false))!;
        StatementSyntax replacement;
        if (statement is UsingStatementSyntax usingStatement)
        {
            string name = usingStatement.Declaration!.Variables[0].Identifier.Text;
            replacement = usingStatement.WithStatement(Guard(name, AsBlock(usingStatement.Statement)));
            return document.WithSyntaxRoot(root.ReplaceNode(statement, replacement.WithAdditionalAnnotations(Formatter.Annotation)));
        }

        var block = (BlockSyntax)statement.Parent!;
        int index = block.Statements.IndexOf(statement);
        string scopeName;
        LocalDeclarationStatementSyntax declaration;
        ITypeSymbol? scopeType;
        if (statement is LocalDeclarationStatementSyntax local)
        {
            var variable = local.Declaration.Variables[0];
            scopeName = variable.Identifier.Text;
            scopeType = model.GetTypeInfo(variable.Initializer!.Value, token).Type;
            declaration = local.WithUsingKeyword(SyntaxFactory.Token(SyntaxKind.UsingKeyword));
        }
        else
        {
            var expression = ((ExpressionStatementSyntax)statement).Expression;
            scopeType = model.GetTypeInfo(expression, token).Type;
            scopeName = FreshName(block, model, statement.SpanStart);
            declaration = SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                    .AddVariables(SyntaxFactory.VariableDeclarator(scopeName)
                        .WithInitializer(SyntaxFactory.EqualsValueClause(expression.WithoutTrivia()))))
                .WithUsingKeyword(SyntaxFactory.Token(SyntaxKind.UsingKeyword))
                .WithTriviaFrom(statement);
        }

        bool guard = !dispose || SymbolEqualityComparer.Default.Equals(scopeType, model.Compilation.GetTypeByMetadataName("CuiSharp.OpenScope")) ||
            SymbolEqualityComparer.Default.Equals(scopeType, model.Compilation.GetTypeByMetadataName("CuiSharp.GridScope"));
        var statements = block.Statements.Take(index).Append(declaration);
        if (guard)
            statements = statements.Append(Guard(scopeName, SyntaxFactory.Block(block.Statements.Skip(index + 1))));
        else
            statements = statements.Concat(block.Statements.Skip(index + 1));
        replacement = block.WithStatements(SyntaxFactory.List(statements));
        return document.WithSyntaxRoot(root.ReplaceNode(block, replacement.WithAdditionalAnnotations(Formatter.Annotation)));
    }

    private static IfStatementSyntax Guard(string name, BlockSyntax body) =>
        SyntaxFactory.IfStatement(SyntaxFactory.IdentifierName(SyntaxFactory.ParseToken(name)), body);

    private static BlockSyntax AsBlock(StatementSyntax body) => body as BlockSyntax ?? SyntaxFactory.Block(body);

    private static string FreshName(BlockSyntax block, SemanticModel model, int position)
    {
        var used = new HashSet<string>(block.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.ValueText), StringComparer.Ordinal);
        used.UnionWith(model.LookupSymbols(position).Select(symbol => symbol.Name));
        string name = "scope";
        for (int suffix = 1; used.Contains(name); suffix++) name = "scope" + suffix;
        return name;
    }
}
