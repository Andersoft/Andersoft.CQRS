using System;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace Andersoft.CQRS.Abstractions;

/// <summary>
/// Persists and retrieves saga state. Events are correlated to an instance by a predicate over
/// saga-state fields (built from <c>MapStartedBy</c>/<c>MapHandledBy</c>) rather than a fixed key.
/// Implementations are responsible for optimistic concurrency (typically via
/// <see cref="SagaState.Version"/>).
/// </summary>
/// <typeparam name="TState">The saga state type, derived from <see cref="SagaState"/>.</typeparam>
public interface ISagaRepository<TState>
    where TState : SagaState
{
    /// <summary>
    /// Loads the single saga state matching <paramref name="match"/>.
    /// Returns <c>null</c> if no saga exists — typically means the saga hasn't
    /// been created yet and the current event is the initial event.
    /// </summary>
    ValueTask<TState?> LoadAsync(Expression<Func<TState, bool>> match, CancellationToken ct = default);

    /// <summary>
    /// Persists the saga state. Implementations must handle both insert (new saga)
    /// and update (existing saga). Optimistic concurrency failures should throw
    /// <see cref="InvalidOperationException"/> or a concurrency-specific exception.
    /// </summary>
    ValueTask SaveAsync(TState state, CancellationToken ct = default);

    /// <summary>
    /// Deletes the saga state. Called when <see cref="SagaState.MarkAsComplete"/>
    /// has been invoked — the saga instance is no longer needed.
    /// </summary>
    ValueTask DeleteAsync(TState state, CancellationToken ct = default);
}
