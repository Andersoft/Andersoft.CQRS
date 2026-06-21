using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Andersoft.CQRS.CodeAnalysis;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class Rap007Analyzer : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor Rule = new(
        "RAP007",
        "Do not throw business exceptions across OneOf boundaries",
        "Do not throw business exceptions across OneOf boundaries",
        "Architecture",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        CqrsAnalyzer.InitializeRule(context, "RAP007");
    }
}

internal static partial class CqrsAnalyzer
{
    private static void AnalyzeRap007OneOfBusinessException(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol thrownType,
        INamedTypeSymbol? iCommandHandlerSymbol,
        INamedTypeSymbol? iQueryHandlerSymbol)
    {
        var containingType = context.ContainingSymbol?.ContainingType;
        if (containingType is null)
        {
            return;
        }

        if (IsApplicationHandlerType(containingType, iCommandHandlerSymbol, iQueryHandlerSymbol))
        {
            Report(context, Diagnostic.Create(
                Rap007Analyzer.Rule,
                context.Node.GetLocation(),
                thrownType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                "application handler"));
            return;
        }

        if (!IsPresentationControllerOrHub(containingType))
        {
            return;
        }

        if (context.Node.FirstAncestorOrSelf<MethodDeclarationSyntax>() is not MethodDeclarationSyntax methodDeclaration)
        {
            return;
        }

        if (!MethodUsesOneOfDispatch(methodDeclaration, context.SemanticModel))
        {
            return;
        }

        Report(context, Diagnostic.Create(
            Rap007Analyzer.Rule,
            context.Node.GetLocation(),
            thrownType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            "presentation action"));
    }
}
