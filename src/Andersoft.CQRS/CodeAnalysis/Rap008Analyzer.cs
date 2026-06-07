using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Concurrent;

namespace Andersoft.CQRS.CodeAnalysis;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class Rap008Analyzer : DiagnosticAnalyzer
{
    internal static readonly DiagnosticDescriptor Rule = new(
        "RAP008",
        "CQRS message must have exactly one handler",
        "CQRS message must have exactly one handler",
        "Architecture",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        CqrsAnalyzer.InitializeRule(context, "RAP008");
    }
}

internal static partial class CqrsAnalyzer
{
    private static void AnalyzeRap008CqrsShape(
        CompilationAnalysisContext context,
        ConcurrentDictionary<INamedTypeSymbol, string> cqrsMessages,
        ConcurrentDictionary<INamedTypeSymbol, int> cqrsHandlerCounts)
    {
        foreach (var pair in cqrsMessages)
        {
            var messageType = pair.Key;
            if (messageType.Locations.IsDefaultOrEmpty)
            {
                continue;
            }

            var location = messageType.Locations[0];
            if (!location.IsInSource || location.SourceTree is null || IsGeneratedFile(location.SourceTree.FilePath))
            {
                continue;
            }

            var count = cqrsHandlerCounts.TryGetValue(messageType, out var found) ? found : 0;
            if (count == 1)
            {
                continue;
            }

            Report(context, Diagnostic.Create(
                Rap008Analyzer.Rule,
                location,
                pair.Value,
                messageType.Name,
                count));
        }
    }

    private static void AnalyzeRap008CollectCqrsTypeInfo(
        INamedTypeSymbol namedType,
        INamedTypeSymbol? iCommandSymbol,
        INamedTypeSymbol? iQuerySymbol,
        INamedTypeSymbol? iCommandHandlerSymbol,
        INamedTypeSymbol? iQueryHandlerSymbol,
        ConcurrentDictionary<INamedTypeSymbol, string> cqrsMessages,
        ConcurrentDictionary<INamedTypeSymbol, int> cqrsHandlerCounts)
    {
        if (namedType.IsAbstract || namedType.TypeKind is TypeKind.Interface or TypeKind.Delegate)
        {
            return;
        }

        if (ImplementsGenericInterface(namedType, iCommandSymbol))
        {
            cqrsMessages.TryAdd(namedType, "ICommand");
        }

        if (ImplementsGenericInterface(namedType, iQuerySymbol))
        {
            cqrsMessages.TryAdd(namedType, "IQuery");
        }

        var handledMessages = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var iface in namedType.AllInterfaces)
        {
            if (!iface.IsGenericType || iface.TypeArguments.Length != 2)
            {
                continue;
            }

            var ifaceDefinition = iface.ConstructedFrom;
            if (!SymbolEqualityComparer.Default.Equals(ifaceDefinition, iCommandHandlerSymbol) &&
                !SymbolEqualityComparer.Default.Equals(ifaceDefinition, iQueryHandlerSymbol))
            {
                continue;
            }

            if (iface.TypeArguments[0] is INamedTypeSymbol messageType)
            {
                handledMessages.Add(messageType);
            }
        }

        foreach (var messageType in handledMessages)
        {
            cqrsHandlerCounts.AddOrUpdate(messageType, 1, static (_, current) => current + 1);
        }
    }
}
