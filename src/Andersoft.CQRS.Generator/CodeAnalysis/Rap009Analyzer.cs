using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System;

namespace Andersoft.CQRS.CodeAnalysis;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class Rap009Analyzer : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor Rule = new(
        "RAP009",
        "Domain event handler placement violation",
        "Domain event handler placement violation",
        "Architecture",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        CqrsAnalyzer.InitializeRule(context, "RAP009");
    }
}

internal static partial class CqrsAnalyzer
{
    private static void AnalyzeRap009DomainEventHandlerPlacement(
        SymbolAnalysisContext context,
        INamedTypeSymbol namedType,
        string filePath,
        INamedTypeSymbol iDomainEventHandlerSymbol)
    {
        if (namedType.IsAbstract || !ImplementsGenericInterface(namedType, iDomainEventHandlerSymbol))
        {
            return;
        }

        var namespaceText = namedType.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        var inApplicationLayer = NamespaceHasSegment(namespaceText, "Application");
        var normalizedPath = filePath.Replace('\\', '/');
        var approvedSegment = namespaceText.Contains(".DomainEventHandlers.", StringComparison.Ordinal) ||
            namespaceText.Contains(".EventHandlers.", StringComparison.Ordinal) ||
            normalizedPath.Contains("/DomainEventHandlers/", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.Contains("/EventHandlers/", StringComparison.OrdinalIgnoreCase);

        if (inApplicationLayer && approvedSegment)
        {
            return;
        }

        Report(context, Diagnostic.Create(
            Rap009Analyzer.Rule,
            namedType.Locations[0],
            namedType.Name));
    }
}
