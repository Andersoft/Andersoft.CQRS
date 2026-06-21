using System;
using System.Threading;
using System.Threading.Tasks;

namespace Andersoft.CQRS.Abstractions;

/// <summary>
/// Scoped accessor that owns saga state for the current unit of work.
/// Tracks lifecycle: IsNew (just created), IsStarted (state loaded/exists),
/// and MarkAsComplete (deletes state on next save).
/// </summary>
public sealed class SagaStateAccessor<TSagaState>
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
    /// Loads existing state or creates a new one with the given correlation ID.
    /// Sets <see cref="IsNew"/> to true when a new instance is created.
    /// </summary>
    public async ValueTask<TSagaState> LoadOrCreateAsync(Guid correlationId, CancellationToken ct = default)
    {
        var existing = await _repository.LoadAsync(correlationId, ct);
        if (existing is not null)
        {
            Data = existing;
            IsNew = false;
            return existing;
        }

        Data = new TSagaState { CorrelationId = correlationId };
        IsNew = true;
        return Data;
    }

    /// <summary>
    /// Loads existing state by correlation ID. Returns null if none exists.
    /// </summary>
    public async ValueTask<TSagaState?> LoadAsync(Guid correlationId, CancellationToken ct = default)
    {
        Data = await _repository.LoadAsync(correlationId, ct);
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
