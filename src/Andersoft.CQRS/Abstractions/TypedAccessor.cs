using System;
using System.Threading;
using System.Threading.Tasks;

namespace Andersoft.CQRS.Abstractions;

/// <summary>
/// Typed accessor wrapping <see cref="SagaStateAccessor{TSagaState}"/>
/// for the non‑generic <see cref="Saga.Accessor"/> property.
/// </summary>
internal sealed class TypedAccessor<TSagaState> : ISagaStateAccessor
    where TSagaState : SagaState, new()
{
    private readonly SagaStateAccessor<TSagaState> _inner;

    public TypedAccessor(SagaStateAccessor<TSagaState> inner) => _inner = inner;

    public TSagaState? State => _inner.Data;

    public bool IsNew => _inner.IsNew;
    public bool IsStarted => _inner.IsStarted;

    public async ValueTask<object> LoadOrCreateAsync(Guid correlationId, CancellationToken ct)
        => await _inner.LoadOrCreateAsync(correlationId, ct);

    public async ValueTask<object?> LoadAsync(Guid correlationId, CancellationToken ct)
        => await _inner.LoadAsync(correlationId, ct);

    public ValueTask SaveAsync(CancellationToken ct) => _inner.SaveAsync(ct);
    public void MarkAsComplete() => _inner.MarkAsComplete();
}
