using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Andersoft.CQRS.CodeAnalysis;

internal static partial class CqrsAnalyzer
{
    private static readonly AsyncLocal<string?> ActiveRuleId = new();

    private static bool IsActive(string ruleId) =>
        string.Equals(ActiveRuleId.Value, ruleId, StringComparison.Ordinal);

    private static void ExecuteForRule(string ruleId, Action action)
    {
        var previous = ActiveRuleId.Value;
        ActiveRuleId.Value = ruleId;
        try
        {
            action();
        }
        finally
        {
            ActiveRuleId.Value = previous;
        }
    }

    private static void Report(SyntaxNodeAnalysisContext context, Diagnostic diagnostic)
    {
        if (IsActive(diagnostic.Id))
        {
            context.ReportDiagnostic(diagnostic);
        }
    }

    private static void Report(SymbolAnalysisContext context, Diagnostic diagnostic)
    {
        if (IsActive(diagnostic.Id))
        {
            context.ReportDiagnostic(diagnostic);
        }
    }

    private static void Report(CompilationAnalysisContext context, Diagnostic diagnostic)
    {
        if (IsActive(diagnostic.Id))
        {
            context.ReportDiagnostic(diagnostic);
        }
    }

    internal static void InitializeRule(AnalysisContext context, string ruleId)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterCompilationStartAction(startContext =>
        {
            var iCommandSymbol = startContext.Compilation.GetTypeByMetadataName("Andersoft.CQRS.Abstractions.ICommand`1");
            var iQuerySymbol = startContext.Compilation.GetTypeByMetadataName("Andersoft.CQRS.Abstractions.IQuery`1");
            var iCommandHandlerSymbol = startContext.Compilation.GetTypeByMetadataName("Andersoft.CQRS.Abstractions.ICommandHandler`2");
            var iQueryHandlerSymbol = startContext.Compilation.GetTypeByMetadataName("Andersoft.CQRS.Abstractions.IQueryHandler`2");
            var iDomainEventHandlerSymbol = startContext.Compilation.GetTypeByMetadataName("Andersoft.CQRS.Abstractions.IDomainEventHandler`1");

            var cqrsMessages = new ConcurrentDictionary<INamedTypeSymbol, string>(SymbolEqualityComparer.Default);
            var cqrsHandlerCounts = new ConcurrentDictionary<INamedTypeSymbol, int>(SymbolEqualityComparer.Default);

            startContext.RegisterSyntaxNodeAction(
                ctx => ExecuteForRule(ruleId, () => AnalyzeThrowSyntax(ctx, iCommandHandlerSymbol, iQueryHandlerSymbol)),
                SyntaxKind.ThrowStatement,
                SyntaxKind.ThrowExpression);

            startContext.RegisterSyntaxNodeAction(
                ctx => ExecuteForRule(ruleId, () => AnalyzeMethodDeclarationForQueryHandlers(ctx)),
                SyntaxKind.MethodDeclaration);

            startContext.RegisterSymbolAction(
                ctx => ExecuteForRule(ruleId, () => AnalyzeNamedType(
                    ctx,
                    iCommandSymbol,
                    iQuerySymbol,
                    iCommandHandlerSymbol,
                    iQueryHandlerSymbol,
                    iDomainEventHandlerSymbol,
                    cqrsMessages,
                    cqrsHandlerCounts)),
                SymbolKind.NamedType);

            startContext.RegisterCompilationEndAction(
                ctx => ExecuteForRule(ruleId, () => AnalyzeRap008CqrsShape(ctx, cqrsMessages, cqrsHandlerCounts)));
        });
    }

    private static void AnalyzeThrowSyntax(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol? iCommandHandlerSymbol,
        INamedTypeSymbol? iQueryHandlerSymbol)
    {
        if (IsGeneratedFile(context.Node.SyntaxTree.FilePath))
        {
            return;
        }

        var thrownExpression = context.Node switch
        {
            ThrowStatementSyntax throwStatement => throwStatement.Expression,
            ThrowExpressionSyntax throwExpression => throwExpression.Expression,
            _ => null,
        };

        if (thrownExpression is null)
        {
            return;
        }

        var thrownType = context.SemanticModel.GetTypeInfo(thrownExpression).Type as INamedTypeSymbol;
        if (!IsBusinessExceptionType(thrownType))
        {
            return;
        }

        AnalyzeRap007OneOfBusinessException(context, thrownType!, iCommandHandlerSymbol, iQueryHandlerSymbol);
    }

    private static void AnalyzeNamedType(
        SymbolAnalysisContext context,
        INamedTypeSymbol? iCommandSymbol,
        INamedTypeSymbol? iQuerySymbol,
        INamedTypeSymbol? iCommandHandlerSymbol,
        INamedTypeSymbol? iQueryHandlerSymbol,
        INamedTypeSymbol? iDomainEventHandlerSymbol,
        ConcurrentDictionary<INamedTypeSymbol, string> cqrsMessages,
        ConcurrentDictionary<INamedTypeSymbol, int> cqrsHandlerCounts)
    {
        if (context.Symbol is not INamedTypeSymbol namedType)
        {
            return;
        }

        if (namedType.Locations.IsDefaultOrEmpty || namedType.Locations[0].SourceTree is null)
        {
            return;
        }

        var filePath = namedType.Locations[0].SourceTree!.FilePath;
        if (IsGeneratedFile(filePath))
        {
            return;
        }

        if (iCommandSymbol is not null || iQuerySymbol is not null || iCommandHandlerSymbol is not null || iQueryHandlerSymbol is not null)
        {
            AnalyzeRap008CollectCqrsTypeInfo(namedType, iCommandSymbol, iQuerySymbol, iCommandHandlerSymbol, iQueryHandlerSymbol, cqrsMessages, cqrsHandlerCounts);
        }

        if (iDomainEventHandlerSymbol is not null)
        {
            AnalyzeRap009DomainEventHandlerPlacement(context, namedType, filePath, iDomainEventHandlerSymbol);
        }
    }

    private static bool IsApplicationHandlerType(
        INamedTypeSymbol namedType,
        INamedTypeSymbol? iCommandHandlerSymbol,
        INamedTypeSymbol? iQueryHandlerSymbol)
    {
        var namespaceText = namedType.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        if (!namespaceText.Contains(".Application.", StringComparison.Ordinal) || !namedType.Name.EndsWith("Handler", StringComparison.Ordinal))
        {
            return false;
        }

        return ImplementsGenericInterface(namedType, iCommandHandlerSymbol) ||
            ImplementsGenericInterface(namedType, iQueryHandlerSymbol);
    }

    private static bool IsPresentationControllerOrHub(INamedTypeSymbol namedType)
    {
        var namespaceText = namedType.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        return namespaceText.Contains(".Presentation.", StringComparison.Ordinal) &&
            (namedType.Name.EndsWith("Controller", StringComparison.Ordinal) ||
             namedType.Name.EndsWith("Hub", StringComparison.Ordinal));
    }

    private static bool MethodUsesOneOfDispatch(MethodDeclarationSyntax methodDeclaration, SemanticModel semanticModel)
    {
        foreach (var invocation in methodDeclaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol methodSymbol)
            {
                continue;
            }

            if (!string.Equals(methodSymbol.Name, "DispatchAsync", StringComparison.Ordinal))
            {
                continue;
            }

            if (IsOneOfOrTaskLikeOneOf(methodSymbol.ReturnType))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsOneOfOrTaskLikeOneOf(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol namedType)
        {
            return false;
        }

        if (IsOneOfType(namedType))
        {
            return true;
        }

        var constructedFrom = namedType.ConstructedFrom.ToDisplayString();
        if (constructedFrom is "System.Threading.Tasks.Task<TResult>" or "System.Threading.Tasks.ValueTask<TResult>" &&
            namedType.TypeArguments.Length == 1)
        {
            return IsOneOfOrTaskLikeOneOf(namedType.TypeArguments[0]);
        }

        return false;
    }

    private static bool IsOneOfType(INamedTypeSymbol typeSymbol)
    {
        var containingNamespace = typeSymbol.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        return containingNamespace == "OneOf" && typeSymbol.Name.StartsWith("OneOf", StringComparison.Ordinal);
    }

    private static bool IsBusinessExceptionType(INamedTypeSymbol? exceptionType)
    {
        if (exceptionType is null)
        {
            return false;
        }

        var exceptionBase = exceptionType;
        var isException = false;
        while (exceptionBase is not null)
        {
            if (exceptionBase.ToDisplayString() == "System.Exception")
            {
                isException = true;
                break;
            }

            exceptionBase = exceptionBase.BaseType;
        }

        if (!isException)
        {
            return false;
        }

        var ns = exceptionType.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        if (ns.Contains(".Common.Application.Errors", StringComparison.Ordinal) ||
            ns.Contains(".Domain.Errors", StringComparison.Ordinal))
        {
            return true;
        }

        return exceptionType.Name.Contains("NotFound", StringComparison.OrdinalIgnoreCase) ||
            exceptionType.Name.Contains("Conflict", StringComparison.OrdinalIgnoreCase) ||
            exceptionType.Name.Contains("Validation", StringComparison.OrdinalIgnoreCase) ||
            exceptionType.Name.Contains("Business", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NamespaceHasSegment(string namespaceText, string segment)
    {
        return namespaceText.Contains($".{segment}.", StringComparison.Ordinal) ||
            namespaceText.EndsWith($".{segment}", StringComparison.Ordinal);
    }

    private static bool ImplementsGenericInterface(INamedTypeSymbol namedType, INamedTypeSymbol? interfaceSymbol)
    {
        if (interfaceSymbol is null)
        {
            return false;
        }

        foreach (var iface in namedType.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(iface.ConstructedFrom, interfaceSymbol))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGeneratedFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return true;
        }

        var normalized = filePath.Replace('\\', '/');
        return normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static void AnalyzeMethodDeclarationForQueryHandlers(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is not MethodDeclarationSyntax methodDeclaration)
        {
            return;
        }

        if (IsGeneratedFile(methodDeclaration.SyntaxTree.FilePath))
        {
            return;
        }

        if (context.SemanticModel.GetDeclaredSymbol(methodDeclaration) is not IMethodSymbol methodSymbol)
        {
            return;
        }

        AnalyzeRap018QueryHandlerAsNoTracking(context, methodDeclaration, methodSymbol);
    }
}
