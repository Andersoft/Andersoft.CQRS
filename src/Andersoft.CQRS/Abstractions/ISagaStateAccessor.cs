using System;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace Andersoft.CQRS.Abstractions;

public interface ISagaStateAccessor
{
    bool IsNew { get; }
    bool IsStarted { get; }

    /// <summary>
    /// Loads the instance matching <paramref name="match"/>, or creates a new one and runs
    /// <paramref name="initialize"/> against it (used by started-by mappings to set the correlation field).
    /// </summary>
    ValueTask<object> LoadOrCreateAsync(LambdaExpression match, Action<object> initialize, CancellationToken ct);

    /// <summary>Loads the instance matching <paramref name="match"/>, or null if none exists.</summary>
    ValueTask<object?> LoadAsync(LambdaExpression match, CancellationToken ct);

    ValueTask SaveAsync(CancellationToken ct);
    void MarkAsComplete();
}
