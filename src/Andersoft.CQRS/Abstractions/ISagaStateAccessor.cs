using System;
using System.Threading;
using System.Threading.Tasks;

namespace Andersoft.CQRS.Abstractions;

public interface ISagaStateAccessor
{
    bool IsNew { get; }
    bool IsStarted { get; }
    ValueTask<object> LoadOrCreateAsync(Guid correlationId, CancellationToken ct);
    ValueTask<object?> LoadAsync(Guid correlationId, CancellationToken ct);
    ValueTask SaveAsync(CancellationToken ct);
    void MarkAsComplete();
}