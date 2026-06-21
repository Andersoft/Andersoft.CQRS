using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Andersoft.CQRS.Abstractions;

namespace Andersoft.CQRS.EntityFrameworkCore;

/// <summary>
/// The coordinator handler for a saga event. Registered as an
/// <see cref="Andersoft.CQRS.Abstractions.IMessageHandler{TEvent}"/> on the event's
/// fan-out, it dispatches <typeparamref name="TEvent"/> to all registered
/// <see cref="Saga"/> instances — auto-loading state, calling the saga handler, and
/// auto-saving. The saga itself is never registered as a direct handler.
/// </summary>
public sealed class SagaDispatcher<TEvent> : Andersoft.CQRS.Abstractions.IMessageHandler<TEvent>
{
    private readonly IEnumerable<Andersoft.CQRS.Abstractions.Saga> _sagas;

    public SagaDispatcher(IEnumerable<Andersoft.CQRS.Abstractions.Saga> sagas)
    {
        _sagas = sagas;
    }

    public async ValueTask HandleAsync(TEvent domainEvent, CancellationToken ct = default)
    {
        foreach (var saga in _sagas)
        {
            if (!saga.Handlers.TryGetValue(typeof(TEvent), out var reg))
                continue;

            var correlationId = reg.GetCorrelationId(domainEvent!);

            if (reg.IsStartedBy)
            {
                var state = await saga.Accessor.LoadOrCreateAsync(correlationId, ct);
                await reg.Handler(domainEvent!, state, ct);
                await saga.Accessor.SaveAsync(ct);
            }
            else
            {
                var state = await saga.Accessor.LoadAsync(correlationId, ct);
                if (!saga.IsStarted) return;
                await reg.Handler(domainEvent!, state!, ct);
                await saga.Accessor.SaveAsync(ct);
            }

            return;
        }
    }
}
