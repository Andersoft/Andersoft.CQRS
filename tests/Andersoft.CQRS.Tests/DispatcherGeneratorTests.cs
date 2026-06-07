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

    private const string ContractsSource = @"
namespace Andersoft.CQRS.Abstractions
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    public interface IQuery<TResult> { }
    public interface ICommand<TResult> { }
    public interface IQueryHandler<in TQuery, TResult> where TQuery : IQuery<TResult> { Task<TResult> HandleAsync(TQuery query, CancellationToken ct = default); }
    public interface ICommandHandler<in TCommand, TResult> where TCommand : ICommand<TResult> { Task<TResult> HandleAsync(TCommand command, CancellationToken ct = default); }
    public interface IInterceptHandler<in TMessage, TResult> { ValueTask<TResult> HandleAsync(TMessage message, RequestHandlerDelegate<TResult> next, CancellationToken ct); }
    public delegate ValueTask<TResult> RequestHandlerDelegate<TResult>();
    public interface IApplicationChannelDispatcher { ValueTask<TResult> DispatchAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> operation, CancellationToken ct, string operationName); }
    public interface IDomainEventHandler<in TEvent> { Task HandleAsync(TEvent domainEvent, CancellationToken ct = default); }
}";

    [Fact]
    public void QueryAndHandler_GeneratesDispatchAndRegistration()
    {
        var source = ContractsSource + @"

namespace TestApp
{
    using Andersoft.CQRS.Abstractions;
    using System.Threading;
    using System.Threading.Tasks;

    public record GetUserQuery : IQuery<string> { }
    public class GetUserHandler : IQueryHandler<GetUserQuery, string>
    {
        public Task<string> HandleAsync(GetUserQuery query, CancellationToken ct = default)
            => Task.FromResult(""Alice"");
    }
}";

        var (genResult, _) = Run(source);

        var generated = genResult.Results[0].GeneratedSources;
        Assert.Equal(2, generated.Length);

        var dispatcherSrc = generated.Single(s => s.HintName == "TypedDispatcher.g.cs").SourceText.ToString();
        Assert.Contains("public sealed class TypedDispatcher", dispatcherSrc);
        Assert.Contains("IQueryHandler<TestApp.GetUserQuery, string>", dispatcherSrc);

        var regSrc = generated.Single(s => s.HintName == "HandlerRegistration.g.cs").SourceText.ToString();
        Assert.Contains("AddApplicationHandlers", regSrc);
        Assert.Contains("TestApp.GetUserHandler", regSrc);
    }

    [Fact]
    public void CommandAndHandler_GeneratesDispatch()
    {
        var source = ContractsSource + @"

namespace TestApp
{
    using Andersoft.CQRS.Abstractions;
    using System.Threading;
    using System.Threading.Tasks;

    public record CreateOrderCommand : ICommand<int> { }
    public class CreateOrderHandler : ICommandHandler<CreateOrderCommand, int>
    {
        public Task<int> HandleAsync(CreateOrderCommand command, CancellationToken ct = default)
            => Task.FromResult(42);
    }
}";

        var (genResult, _) = Run(source);
        var generated = genResult.Results[0].GeneratedSources;

        var dispatcherSrc = generated.Single(s => s.HintName == "TypedDispatcher.g.cs").SourceText.ToString();
        Assert.Contains("ICommandHandler<TestApp.CreateOrderCommand, int>", dispatcherSrc);
    }

    [Fact]
    public void NoCqrsTypes_GeneratesNoOutput()
    {
        var source = @"namespace TestApp { public class PlainClass { } }";

        var (genResult, _) = Run(source);

        Assert.Empty(genResult.Results[0].GeneratedSources);
    }

    [Fact]
    public void Interceptor_GeneratesInterceptorLogic()
    {
        var source = ContractsSource + @"

namespace TestApp
{
    using Andersoft.CQRS.Abstractions;
    using System.Threading;
    using System.Threading.Tasks;

    public record GetUserQuery : IQuery<string> { }
    public class GetUserHandler : IQueryHandler<GetUserQuery, string>
    {
        public Task<string> HandleAsync(GetUserQuery query, CancellationToken ct = default)
            => Task.FromResult(""Alice"");
    }
    public class LoggingInterceptor : IInterceptHandler<GetUserQuery, string>
    {
        public ValueTask<string> HandleAsync(GetUserQuery msg, RequestHandlerDelegate<string> next, CancellationToken ct) => next();
    }
}";

        var (genResult, _) = Run(source);
        var generated = genResult.Results[0].GeneratedSources;

        var dispatcherSrc = generated.Single(s => s.HintName == "TypedDispatcher.g.cs").SourceText.ToString();
        Assert.Contains("Interceptors", dispatcherSrc);
        Assert.Contains("ChainInterceptors", dispatcherSrc);
    }

    [Fact]
    public void DomainEventHandler_GeneratesRegistration()
    {
        var source = ContractsSource + @"

namespace TestApp
{
    using Andersoft.CQRS.Abstractions;
    using System.Threading;
    using System.Threading.Tasks;

    public record UserCreatedEvent { }
    public class UserCreatedHandler : IDomainEventHandler<UserCreatedEvent>
    {
        public Task HandleAsync(UserCreatedEvent e, CancellationToken ct = default) => Task.CompletedTask;
    }
}";

        var (genResult, _) = Run(source);
        var generated = genResult.Results[0].GeneratedSources;

        var regSrc = generated.Single(s => s.HintName == "HandlerRegistration.g.cs").SourceText.ToString();
        Assert.Contains("IDomainEventHandler<TestApp.UserCreatedEvent>", regSrc);
    }

    [Fact]
    public void MultipleQueries_GeneratesAllDispatchMethods()
    {
        var source = ContractsSource + @"

namespace TestApp
{
    using Andersoft.CQRS.Abstractions;
    using System.Threading;
    using System.Threading.Tasks;

    public record GetUserQuery : IQuery<string> { }
    public record ListOrdersQuery : IQuery<int> { }
    public class GetUserHandler : IQueryHandler<GetUserQuery, string>
    {
        public Task<string> HandleAsync(GetUserQuery query, CancellationToken ct = default) => Task.FromResult(""Alice"");
    }
    public class ListOrdersHandler : IQueryHandler<ListOrdersQuery, int>
    {
        public Task<int> HandleAsync(ListOrdersQuery query, CancellationToken ct = default) => Task.FromResult(10);
    }
}";

        var (genResult, _) = Run(source);
        var generated = genResult.Results[0].GeneratedSources;

        var dispatcherSrc = generated.Single(s => s.HintName == "TypedDispatcher.g.cs").SourceText.ToString();
        Assert.Contains("GetUserQuery", dispatcherSrc);
        Assert.Contains("ListOrdersQuery", dispatcherSrc);
    }

    [Fact]
    public void MixedQueryAndCommand_GeneratesBothDispatchMethods()
    {
        var source = ContractsSource + @"

namespace TestApp
{
    using Andersoft.CQRS.Abstractions;
    using System.Threading;
    using System.Threading.Tasks;

    public record GetUserQuery : IQuery<string> { }
    public record CreateUserCommand : ICommand<int> { }
    public class GetUserHandler : IQueryHandler<GetUserQuery, string>
    {
        public Task<string> HandleAsync(GetUserQuery query, CancellationToken ct = default) => Task.FromResult(""Alice"");
    }
    public class CreateUserHandler : ICommandHandler<CreateUserCommand, int>
    {
        public Task<int> HandleAsync(CreateUserCommand command, CancellationToken ct = default) => Task.FromResult(1);
    }
}";

        var (genResult, _) = Run(source);
        var generated = genResult.Results[0].GeneratedSources;

        var dispatcherSrc = generated.Single(s => s.HintName == "TypedDispatcher.g.cs").SourceText.ToString();
        Assert.Contains("IQueryHandler<TestApp.GetUserQuery, string>", dispatcherSrc);
        Assert.Contains("ICommandHandler<TestApp.CreateUserCommand, int>", dispatcherSrc);
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

        var diags = compilation.GetDiagnostics();
        if (diags.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            var err = string.Join("\n", diags.Where(d => d.Severity == DiagnosticSeverity.Error));
            throw new Exception($"Compilation errors: {err}");
        }

        var generator = new DispatcherGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGenerators(compilation);
        var runResult = driver.GetRunResult();

        return (runResult, compilation);
    }
}
