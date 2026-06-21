using System;

namespace Andersoft.CQRS.Abstractions;

/// <summary>
/// Base class for saga state persisted by <see cref="ISagaRepository{TState}"/>.
/// </summary>
public abstract class SagaState
{
    /// <summary>
    /// Surrogate primary key identifying this saga instance. Store-generated on insert;
    /// it is <em>not</em> used to route events. Events are correlated to an instance by
    /// matching their key to a saga-state field declared in <c>ConfigureHowToFindSaga</c>
    /// (see <c>MapStartedBy</c>/<c>MapHandledBy</c>), each of which is backed by a unique index.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Optimistic concurrency token. Incremented on each save.
    /// </summary>
    public uint Version { get; set; }
}
