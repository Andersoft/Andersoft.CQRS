using System.Threading;
using System.Threading.Tasks;

namespace Andersoft.CQRS.Abstractions;

/// <summary>Pipeline behaviour around a message that returns a <typeparamref name="TResult"/>.</summary>
public interface IInterceptHandler<in TMessage, TResult>
{
    ValueTask<TResult> HandleAsync(TMessage message, RequestHandlerDelegate<TResult> next, CancellationToken ct);
}

/// <summary>Pipeline behaviour around a message that returns no value.</summary>
public interface IInterceptHandler<in TMessage>
{
    ValueTask HandleAsync(TMessage message, RequestHandlerDelegate next, CancellationToken ct);
}
