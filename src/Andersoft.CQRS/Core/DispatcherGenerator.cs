using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Andersoft.CQRS;

[Generator]
public sealed class DispatcherGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var messageTypes = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is RecordDeclarationSyntax,
                transform: static (ctx, ct) => GetMessageTypeInfo(ctx, ct))
            .Where(static m => m is not null)
            .Collect();

        var handlerTypes = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => GetHandlerTypes(ctx, ct))
            .Where(static h => h.Length > 0)
            .SelectMany<ImmutableArray<HandlerTypeInfo>, HandlerTypeInfo>(static (h, _) => h)
            .Collect();

        var combined = messageTypes.Combine(handlerTypes);

        context.RegisterSourceOutput(combined, static (spc, pair) =>
        {
            var messages = pair.Left;
            var handlers = pair.Right;

            if (!messages.IsEmpty || handlers.Any(h => h.Kind == HandlerKind.DomainEvent))
            {
                var queries = messages.Where(m => m!.Kind == MessageKind.Query).ToList();
                var commands = messages.Where(m => m!.Kind == MessageKind.Command).ToList();
                var interceptors = handlers.Where(h => h.Kind == HandlerKind.Interceptor).ToList();
                var domainEvents = handlers.Where(h => h.Kind == HandlerKind.DomainEvent).ToList();
                var source = GenerateTypedDispatcher(queries!, commands!, interceptors, domainEvents);
                spc.AddSource("TypedDispatcher.g.cs", SourceText.From(source, Encoding.UTF8));
            }

            if (!handlers.IsEmpty)
            {
                var source = GenerateRegistrationExtension(handlers);
                spc.AddSource("HandlerRegistration.g.cs", SourceText.From(source, Encoding.UTF8));
            }
        });
    }

    private static MessageTypeInfo? GetMessageTypeInfo(GeneratorSyntaxContext context, System.Threading.CancellationToken ct)
    {
        var recordDecl = (RecordDeclarationSyntax)context.Node;
        var model = context.SemanticModel;

        if (model.GetDeclaredSymbol(recordDecl, ct) is not INamedTypeSymbol recordSymbol)
            return null;

        foreach (var iface in recordSymbol.AllInterfaces)
        {
            if (iface.IsGenericType && iface.TypeArguments.Length == 1)
            {
                var ifaceName = iface.ConstructedFrom.ToDisplayString();
                if (ifaceName == "Andersoft.CQRS.Abstractions.IQuery<TResult>")
                {
                    return new MessageTypeInfo(
                        recordSymbol.ToDisplayString(),
                        recordSymbol.Name,
                        iface.TypeArguments[0].ToDisplayString(),
                        MessageKind.Query);
                }
                if (ifaceName == "Andersoft.CQRS.Abstractions.ICommand<TResult>")
                {
                    return new MessageTypeInfo(
                        recordSymbol.ToDisplayString(),
                        recordSymbol.Name,
                        iface.TypeArguments[0].ToDisplayString(),
                        MessageKind.Command);
                }
            }
        }

        return null;
    }

    private static ImmutableArray<HandlerTypeInfo> GetHandlerTypes(GeneratorSyntaxContext context, System.Threading.CancellationToken ct)
    {
        var classDecl = (ClassDeclarationSyntax)context.Node;
        var model = context.SemanticModel;

        if (model.GetDeclaredSymbol(classDecl, ct) is not INamedTypeSymbol classSymbol)
            return ImmutableArray<HandlerTypeInfo>.Empty;

        var results = ImmutableArray.CreateBuilder<HandlerTypeInfo>();

        foreach (var iface in classSymbol.AllInterfaces)
        {
            if (!iface.IsGenericType)
                continue;

            var typeArgs = iface.TypeArguments;
            var ifaceName = iface.ConstructedFrom.ToDisplayString();

            if (typeArgs.Length == 2)
            {
                if (ifaceName == "Andersoft.CQRS.Abstractions.IQueryHandler<TQuery, TResult>")
                {
                    results.Add(new HandlerTypeInfo(
                        classSymbol.ToDisplayString(),
                        typeArgs[0].ToDisplayString(),
                        typeArgs[1].ToDisplayString(),
                        HandlerKind.Query));
                }
                else if (ifaceName == "Andersoft.CQRS.Abstractions.ICommandHandler<TCommand, TResult>")
                {
                    results.Add(new HandlerTypeInfo(
                        classSymbol.ToDisplayString(),
                        typeArgs[0].ToDisplayString(),
                        typeArgs[1].ToDisplayString(),
                        HandlerKind.Command));
                }
                else if (ifaceName == "Andersoft.CQRS.Abstractions.IInterceptHandler<TMessage, TResult>")
                {
                    // An open generic interceptor (e.g. LoggingInterceptor<TMessage, TResult>) supplies
                    // its type arguments from the class's own type parameters, so it applies to every
                    // message. A closed interceptor pins concrete message/result types.
                    var isOpenGeneric = typeArgs[0].TypeKind == TypeKind.TypeParameter
                        || typeArgs[1].TypeKind == TypeKind.TypeParameter;

                    if (isOpenGeneric)
                    {
                        results.Add(new HandlerTypeInfo(
                            classSymbol.ConstructUnboundGenericType().ToDisplayString(),
                            string.Empty,
                            string.Empty,
                            HandlerKind.Interceptor,
                            isOpenGeneric: true));
                    }
                    else
                    {
                        results.Add(new HandlerTypeInfo(
                            classSymbol.ToDisplayString(),
                            typeArgs[0].ToDisplayString(),
                            typeArgs[1].ToDisplayString(),
                            HandlerKind.Interceptor));
                    }
                }
            }
            else if (typeArgs.Length == 1)
            {
                if (ifaceName == "Andersoft.CQRS.Abstractions.IDomainEventHandler<TEvent>")
                {
                    // Exclude framework interfaces (e.g. the IEquatable<TSelf> records synthesize)
                    // so only user-defined domain markers remain to derive the catch-all type from.
                    var eventInterfaces = string.Join("|", typeArgs[0].AllInterfaces
                        .Select(i => i.ToDisplayString())
                        .Where(name => !name.StartsWith("System.")));
                    results.Add(new HandlerTypeInfo(
                        classSymbol.ToDisplayString(),
                        typeArgs[0].ToDisplayString(),
                        "System.Threading.Tasks.Task",
                        HandlerKind.DomainEvent,
                        eventInterfaces));
                }
            }
        }

        return results.ToImmutable();
    }

    private static string GenerateTypedDispatcher(
        List<MessageTypeInfo> queries,
        List<MessageTypeInfo> commands,
        List<HandlerTypeInfo> interceptors,
        List<HandlerTypeInfo> domainEvents)
    {
        // Build a lookup: messageType -> resultType for closed interceptors.
        var interceptorsByMessage = new Dictionary<string, string>();
        foreach (var i in interceptors.Where(i => !i.IsOpenGeneric))
        {
            interceptorsByMessage[i.MessageType] = i.ResultType;
        }

        // An open generic interceptor (IInterceptHandler<,>) applies to every message, so when one is
        // present every query/command gets the interceptor pipeline wired up. The DI container resolves
        // the closed interceptor instances (open generic + any closed registrations) at runtime.
        var hasOpenInterceptors = interceptors.Any(i => i.IsOpenGeneric);
        bool HasInterceptors(MessageTypeInfo m) => hasOpenInterceptors || interceptorsByMessage.ContainsKey(m.FullTypeName);

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using Andersoft.CQRS;");
        sb.AppendLine("using Andersoft.CQRS.Abstractions;");
        sb.AppendLine();
        sb.AppendLine("namespace Andersoft.CQRS;");
        sb.AppendLine();
        sb.AppendLine("");
        sb.AppendLine("public sealed class TypedDispatcher");
        sb.AppendLine("{");

        // Handler fields
        foreach (var query in queries)
        {
            sb.AppendLine($"    private readonly IQueryHandler<{query.FullTypeName}, {query.ResultType}> _{ToCamelCase(query.ShortName)}Handler;");
        }

        foreach (var command in commands)
        {
            sb.AppendLine($"    private readonly ICommandHandler<{command.FullTypeName}, {command.ResultType}> _{ToCamelCase(command.ShortName)}Handler;");
        }

        // Interceptor fields
        foreach (var msg in queries.Concat(commands))
        {
            if (HasInterceptors(msg))
            {
                sb.AppendLine($"    private readonly System.Collections.Generic.List<IInterceptHandler<{msg.FullTypeName}, {msg.ResultType}>> _{ToCamelCase(msg.ShortName)}Interceptors;");
            }
        }

        // Domain event handler fields
        var domainEventGroups = domainEvents.GroupBy(d => d.MessageType).ToList();
        foreach (var group in domainEventGroups)
        {
            var shortName = ExtractShortName(group.Key);
            sb.AppendLine($"    private readonly System.Collections.Generic.List<IDomainEventHandler<{group.Key}>> _{ToCamelCase(shortName)}Handlers;");
        }

        // Constructor
        sb.AppendLine();
        sb.AppendLine("    public TypedDispatcher(");

        var paramList = new List<string>();
        foreach (var query in queries)
        {
            paramList.Add($"        IQueryHandler<{query.FullTypeName}, {query.ResultType}> {ToCamelCase(query.ShortName)}Handler");
        }
        foreach (var command in commands)
        {
            paramList.Add($"        ICommandHandler<{command.FullTypeName}, {command.ResultType}> {ToCamelCase(command.ShortName)}Handler");
        }
        foreach (var msg in queries.Concat(commands))
        {
            if (HasInterceptors(msg))
            {
                paramList.Add($"        System.Collections.Generic.IEnumerable<IInterceptHandler<{msg.FullTypeName}, {msg.ResultType}>> {ToCamelCase(msg.ShortName)}Interceptors");
            }
        }

        foreach (var group in domainEventGroups)
        {
            var shortName = ExtractShortName(group.Key);
            paramList.Add($"        System.Collections.Generic.IEnumerable<IDomainEventHandler<{group.Key}>> {ToCamelCase(shortName)}Handlers");
        }

        sb.AppendLine(string.Join(",\n", paramList) + ")");
        sb.AppendLine("    {");

        foreach (var query in queries)
        {
            sb.AppendLine($"        _{ToCamelCase(query.ShortName)}Handler = {ToCamelCase(query.ShortName)}Handler;");
        }

        foreach (var command in commands)
        {
            sb.AppendLine($"        _{ToCamelCase(command.ShortName)}Handler = {ToCamelCase(command.ShortName)}Handler;");
        }

        foreach (var msg in queries.Concat(commands))
        {
            if (HasInterceptors(msg))
            {
                var camel = ToCamelCase(msg.ShortName);
                sb.AppendLine($"        _{camel}Interceptors = System.Linq.Enumerable.ToList({camel}Interceptors ?? System.Array.Empty<IInterceptHandler<{msg.FullTypeName}, {msg.ResultType}>>());");
            }
        }

        foreach (var group in domainEventGroups)
        {
            var shortName = ExtractShortName(group.Key);
            var camel = ToCamelCase(shortName);
            sb.AppendLine($"        _{camel}Handlers = System.Linq.Enumerable.ToList({camel}Handlers ?? System.Array.Empty<IDomainEventHandler<{group.Key}>>());");
        }

        sb.AppendLine("    }");

        // DispatchAsync methods
        foreach (var query in queries)
        {
            var camel = ToCamelCase(query.ShortName);
            var hasInterceptors = HasInterceptors(query);

            sb.AppendLine();
            sb.AppendLine($"    public System.Threading.Tasks.ValueTask<{query.ResultType}> DispatchAsync(");
            sb.AppendLine($"        {query.FullTypeName} query,");
            sb.AppendLine("        System.Threading.CancellationToken ct = default)");

            if (hasInterceptors)
            {
                sb.AppendLine("    {");
                sb.AppendLine($"        return ChainInterceptors(_{camel}Interceptors, query, () => _{camel}Handler.HandleAsync(query, ct), ct);");
                sb.AppendLine("    }");
            }
            else
            {
                sb.AppendLine($"        => _{camel}Handler.HandleAsync(query, ct);");
            }
        }

        foreach (var command in commands)
        {
            var camel = ToCamelCase(command.ShortName);
            var hasInterceptors = HasInterceptors(command);

            sb.AppendLine();
            sb.AppendLine($"    public System.Threading.Tasks.ValueTask<{command.ResultType}> DispatchAsync(");
            sb.AppendLine($"        {command.FullTypeName} command,");
            sb.AppendLine("        System.Threading.CancellationToken ct = default)");

            if (hasInterceptors)
            {
                sb.AppendLine("    {");
                sb.AppendLine($"        return ChainInterceptors(_{camel}Interceptors, command, () => _{camel}Handler.HandleAsync(command, ct), ct);");
                sb.AppendLine("    }");
            }
            else
            {
                sb.AppendLine($"        => _{camel}Handler.HandleAsync(command, ct);");
            }
        }

        // PublishAsync methods
        foreach (var group in domainEventGroups)
        {
            var shortName = ExtractShortName(group.Key);
            var camel = ToCamelCase(shortName);

            sb.AppendLine();
            sb.AppendLine("    public System.Threading.Tasks.ValueTask PublishAsync(");
            sb.AppendLine($"        {group.Key} domainEvent,");
            sb.AppendLine("        System.Threading.CancellationToken ct = default)");
            sb.AppendLine("    {");
            sb.AppendLine($"        return PublishToHandlers(_{camel}Handlers, domainEvent, ct);");
            sb.AppendLine("    }");
        }

        // Catch-all PublishAsync: dispatches to the matching concrete overload at runtime.
        // Needed because callers (e.g. the SaveChanges interceptor) only know events as the
        // base IDomainEvent interface, while the per-type overloads above are concrete.
        if (domainEventGroups.Count > 0)
        {
            // The catch-all parameter is the single interface common to every event type
            // (typically the IDomainEvent marker). Falls back to object if there isn't one.
            var interfaceSets = domainEventGroups
                .Select(g => g.First().EventInterfaces
                    .Split(new[] { '|' }, System.StringSplitOptions.RemoveEmptyEntries)
                    .ToList())
                .ToList();

            var common = interfaceSets[0];
            for (var i = 1; i < interfaceSets.Count; i++)
            {
                common = common.Where(x => interfaceSets[i].Contains(x)).ToList();
            }

            var catchAllType = common.Count == 1 ? common[0] : "object";

            sb.AppendLine();
            sb.AppendLine("    public System.Threading.Tasks.ValueTask PublishAsync(");
            sb.AppendLine($"        {catchAllType} domainEvent,");
            sb.AppendLine("        System.Threading.CancellationToken ct = default)");
            sb.AppendLine("        => domainEvent switch");
            sb.AppendLine("        {");
            foreach (var group in domainEventGroups)
            {
                sb.AppendLine($"            {group.Key} e => PublishAsync(e, ct),");
            }
            sb.AppendLine("            _ => default,");
            sb.AppendLine("        };");
        }

        // ChainInterceptors helper method
        sb.AppendLine();
        sb.AppendLine("    private static System.Threading.Tasks.ValueTask<TResult> ChainInterceptors<TMessage, TResult>(");
        sb.AppendLine("        System.Collections.Generic.List<IInterceptHandler<TMessage, TResult>> interceptors,");
        sb.AppendLine("        TMessage message,");
        sb.AppendLine("        RequestHandlerDelegate<TResult> handler,");
        sb.AppendLine("        System.Threading.CancellationToken ct)");
        sb.AppendLine("    {");
        sb.AppendLine("        RequestHandlerDelegate<TResult> next = handler;");
        sb.AppendLine("        for (var i = interceptors.Count - 1; i >= 0; i--)");
        sb.AppendLine("        {");
        sb.AppendLine("            var interceptor = interceptors[i];");
        sb.AppendLine("            var current = next;");
        sb.AppendLine("            next = () => interceptor.HandleAsync(message, current, ct);");
        sb.AppendLine("        }");
        sb.AppendLine("        return next();");
        sb.AppendLine("    }");

        // PublishToHandlers helper
        sb.AppendLine();
        sb.AppendLine("    private static async System.Threading.Tasks.ValueTask PublishToHandlers<TEvent>(");
        sb.AppendLine("        System.Collections.Generic.List<IDomainEventHandler<TEvent>> handlers,");
        sb.AppendLine("        TEvent domainEvent,");
        sb.AppendLine("        System.Threading.CancellationToken ct)");
        sb.AppendLine("    {");
        sb.AppendLine("        foreach (var handler in handlers)");
        sb.AppendLine("        {");
        sb.AppendLine("            await handler.HandleAsync(domainEvent, ct);");
        sb.AppendLine("        }");
        sb.AppendLine("    }");

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string GenerateRegistrationExtension(ImmutableArray<HandlerTypeInfo> handlers)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using Andersoft.CQRS;");
        sb.AppendLine("using Andersoft.CQRS.Abstractions;");
        sb.AppendLine();
        sb.AppendLine("namespace Andersoft.CQRS;");
        sb.AppendLine();
        sb.AppendLine("public static class HandlerRegistration");
        sb.AppendLine("{");
        sb.AppendLine("    public static IServiceCollection AddApplicationHandlers(this IServiceCollection services)");
        sb.AppendLine("    {");

        foreach (var handler in handlers)
        {
            // Open generic interceptors register against the unbound interface so the container can
            // close them per message type (and honour any generic constraints) when resolving the
            // IEnumerable<IInterceptHandler<TMessage, TResult>> the dispatcher requests.
            if (handler.IsOpenGeneric)
            {
                sb.AppendLine($"        services.AddScoped(typeof(IInterceptHandler<,>), typeof({handler.HandlerType}));");
                continue;
            }

            var interfaceType = handler.Kind switch
            {
                HandlerKind.Query => $"IQueryHandler<{handler.MessageType}, {handler.ResultType}>",
                HandlerKind.Command => $"ICommandHandler<{handler.MessageType}, {handler.ResultType}>",
                HandlerKind.DomainEvent => $"IDomainEventHandler<{handler.MessageType}>",
                HandlerKind.Interceptor => $"IInterceptHandler<{handler.MessageType}, {handler.ResultType}>",
                _ => throw new System.InvalidOperationException($"Unknown handler kind: {handler.Kind}")
            };
            sb.AppendLine($"        services.AddScoped<{interfaceType}, {handler.HandlerType}>();");
        }

        sb.AppendLine("        services.AddScoped<TypedDispatcher>();");
        sb.AppendLine("        return services;");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string ToCamelCase(string s)
    {
        if (string.IsNullOrEmpty(s) || char.IsLower(s[0]))
            return s;
        return char.ToLowerInvariant(s[0]) + s.Substring(1);
    }

    private static string ExtractShortName(string fullTypeName)
    {
        var lastDot = fullTypeName.LastIndexOf('.');
        return lastDot >= 0 ? fullTypeName.Substring(lastDot + 1) : fullTypeName;
    }

    private sealed class MessageTypeInfo
    {
        public string FullTypeName { get; }
        public string ShortName { get; }
        public string ResultType { get; }
        public MessageKind Kind { get; }

        public MessageTypeInfo(string fullTypeName, string shortName, string resultType, MessageKind kind)
        {
            FullTypeName = fullTypeName;
            ShortName = shortName;
            ResultType = resultType;
            Kind = kind;
        }
    }

    private sealed class HandlerTypeInfo
    {
        public string HandlerType { get; }
        public string MessageType { get; }
        public string ResultType { get; }
        public HandlerKind Kind { get; }

        /// <summary>
        /// For domain-event handlers, the pipe-delimited list of interfaces implemented by the
        /// event type. Used to derive the common marker interface for the catch-all dispatch.
        /// </summary>
        public string EventInterfaces { get; }

        /// <summary>
        /// True when the handler is an open generic interceptor (e.g. <c>Logging&lt;TMessage, TResult&gt;</c>)
        /// that applies to every message. <see cref="HandlerType"/> then holds the unbound generic
        /// type name (e.g. <c>Foo.Logging&lt;,&gt;</c>) for open generic DI registration.
        /// </summary>
        public bool IsOpenGeneric { get; }

        public HandlerTypeInfo(string handlerType, string messageType, string resultType, HandlerKind kind, string eventInterfaces = "", bool isOpenGeneric = false)
        {
            HandlerType = handlerType;
            MessageType = messageType;
            ResultType = resultType;
            Kind = kind;
            EventInterfaces = eventInterfaces;
            IsOpenGeneric = isOpenGeneric;
        }
    }

    private enum MessageKind { Query, Command }
    private enum HandlerKind { Query, Command, DomainEvent, Interceptor }
}
