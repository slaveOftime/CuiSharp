using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CuiSharp.Analyzers;

/// <summary>
/// The frame rules: what may run inside a frame, and what a frame must not do.
/// </summary>
/// <remarks>
/// CUI builds the whole window in one synchronous pass, so a frame that blocks, allocates or
/// loses the UI thread shows up as a stutter or a dead window. These rules point at the three
/// shapes that cause it and say what to do instead.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class FrameAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [Diagnostics.BlockingCall, Diagnostics.ConfigureAwaitInComponent, Diagnostics.AsyncVoidInComponent,
         Diagnostics.AllocationInFrame, Diagnostics.ReflectionUnderAot];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterCompilationStartAction(start =>
        {
            var types = new KnownTypes(start.Compilation);

            // CUI001: a blocking call in anything that runs per frame.
            start.RegisterSyntaxNodeAction(ctx => CheckBlocking(ctx, types),
                SyntaxKind.SimpleMemberAccessExpression, SyntaxKind.InvocationExpression);

            // CUI005 / CUI006: the hosting rules.
            start.RegisterSyntaxNodeAction(ctx => CheckConfigureAwait(ctx, types), SyntaxKind.InvocationExpression);
            start.RegisterSymbolAction(ctx => CheckAsyncVoid(ctx, types), SymbolKind.Method);

            // CUI008: reflection-based serialization, which Native AOT trims away.
            start.RegisterSyntaxNodeAction(ctx => CheckReflection(ctx, types), SyntaxKind.InvocationExpression);

            // CUI007: allocations in a frame method.
            start.RegisterSyntaxNodeAction(ctx => CheckAllocation(ctx, types),
                SyntaxKind.ArrayCreationExpression, SyntaxKind.ImplicitArrayCreationExpression,
                SyntaxKind.ObjectCreationExpression, SyntaxKind.AddExpression);
        });
    }

    // ---- CUI001 -------------------------------------------------------------

    private static void CheckBlocking(SyntaxNodeAnalysisContext context, KnownTypes types)
    {
        if (!InFrame(context.Node, context.SemanticModel, types, context.CancellationToken)) return;
        SyntaxNode node = context.Node;

        // task.Result: a property, so it is only ever a member access.
        if (node is MemberAccessExpressionSyntax access && access.Name.Identifier.ValueText == "Result")
        {
            ITypeSymbol? receiver = context.SemanticModel.GetTypeInfo(access.Expression, context.CancellationToken).Type;
            if (receiver is null || !IsAwaitable(receiver)) return;

            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.BlockingCall, access.GetLocation(),
                access.Expression + ".Result"));
            return;
        }

        if (node is not InvocationExpressionSyntax invocation) return;

        // task.Wait(): reported on the invocation, so the member access inside it does not
        // report the same line twice.
        if (CalledName(invocation) == "Wait")
        {
            ITypeSymbol? receiver = invocation.Expression is MemberAccessExpressionSyntax waited
                ? context.SemanticModel.GetTypeInfo(waited.Expression, context.CancellationToken).Type
                : null;
            if (receiver is not null && IsAwaitable(receiver))
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.BlockingCall, invocation.GetLocation(),
                    invocation.Expression + "()"));
                return;
            }
        }

        // task.GetAwaiter().GetResult()
        if (CalledName(invocation) == "GetResult" && invocation.Expression.ToString().Contains("GetAwaiter()", StringComparison.Ordinal))
        {
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.BlockingCall, invocation.GetLocation(), "GetAwaiter().GetResult()"));
            return;
        }

        // Thread.Sleep(...) / Thread.SpinWait(...)
        if (CalledName(invocation) is "Sleep" or "SpinWait" or "Yield" &&
            invocation.Expression is MemberAccessExpressionSyntax member &&
            member.Expression.ToString() is "Thread" or "System.Threading.Thread")
        {
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.BlockingCall, invocation.GetLocation(), "Thread." + CalledName(invocation)));
            return;
        }

        if (invocation.Expression is not MemberAccessExpressionSyntax call) return;
        string owner = call.Expression.ToString();

        // Task.Run(...) inside a frame: the work was moved off the frame thread into one started
        // by the frame, which is the opposite of what a Mutation does — it keeps the result,
        // notices cancellation, applies on the UI thread and reports the failure.
        if (owner is "Task" or "System.Threading.Tasks.Task" && CalledName(invocation) == "Run")
        {
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.BlockingCall, invocation.GetLocation(), "Task.Run"));
            return;
        }

        // File.* / Directory.*: the disk is not part of a frame, and one read per keystroke is the
        // most common freeze in an immediate-mode app.
        if (owner is "File" or "System.IO.File" or "Directory" or "System.IO.Directory")
        {
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.BlockingCall, invocation.GetLocation(),
                owner + "." + CalledName(invocation)));
        }
    }

    private static bool IsAwaitable(ITypeSymbol type) =>
        type.Name is "Task" or "Task`1" or "ValueTask" or "ValueTask`1";

    // ---- CUI008 -------------------------------------------------------------

    /// <summary>
    /// The serializers that reflect over a type at run time. Serializing a type without a
    /// source-generated context publishes with warnings and fails on the target, so this is a
    /// warning on the call rather than a note about the project.
    /// </summary>
    private static void CheckReflection(SyntaxNodeAnalysisContext context, KnownTypes types)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method) return;
        if (method.ContainingType?.Name != "JsonSerializer") return;
        if (method.Name is not ("Serialize" or "Deserialize" or "SerializeAsync" or "DeserializeAsync" or "DeserializeAsyncEnumerable")) return;

        // The overloads that take a JsonTypeInfo or a context carry the metadata the trimmer needs:
        // those calls are the fix, so reporting them would be noise.
        foreach (IParameterSymbol parameter in method.Parameters)
        {
            if (parameter.Type.Name is "JsonTypeInfo" or "JsonSerializerContext") return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Diagnostics.ReflectionUnderAot, invocation.GetLocation(),
            $"JsonSerializer.{method.Name}"));
    }

    // ---- CUI005 -------------------------------------------------------------

    private static void CheckConfigureAwait(SyntaxNodeAnalysisContext context, KnownTypes types)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (CalledName(invocation) != "ConfigureAwait") return;
        if (!InsideComponent(invocation, context.SemanticModel, types, context.CancellationToken)) return;

        context.ReportDiagnostic(Diagnostic.Create(Diagnostics.ConfigureAwaitInComponent, invocation.GetLocation()));
    }

    // ---- CUI006 -------------------------------------------------------------

    private static void CheckAsyncVoid(SymbolAnalysisContext context, KnownTypes types)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (!method.IsAsync || !method.ReturnsVoid) return;
        if (method.MethodKind is not (MethodKind.Ordinary or MethodKind.LocalFunction)) return;
        if (!types.IsComponent(method.ContainingType)) return;

        context.ReportDiagnostic(Diagnostic.Create(Diagnostics.AsyncVoidInComponent,
            method.Locations.FirstOrDefault(), method.Name));
    }

    // ---- CUI007 -------------------------------------------------------------

    private static void CheckAllocation(SyntaxNodeAnalysisContext context, KnownTypes types)
    {
        if (!InFrame(context.Node, context.SemanticModel, types, context.CancellationToken)) return;

        switch (context.Node)
        {
            case ArrayCreationExpressionSyntax array:
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.AllocationInFrame, array.GetLocation(), "array"));
                break;
            case ImplicitArrayCreationExpressionSyntax implicitArray:
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.AllocationInFrame, implicitArray.GetLocation(), "array"));
                break;
            case ObjectCreationExpressionSyntax creation:
                ITypeSymbol? created = context.SemanticModel.GetTypeInfo(creation, context.CancellationToken).Type;
                if (created is null || !created.IsReferenceType) return;
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.AllocationInFrame, creation.GetLocation(), "object"));
                break;
            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression):
                ITypeSymbol? left = context.SemanticModel.GetTypeInfo(binary.Left, context.CancellationToken).Type;
                ITypeSymbol? right = context.SemanticModel.GetTypeInfo(binary.Right, context.CancellationToken).Type;
                if (left?.SpecialType != SpecialType.System_String && right?.SpecialType != SpecialType.System_String) return;
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.AllocationInFrame, binary.GetLocation(), "string concatenation"));
                break;
        }
    }

    // ---- shared -------------------------------------------------------------

    /// <summary>
    /// True when the node runs inside a frame: a method, lambda or local function that takes a
    /// <c>Ui</c>, or any member of a <c>Component</c>.
    /// </summary>
    private static bool InFrame(SyntaxNode node, SemanticModel model, KnownTypes types, CancellationToken token)
    {
        foreach (SyntaxNode ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case MethodDeclarationSyntax method when HasUiParameter(method.ParameterList, model, types, token):
                case LocalFunctionStatementSyntax local when HasUiParameter(local.ParameterList, model, types, token):
                    return true;
                case LambdaExpressionSyntax lambda when LambdaTakesUi(lambda, model, types, token):
                    return true;
                case TypeDeclarationSyntax declaration:
                    return model.GetDeclaredSymbol(declaration, token) is { } type && types.IsComponent(type);
            }
        }

        return false;
    }

    private static bool HasUiParameter(BaseParameterListSyntax? parameters, SemanticModel model, KnownTypes types, CancellationToken token)
    {
        if (parameters is null) return false;
        foreach (ParameterSyntax parameter in parameters.Parameters)
        {
            if (parameter.Type is null) continue;
            ITypeSymbol? type = model.GetTypeInfo(parameter.Type, token).Type;
            if (type is not null && types.IsUi(type)) return true;
        }

        return false;
    }

    private static bool LambdaTakesUi(LambdaExpressionSyntax lambda, SemanticModel model, KnownTypes types, CancellationToken token)
    {
        // A canvas painter is written without types: (ui, box) => ... names it `ui`.
        ParameterSyntax[] parameters = lambda switch
        {
            SimpleLambdaExpressionSyntax simple => [simple.Parameter],
            ParenthesizedLambdaExpressionSyntax parenthesized => [.. parenthesized.ParameterList.Parameters],
            _ => [],
        };

        foreach (ParameterSyntax parameter in parameters)
        {
            if (parameter.Identifier.ValueText == "ui") return true;
            if (parameter.Type is null) continue;
            ITypeSymbol? type = model.GetTypeInfo(parameter.Type, token).Type;
            if (type is not null && types.IsUi(type)) return true;
        }

        return false;
    }

    private static bool InsideComponent(SyntaxNode node, SemanticModel model, KnownTypes types, CancellationToken token)
    {
        TypeDeclarationSyntax? declaration = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        return declaration is not null && model.GetDeclaredSymbol(declaration, token) is { } type && types.IsComponent(type);
    }

    private static string CalledName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        _ => "",
    };
}
