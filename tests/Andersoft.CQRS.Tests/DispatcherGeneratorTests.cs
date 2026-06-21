using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Andersoft.CQRS;

namespace Andersoft.CQRS.Tests;

public sealed class DispatcherGeneratorTests
{
    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default
        .WithLanguageVersion(LanguageVersion.CSharp11);

    // The library's public surface, reduced to what the generator keys off: two handler
    // arities, two interceptor arities, the pipeline delegates, and the saga base.
    private const string ContractsSource = @"
namespace Andersoft.CQRS
{
    using System.Threading.Tasks;
    public delegate ValueTask<TResult> RequestHandlerDelegate<TResult>();
    public delegate ValueTask RequestHandlerDelegate();
}
namespace Andersoft.CQRS.Abstractions
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Andersoft.CQRS;

    public interface IMessageHandler<in TMessage> { ValueTask HandleAsync(TMessage message, CancellationToken ct = default); }
    public interface IMessageHandler<in TMessage, TResult> { ValueTask<TResult> HandleAsync(TMessage message, CancellationToken ct = default); }
    public interface IInterceptHandler<in TMessage, TResult> { ValueTask<TResult> HandleAsync(TMessage message, RequestHandlerDelegate<TResult> next, CancellationToken ct); }
    public interface IInterceptHandler<in TMessage> { ValueTask HandleAsync(TMessage message, RequestHandlerDelegate next, CancellationToken ct); }

    public abstract class SagaState { public Guid CorrelationId { get; set; } public uint Version { get; set; } }

