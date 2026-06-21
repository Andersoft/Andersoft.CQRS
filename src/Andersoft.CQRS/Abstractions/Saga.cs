using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Andersoft.CQRS.Abstractions;

/// <summary>
/// Non-generic saga base registered in DI so <see cref="SagaDispatcher{TEvent}"/> can resolve all
/// sagas as <c>IEnumerable&lt;Saga&gt;</c>.
///
/// <para>
/// The generic, consumer-facing <c>Saga&lt;TState&gt;</c> base is <b>source-generated</b> into the
/// consumer's assembly so it can expose the generated <c>TypedDispatcher</c> (a type that only
/// exists there). It derives from this class and overrides <see cref="BuildHandlers"/>. Everything
/// the coordinator machinery needs — the handler table, the state accessor, lifecycle flags — lives
/// here, none of it dependent on <c>TypedDispatcher</c>. Concrete sagas extend the generated
/// <c>Saga&lt;TState&gt;</c>:
/// </para>
/// <code>
/// public sealed class OrderSaga
///     : Saga&lt;OrderSagaState&gt;,
///       IMessageHandler&lt;StartOrder&gt;,
///       IMessageHandler&lt;CompleteOrder&gt;
/// {
///     protected override void ConfigureHowToFindSaga(ISagaPropertyMapper&lt;OrderSagaState&gt; m)
///     {
///         m.MapStartedBy&lt;StartOrder&gt;(e => e.OrderId);
///         m.MapHandledBy&lt;CompleteOrder&gt;(e => e.OrderId);
///     }
///
///     public ValueTask HandleAsync(StartOrder e, CancellationToken ct)
///     {
///         if (IsNew) Data!.CustomerId = e.CustomerId;
///         return default;
///     }
///
///     public async ValueTask HandleAsync(CompleteOrder e, CancellationToken ct)
///     {
///         await Dispatcher.DispatchAsync(new SendReceipt(e.OrderId), ct);
///         MarkAsComplete();
///     }
/// }
/// </code>
/// </summary>
public abstract class Saga
{
    internal Dictionary<Type, SagaHandlerRegistration> Handlers { get; } = new();

    // protected internal: the generated Saga<TState> (a different assembly) reaches this via the
    // protected facet; the EntityFrameworkCore registration reaches it via the internal facet
    // (InternalsVisibleTo) when wiring the accessor.
    protected internal ISagaStateAccessor Accessor { get; set; } = null!;

    /// <summary>True if the current event created a new saga instance.</summary>
    public bool IsNew => Accessor.IsNew;

    /// <summary>True after state has been loaded or created.</summary>
    public bool IsStarted => Accessor.IsStarted;

    /// <summary>Marks the saga as complete. State is deleted on next save.</summary>
    public void MarkAsComplete() => Accessor.MarkAsComplete();

    /// <summary>
    /// Builds the saga's correlation handler table from <c>ConfigureHowToFindSaga</c>. Implemented by
    /// the generated <c>Saga&lt;TState&gt;</c> base and invoked once during registration.
    /// </summary>
    protected internal abstract void BuildHandlers();

    /// <summary>
    /// Registers the correlation and handler for <typeparamref name="TEvent"/>. Called by the
    /// generated saga base's mapper — it lives here so <see cref="SagaHandlerRegistration"/> can stay
    /// internal to this assembly rather than being constructed in generated consumer code.
    /// </summary>
    /// <remarks>
    /// Binds the handler with a plain generic-interface type check. No reflection, GetMethod, or
    /// CreateDelegate — fully AOT/trim-safe. State is reached via the saga's Data property, so the
    /// handler takes no state parameter.
    /// </remarks>
    protected internal void RegisterSagaHandler<TEvent>(Func<TEvent, Guid> correlation, bool isStartedBy)
    {
        if (this is not IMessageHandler<TEvent> handler)
        {
            throw new InvalidOperationException(
                $"Saga '{GetType().Name}' maps '{typeof(TEvent).Name}' but does not implement " +
                $"IMessageHandler<{typeof(TEvent).Name}>. Add the interface and a " +
                $"'ValueTask HandleAsync({typeof(TEvent).Name} message, CancellationToken ct)' method.");
        }

        Handlers[typeof(TEvent)] = new SagaHandlerRegistration
        {
            IsStartedBy = isStartedBy,
            GetCorrelationId = e => correlation((TEvent)e),
            Handler = (e, _, ct) => handler.HandleAsync((TEvent)e, ct),
        };
    }
}
