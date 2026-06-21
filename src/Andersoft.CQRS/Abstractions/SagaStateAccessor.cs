using System;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace Andersoft.CQRS.Abstractions;

/// <summary>
/// Scoped accessor that owns saga state for the current unit of work.
/// Tracks lifecycle: IsNew (just created), IsStarted (state loaded/exists),
/// and MarkAsComplete (deletes state on next save).
/// </summary>
public sealed class SagaStateAccessor<TSagaState> : ISagaStateAccessor
    where TSagaState : SagaState, new()
{
    private readonly ISagaRepository<TSagaState> _repository;
    private bool _completed;

    public SagaStateAccessor(ISagaRepository<TSagaState> repository)
    {
        _repository = repository;
    }

    /// <summary>The currently loaded saga data, or null if not yet loaded.</summary>
    public TSagaState? Data { get; private set; }

    /// <summary>True if the saga was just created (no existing state found).</summary>
    public bool IsNew { get; private set; }

    /// <summary>True if saga state exists (either loaded from store or just created).</summary>
    public bool IsStarted => Data is not null;

    /// <summary>True if MarkAsComplete was called — state will be deleted on save.</summary>
    public bool IsCompleted => _completed;

    /// <summary>
    /// Loads the instance matching <paramref name="match"/>, or creates a new one and runs
    /// <paramref name="initialize"/> against it (to set the mapped correlation field). Sets
    /// <see cref="IsNew"/> to true when a new instance is created. The <c>Id</c> primary key is
    /// store-generated, so it is left unset here and assigned on the first <see cref="SaveAsync"/>.
    /// </summary>
    public async ValueTask<TSagaState> LoadOrCreateAsync(
        Expression<Func<TSagaState, bool>> match,
        Action<TSagaState> initialize,
        CancellationToken ct = default)
    {
        var existing = await _repository.LoadAsync(match, ct);
        if (existing is not null)
        {
            Data = existing;
            IsNew = false;
            return existing;
        }

        Data = new TSagaState();
        initialize(Data);
        IsNew = true;
        return Data;
    }

    /// <summary>
    /// Loads existing state matching <paramref name="match"/>. Returns null if none exists.
    /// </summary>
    public async ValueTask<TSagaState?> LoadAsync(
        Expression<Func<TSagaState, bool>> match,
        CancellationToken ct = default)
    {
        Data = await _repository.LoadAsync(match, ct);
        IsNew = false;
        return Data;
    }

    /// <summary>
    /// Marks the saga as complete. On the next <see cref="SaveAsync"/> call,
    /// the state will be deleted rather than persisted.
    /// </summary>
    public void MarkAsComplete()
    {
        _completed = true;
    }

    // Non-generic bridge for the saga lifecycle (SagaDispatcher works through ISagaStateAccessor).
    // IsNew/IsStarted/SaveAsync/MarkAsComplete are satisfied implicitly by the members above.
    // The non-generic match expression is the Expression<Func<TSagaState, bool>> built by the
    // saga's registration, so the cast always succeeds.
    async ValueTask<object> ISagaStateAccessor.LoadOrCreateAsync(LambdaExpression match, Action<object> initialize, CancellationToken ct)
        => await LoadOrCreateAsync((Expression<Func<TSagaState, bool>>)match, s => initialize(s), ct);

    async ValueTask<object?> ISagaStateAccessor.LoadAsync(LambdaExpression match, CancellationToken ct)
        => await LoadAsync((Expression<Func<TSagaState, bool>>)match, ct);

    /// <summary>
    /// Persists the current state. If <see cref="MarkAsComplete"/> was called,
    /// deletes the state instead.
    /// </summary>
    public async ValueTask SaveAsync(CancellationToken ct = default)
    {
        if (_completed)
        {
            if (Data is not null)
                await _repository.DeleteAsync(Data, ct);
            return;
        }

        if (Data is not null)
            await _repository.SaveAsync(Data, ct);
    }
}
