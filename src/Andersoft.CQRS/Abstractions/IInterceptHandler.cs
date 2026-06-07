using System.Threading;
using System.Threading.Tasks;

namespace Andersoft.CQRS.Abstractions;

public interface IInterceptHandler<in TMessage, TResult>
{
    ValueTask<TResult> HandleAsync(TMessage message, RequestHandlerDelegate<TResult> next, CancellationToken ct);
}