    // Only the non-generic Saga base is part of the library surface. The generic Saga<TState>
    // that concrete sagas extend is emitted by the generator (post-initialization), so it is NOT
    // stubbed here — the generator provides it and the saga tests assert on it.
    public abstract class Saga { }
}";

    [Fact]
    public void ResultHandler_GeneratesSingleDispatchAndRegistration()
    {
        var source = ContractsSource + @"
namespace TestApp
{
    using Andersoft.CQRS.Abstractions;
    using System.Threading;
    using System.Threading.Tasks;

    public record GetUserQuery { }
    public class GetUserHandler : IMessageHandler<GetUserQuery, string>
    {
        public ValueTask<string> HandleAsync(GetUserQuery message, CancellationToken ct = default) => new(""Alice"");
    }
}";
        var generated = RunAndGet(source);

        var dispatcher = generated.Dispatcher;
        Assert.Contains("public System.Threading.Tasks.ValueTask<string> DispatchAsync(", dispatcher);
        Assert.Contains("IMessageHandler<TestApp.GetUserQuery, string>", dispatcher);
        Assert.Contains("=> _getUserQueryHandler.HandleAsync(message, ct);", dispatcher);

        Assert.Contains("services.AddScoped<IMessageHandler<TestApp.GetUserQuery, string>, TestApp.GetUserHandler>();", generated.Registration);
    }

    [Fact]
    public void VoidHandler_GeneratesFanOutDispatchAndRegistration()
    {
        var source = ContractsSource + @"
namespace TestApp
{
    using Andersoft.CQRS.Abstractions;
    using System.Threading;
    using System.Threading.Tasks;

    public record CreateOrder { }
    public class CreateOrderHandler : IMessageHandler<CreateOrder>
    {
        public ValueTask HandleAsync(CreateOrder message, CancellationToken ct = default) => default;
    }
}";
        var generated = RunAndGet(source);

        // Void messages dispatch to ALL handlers and return no value.
        Assert.Contains("public System.Threading.Tasks.ValueTask DispatchAsync(", generated.Dispatcher);
        Assert.Contains("=> InvokeAll(_createOrderHandlers, message, ct);", generated.Dispatcher);
        Assert.Contains("System.Collections.Generic.List<IMessageHandler<TestApp.CreateOrder>>", generated.Dispatcher);

        Assert.Contains("services.AddScoped<IMessageHandler<TestApp.CreateOrder>, TestApp.CreateOrderHandler>();", generated.Registration);
    }

    [Fact]
    public void VoidMessage_WithManyHandlers_FansOutAndRegistersAll()
    {
        var source = ContractsSource + @"
namespace TestApp
{
    using Andersoft.CQRS.Abstractions;
    using System.Threading;
    using System.Threading.Tasks;

    public record OrderPlaced { }
    public class EmailHandler : IMessageHandler<OrderPlaced>
    {
        public ValueTask HandleAsync(OrderPlaced message, CancellationToken ct = default) => default;
    }
    public class AuditHandler : IMessageHandler<OrderPlaced>
    {
        public ValueTask HandleAsync(OrderPlaced message, CancellationToken ct = default) => default;
    }
}";
        var generated = RunAndGet(source);

        // One dispatch overload, two registrations — fan-out is just handler count.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(generated.Dispatcher, @"InvokeAll\(_orderPlacedHandlers"));
        Assert.Contains("services.AddScoped<IMessageHandler<TestApp.OrderPlaced>, TestApp.EmailHandler>();", generated.Registration);
        Assert.Contains("services.AddScoped<IMessageHandler<TestApp.OrderPlaced>, TestApp.AuditHandler>();", generated.Registration);
    }

    [Fact]
    public void VoidMessages_GenerateObjectCatchAllDispatch()
    {
        var source = ContractsSource + @"
namespace TestApp
{
    using Andersoft.CQRS.Abstractions;
    using System.Threading;
    using System.Threading.Tasks;

    public record OrderPlaced { }
    public record OrderShipped { }
    public class P : IMessageHandler<OrderPlaced> { public ValueTask HandleAsync(OrderPlaced m, CancellationToken ct = default) => default; }
    public class S : IMessageHandler<OrderShipped> { public ValueTask HandleAsync(OrderShipped m, CancellationToken ct = default) => default; }
}";
        var generated = RunAndGet(source);

        Assert.Contains("public System.Threading.Tasks.ValueTask DispatchAsync(\n        object message,", generated.Dispatcher);
        Assert.Contains("=> message switch", generated.Dispatcher);
        Assert.Contains("TestApp.OrderPlaced e => DispatchAsync(e, ct),", generated.Dispatcher);
        Assert.Contains("TestApp.OrderShipped e => DispatchAsync(e, ct),", generated.Dispatcher);
        Assert.Contains("_ => default,", generated.Dispatcher);
    }

    [Fact]
    public void NoHandlers_StillEmitsSagaBaseAndDispatcher()
    {
        // The Saga<TState> base is always emitted (post-init) so consumers can extend it, and the
        // dispatcher it references is always emitted alongside — even with no handlers or sagas.
        var source = ContractsSource + @"namespace TestApp { public class PlainClass { } }";
        var generated = RunAndGet(source);

        Assert.Contains("public abstract class Saga<TSagaState> : Saga", generated.SagaBase);
        Assert.Contains("public sealed class TypedDispatcher", generated.Dispatcher);
    }

    [Fact]
    public void ResultInterceptor_GeneratesChain()
    {
        var source = ContractsSource + @"
namespace TestApp
{
    using Andersoft.CQRS;
    using Andersoft.CQRS.Abstractions;
    using System.Threading;
    using System.Threading.Tasks;

    public record GetUserQuery { }
    public class GetUserHandler : IMessageHandler<GetUserQuery, string>
    {
        public ValueTask<string> HandleAsync(GetUserQuery message, CancellationToken ct = default) => new(""Alice"");
    }
    public class LoggingInterceptor : IInterceptHandler<GetUserQuery, string>
    {
        public ValueTask<string> HandleAsync(GetUserQuery m, RequestHandlerDelegate<string> next, CancellationToken ct) => next();
    }
}";
        var generated = RunAndGet(source);

        Assert.Contains("_getUserQueryInterceptors", generated.Dispatcher);
        Assert.Contains("ChainInterceptors(_getUserQueryInterceptors", generated.Dispatcher);
        Assert.Contains("services.AddScoped<IInterceptHandler<TestApp.GetUserQuery, string>, TestApp.LoggingInterceptor>();", generated.Registration);
    }

    [Fact]
    public void VoidInterceptor_GeneratesVoidChain()
    {
        var source = ContractsSource + @"
namespace TestApp
{
    using Andersoft.CQRS;
    using Andersoft.CQRS.Abstractions;
    using System.Threading;
    using System.Threading.Tasks;

    public record CreateOrder { }
    public class CreateOrderHandler : IMessageHandler<CreateOrder>
    {
        public ValueTask HandleAsync(CreateOrder message, CancellationToken ct = default) => default;
    }
    public class ValidateOrder : IInterceptHandler<CreateOrder>
    {
        public ValueTask HandleAsync(CreateOrder m, RequestHandlerDelegate next, CancellationToken ct) => next();
    }
}";
        var generated = RunAndGet(source);

        Assert.Contains("ChainInterceptors(_createOrderInterceptors, message, () => InvokeAll(_createOrderHandlers", generated.Dispatcher);
        Assert.Contains("System.Collections.Generic.List<IInterceptHandler<TestApp.CreateOrder>>", generated.Dispatcher);
        Assert.Contains("services.AddScoped<IInterceptHandler<TestApp.CreateOrder>, TestApp.ValidateOrder>();", generated.Registration);
    }

    [Fact]
    public void OpenGenericInterceptor_AppliesToEveryResultMessage()
    {
        var source = ContractsSource + @"
namespace TestApp
{
    using Andersoft.CQRS;
    using Andersoft.CQRS.Abstractions;
    using System.Threading;
    using System.Threading.Tasks;

    public record GetUserQuery { }
    public class GetUserHandler : IMessageHandler<GetUserQuery, string>
    {
        public ValueTask<string> HandleAsync(GetUserQuery message, CancellationToken ct = default) => new(""Alice"");
    }
    public class LoggingInterceptor<TMessage, TResult> : IInterceptHandler<TMessage, TResult>
    {
        public ValueTask<TResult> HandleAsync(TMessage m, RequestHandlerDelegate<TResult> next, CancellationToken ct) => next();
    }
}";
        var generated = RunAndGet(source);

        Assert.Contains("ChainInterceptors(_getUserQueryInterceptors", generated.Dispatcher);
        Assert.Contains("services.AddScoped(typeof(IInterceptHandler<,>), typeof(TestApp.LoggingInterceptor<,>));", generated.Registration);
    }

    [Fact]
    public void Saga_IsExcludedFromHandlers_AndWiredAsCoordinator()
    {
        var source = ContractsSource + @"
namespace TestApp
{
    using Andersoft.CQRS.Abstractions;
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    public record NodeStarted(Guid ExecutionId);
    public record NodeCompleted(Guid ExecutionId);

    public sealed class WorkflowState : SagaState { }

    // No `partial` — the generated Saga<TState> base carries the dispatcher.
    public sealed class WorkflowSaga
        : Saga<WorkflowState>, IMessageHandler<NodeStarted>, IMessageHandler<NodeCompleted>
    {
        protected override void ConfigureHowToFindSaga(ISagaPropertyMapper<WorkflowState> m)
        {
            m.MapStartedBy<NodeStarted>(e => e.ExecutionId);
            m.MapHandledBy<NodeCompleted>(e => e.ExecutionId);
        }
        public ValueTask HandleAsync(NodeStarted message, CancellationToken ct = default) => default;
        public ValueTask HandleAsync(NodeCompleted message, CancellationToken ct = default) => default;
    }
}";
        var generated = RunAndGet(source);

        // The saga is NOT registered as a direct handler...
        Assert.DoesNotContain("TestApp.WorkflowSaga>();", generated.Registration);
        Assert.DoesNotContain("IMessageHandler<TestApp.NodeStarted>, TestApp.WorkflowSaga>", generated.Registration);

        // ...it's an AddSaga coordinator with a DEFERRED TypedDispatcher factory wired in (resolving
        // it eagerly would form a DI cycle), and its events flow through SagaDispatcher fan-out.
        Assert.Contains("services.AddSaga<TestApp.WorkflowSaga, TestApp.WorkflowState>(", generated.Registration);
        Assert.Contains("static (saga, sp) => saga.DispatcherFactory = () => sp.GetRequiredService<TypedDispatcher>());", generated.Registration);
        Assert.Contains("new Andersoft.CQRS.EntityFrameworkCore.SagaDispatcher<TestApp.NodeStarted>(", generated.Registration);
        Assert.Contains("new Andersoft.CQRS.EntityFrameworkCore.SagaDispatcher<TestApp.NodeCompleted>(", generated.Registration);

        // Saga events appear as void messages in the dispatcher.
        Assert.Contains("InvokeAll(_nodeStartedHandlers, message, ct);", generated.Dispatcher);

        // EF model config for the state type is generated for OnModelCreating.
        Assert.Contains("public static Microsoft.EntityFrameworkCore.ModelBuilder ApplySagaConfigurations(", generated.Registration);
        Assert.Contains("modelBuilder.ConfigureSagaState<TestApp.WorkflowState>();", generated.Registration);

        // The generated Saga<TState> base (always emitted) carries a deferred dispatcher factory that
        // the configure callback wires up, plus a lazy Dispatcher getter — no `partial` required on
        // the consumer's saga.
        Assert.Contains("public abstract class Saga<TSagaState> : Saga", generated.SagaBase);
        Assert.Contains("global::System.Func<global::Andersoft.CQRS.TypedDispatcher> DispatcherFactory { get; internal set; }", generated.SagaBase);
        Assert.Contains("global::Andersoft.CQRS.TypedDispatcher Dispatcher => DispatcherFactory();", generated.SagaBase);
    }

    // ── harness ────────────────────────────────────────────────────────

    private sealed class Generated
    {
        public string Dispatcher { get; init; } = string.Empty;
        public string Registration { get; init; } = string.Empty;
        public string SagaBase { get; init; } = string.Empty;
    }

    private static Generated RunAndGet(string source)
    {
        var (genResult, _) = Run(source);
        var generated = genResult.Results[0].GeneratedSources;
        return new Generated
        {
            Dispatcher = generated.Single(s => s.HintName == "TypedDispatcher.g.cs").SourceText.ToString(),
            Registration = generated.Single(s => s.HintName == "HandlerRegistration.g.cs").SourceText.ToString(),
            SagaBase = generated.Single(s => s.HintName == "SagaBase.g.cs").SourceText.ToString(),
        };
    }

    private static (GeneratorDriverRunResult, Compilation) Run(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, ParseOptions);

        var references = ImmutableArray.Create<MetadataReference>(
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // The input is intentionally NOT required to compile standalone: a saga extends the
        // generator-provided Saga<TState> base, which is absent until the generator runs (and
        // generators run fine against compilations with errors). Generation correctness is asserted
        // on the emitted text instead.
        var generator = new DispatcherGenerator();
        // Match the input's parse options so post-initialization sources don't trip the
        // "inconsistent language versions" guard when added to the compilation.
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { generator.AsSourceGenerator() },
            parseOptions: ParseOptions);
        driver = driver.RunGenerators(compilation);
        return (driver.GetRunResult(), compilation);
    }
}
