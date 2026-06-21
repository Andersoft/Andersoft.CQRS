using System;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace Andersoft.CQRS.Abstractions;

internal sealed class SagaHandlerRegistration
{
    public bool IsStartedBy { get; set; }

    /// <summary>
    /// Builds the find-predicate for a given event: an <c>Expression&lt;Func&lt;TState, bool&gt;&gt;</c>
    /// (typed as the non-generic <see cref="LambdaExpression"/> so it can live on the non-generic
    /// <see cref="Saga"/>) that matches the configured saga-state field against the event's key.
    /// </summary>
    public Func<object, LambdaExpression> BuildPredicate { get; set; } = null!;

    /// <summary>
    /// Initializes a newly created saga instance with the event's key — sets the correlation field
    /// mapped by <c>MapStartedBy</c>. Null for <c>MapHandledBy</c> (find-only) registrations.
    /// </summary>
    public Action<object, object>? InitializeState { get; set; }

    public Func<object, object, CancellationToken, ValueTask> Handler { get; set; } = null!;
}
