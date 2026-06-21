using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Andersoft.CQRS.Abstractions;

namespace Andersoft.CQRS.EntityFrameworkCore;

/// <summary>
/// Generic event handler that dispatches <typeparamref name="TEvent"/> to
/// all registered <see cref="Saga"/> instances. Auto-loads state, calls the
/// saga handler, and auto-saves.
/// </summary>
internal sealed class SagaDispatcher<TEvent>
{
    private readonly IEnumerable<Andersoft.CQRS.Abstractions.Saga> _sagas;

    public SagaDispatcher(IEnumerable<Andersoft.CQRS.Abstractions.Saga> sagas)
    {
        _sagas = sagas;
    }

    public async ValueTask HandleAsync(TEvent domainEvent, CancellationToken ct)
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
