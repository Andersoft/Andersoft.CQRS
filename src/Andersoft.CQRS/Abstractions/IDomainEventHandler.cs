using System.Threading;
using System.Threading.Tasks;

namespace Andersoft.CQRS.Abstractions;

public interface IDomainEventHandler<in TEvent>
{
    ValueTask HandleAsync(TEvent domainEvent, CancellationToken ct = default);
}
