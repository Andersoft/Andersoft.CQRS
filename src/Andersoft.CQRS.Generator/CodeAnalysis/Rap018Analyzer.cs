using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Andersoft.CQRS.CodeAnalysis;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class Rap018Analyzer : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor Rule = new(
        "RAP018",
        "Query handler missing AsNoTracking",
        "Query handler missing AsNoTracking",
        "Architecture",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        CqrsAnalyzer.InitializeRule(context, "RAP018");
    }
}

internal static partial class CqrsAnalyzer
{
    private static void AnalyzeRap018QueryHandlerAsNoTracking(
        SyntaxNodeAnalysisContext context,
        MethodDeclarationSyntax methodDeclaration,
        IMethodSymbol methodSymbol)
    {
        if (!methodSymbol.Name.EndsWith("Handler", StringComparison.Ordinal) &&
            !methodSymbol.ContainingType.Name.EndsWith("QueryHandler", StringComparison.Ordinal))
        {
            return;
        }

        var materializers = new[] { "ToListAsync", "FirstOrDefaultAsync", "SingleOrDefaultAsync", "AnyAsync", "CountAsync", "ToArrayAsync" };

        foreach (var invocation in methodDeclaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (context.SemanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol called)
            {
                continue;
            }

            if (!materializers.Contains(called.Name, StringComparer.Ordinal))
            {
                continue;
            }

            var expressionText = invocation.Expression.ToString();
            if (expressionText.Contains("AsNoTracking", StringComparison.Ordinal))
            {
                continue;
            }

            Report(context, Diagnostic.Create(
                Rap018Analyzer.Rule,
                invocation.GetLocation(),
                methodSymbol.Name));
        }
    }
}
