using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CuiSharp.Analyzers;

/// <summary>
/// The id rule: a widget id keys its hover, focus, scroll and text state, so a constant id
/// inside a loop makes every row share one state.
/// </summary>
/// <remarks>
/// Only literal ids are reported. An id built from a loop index, or a call the compiler
/// cannot fold, is almost always unique already, and a rule that guesses would be noise.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class WidgetIdAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Diagnostics.ConstantIdInLoop];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterCompilationStartAction(start =>
        {
            var types = new KnownTypes(start.Compilation);
            start.RegisterSyntaxNodeAction(ctx => Check(ctx, types), SyntaxKind.InvocationExpression);
        });
    }

    private static void Check(SyntaxNodeAnalysisContext context, KnownTypes types)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (!SyntaxHelpers.InLoop(invocation)) return;
        if (invocation.ArgumentList.Arguments.Count == 0) return;

        // Inside an id scope or a Keyed loop the literal is already namespaced; that is the
        // documented fix, so do not report it again.
        if (InsideIdScope(invocation)) return;

        IMethodSymbol? method = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol as IMethodSymbol;
        if (method is null || !types.IsUiMethod(method, invocation, context.SemanticModel, context.CancellationToken)) return;

        if (method.Parameters.Length == 0 || method.Parameters[0].Type.SpecialType != SpecialType.None) return;
        if (method.Parameters[0].Type.ToDisplayString() != "CuiSharp.UiText") return;

        // An addressing call targets an id that already exists elsewhere; it registers no state of
        // its own, so a constant target inside a loop is not a row collision.
        if (AddressingMethods.Contains(method.Name)) return;

        var first = invocation.ArgumentList.Arguments[0].Expression;
        if (first is not LiteralExpressionSyntax literal || literal.Token.ValueText.Length == 0) return;

        // A label-and-id pair (ui.Tree("node", ...)) is still a constant id.
        context.ReportDiagnostic(Diagnostic.Create(Diagnostics.ConstantIdInLoop, literal.GetLocation(), literal.Token.ValueText));
    }

    /// <summary>
    /// Calls that reference an existing id instead of registering a new one. A new C function of
    /// this shape belongs here with a one-line why, the same way an override does.
    /// </summary>
    private static readonly HashSet<string> AddressingMethods = new(StringComparer.Ordinal) { "ScrollTo" };

    private static bool InsideIdScope(SyntaxNode node)
    {
        foreach (SyntaxNode ancestor in node.Ancestors())
        {
            if (ancestor is UsingStatementSyntax { Expression: InvocationExpressionSyntax call } &&
                call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Id" or "Keyed" })
            {
                return true;
            }

            if (ancestor is ForEachStatementSyntax foreachStatement &&
                foreachStatement.Expression is InvocationExpressionSyntax loop &&
                loop.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Keyed" })
            {
                return true;
            }
        }

        return false;
    }
}
