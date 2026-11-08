using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CuiSharp.Analyzers;

/// <summary>
/// The scope rules: a container opens a native scope that has to be closed, and a
/// conditional container only lays its body out when it is open.
/// </summary>
/// <remarks>
/// Both <c>using (var s = ui.Sheet(...))</c> and <c>using var s = ui.Sheet(...);</c> exist in
/// the wild (the gallery and the template use one each), so the rules read either shape.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ScopeAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [Diagnostics.ScopeNotDisposed, Diagnostics.OpenScopeNotChecked];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterCompilationStartAction(start =>
        {
            var types = new KnownTypes(start.Compilation);
            start.RegisterSyntaxNodeAction(ctx => CheckScopeDiscarded(ctx, types), SyntaxKind.ExpressionStatement);
            start.RegisterSyntaxNodeAction(ctx => CheckLocalScope(ctx, types),
                SyntaxKind.LocalDeclarationStatement, SyntaxKind.UsingStatement);
        });
    }

    // ---- CUI002 -------------------------------------------------------------

    private static void CheckScopeDiscarded(SyntaxNodeAnalysisContext context, KnownTypes types)
    {
        // ui.Card(new Card());        // the scope is dropped, so its pop never runs
        var statement = (ExpressionStatementSyntax)context.Node;
        if (statement.Expression is not InvocationExpressionSyntax invocation) return;

        IMethodSymbol? method = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol as IMethodSymbol;
        if (method is null || !types.IsScope(method.ReturnType)) return;

        context.ReportDiagnostic(Diagnostic.Create(Diagnostics.ScopeNotDisposed, statement.GetLocation(), method.Name));
    }

    // ---- CUI002 and CUI003 on a local ---------------------------------------

    private static void CheckLocalScope(SyntaxNodeAnalysisContext context, KnownTypes types)
    {
        if (!TryReadScope(context, types, out bool isUsing, out VariableDeclaratorSyntax? declarator, out ITypeSymbol? type))
        {
            return;
        }

        string name = declarator!.Identifier.ValueText;

        // CUI002: a scope kept for later without a using, and never disposed by hand.
        // (`fixed` is the reason C# allows the local at all; a bare assignment is a leak.)
        if (!isUsing)
        {
            if (IsDisposedLater(context.Node, name)) return;
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.ScopeNotDisposed, context.Node.GetLocation(), name));
            return;
        }

        // CUI003: a conditional container whose Open flag the body never reads.
        if (!types.IsConditionalScope(type)) return;
        if (IsCheckedForOpen(context.Node, name)) return;

        context.ReportDiagnostic(Diagnostic.Create(Diagnostics.OpenScopeNotChecked, context.Node.GetLocation(), name, name));
    }

    /// <summary>
    /// Reads the single initializer of a local or using declaration and its type, so the rules
    /// do not have to care which of the two shapes was written.
    /// </summary>
    private static bool TryReadScope(SyntaxNodeAnalysisContext context, KnownTypes types,
        out bool isUsing, out VariableDeclaratorSyntax? declarator, out ITypeSymbol? type)
    {
        isUsing = false;
        declarator = null;
        type = null;

        VariableDeclarationSyntax? declaration = context.Node switch
        {
            LocalDeclarationStatementSyntax local => local.Declaration,
            UsingStatementSyntax usingStatement => usingStatement.Declaration,
            _ => null,
        };

        if (declaration is null || declaration.Variables.Count != 1) return false;

        isUsing = context.Node switch
        {
            UsingStatementSyntax => true,
            LocalDeclarationStatementSyntax localStatement => localStatement.UsingKeyword != default,
            _ => false,
        };

        if (declaration.Variables[0].Initializer?.Value is not { } value) return false;

        var operation = context.SemanticModel.GetOperation(value, context.CancellationToken);
        if (operation?.Type is not { } initializerType || !types.IsScope(initializerType)) return false;

        declarator = declaration.Variables[0];
        type = initializerType;
        return true;
    }

    private static bool IsCheckedForOpen(SyntaxNode declaration, string name)
    {
        // `using (var s = ...) body` keeps its body in the statement; a declaration
        // (`using var s = ...;`) owns the rest of the block from its parent.
        SyntaxNode? region = declaration switch
        {
            UsingStatementSyntax usingStatement => usingStatement.Statement,
            _ => declaration.Parent,
        };

        if (region is null) return false;

        foreach (SyntaxNode node in region.DescendantNodes())
        {
            // if (sheet) / !sheet.Open / sheet.Open && ... / sheet.Open ? a : b
            if (node is IdentifierNameSyntax identifier && identifier.Identifier.ValueText == name && IsInCondition(identifier))
            {
                return true;
            }

            if (node is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Open" } member &&
                member.Expression is IdentifierNameSyntax target && target.Identifier.ValueText == name)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInCondition(SyntaxNode node) =>
        node.Parent is IfStatementSyntax or WhileStatementSyntax or ConditionalExpressionSyntax ||
        node.Parent is BinaryExpressionSyntax or PrefixUnaryExpressionSyntax;

    private static bool IsDisposedLater(SyntaxNode declaration, string name)
    {
        SyntaxNode? region = declaration.Parent;
        if (region is null) return false;

        return region.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(call =>
            call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Dispose" } member &&
            member.Expression is IdentifierNameSyntax target && target.Identifier.ValueText == name);
    }
}
