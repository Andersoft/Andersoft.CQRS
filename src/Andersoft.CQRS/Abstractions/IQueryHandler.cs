using System.Threading;
using System.Threading.Tasks;

namespace Andersoft.CQRS.Abstractions;

public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery<TResult>
{
  ValueTask<TResult> HandleAsync(TQuery query, CancellationToken ct = default);
}