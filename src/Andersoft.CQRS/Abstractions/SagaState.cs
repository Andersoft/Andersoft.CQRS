using System;

namespace Andersoft.CQRS.Abstractions;

/// <summary>
/// Base class for saga state persisted by <see cref="ISagaRepository{TState}"/>.
/// </summary>
public abstract class SagaState
{
    /// <summary>
    /// Uniquely identifies this saga instance. Events are routed to a saga
    /// by matching their correlation property to this value.
    /// </summary>
    public Guid CorrelationId { get; set; }

    /// <summary>
    /// Optimistic concurrency token. Incremented on each save.
    /// </summary>
    public uint Version { get; set; }
}
