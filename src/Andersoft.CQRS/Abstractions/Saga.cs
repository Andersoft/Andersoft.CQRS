using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Andersoft.CQRS.Abstractions;

// ── Non‑generic base — registered in DI for SagaDispatcher ─────────────

/// <summary>
/// Non-generic saga base registered in DI so <see cref="SagaDispatcher{TEvent}"/>
/// can resolve all sagas as <c>IEnumerable&lt;Saga&gt;</c>.
/// </summary>
public abstract class Saga
{
    internal Dictionary<Type, SagaHandlerRegistration> Handlers { get; } = new();
    internal ISagaStateAccessor Accessor { get; set; } = null!;

    /// <summary>True if the current event created a new saga instance.</summary>
    public bool IsNew => Accessor.IsNew;

    /// <summary>True after state has been loaded or created.</summary>
    public bool IsStarted => Accessor.IsStarted;

    /// <summary>Marks the saga as complete. State is deleted on next save.</summary>
    public void MarkAsComplete() => Accessor.MarkAsComplete();
}

// ── Generic typed base — concrete sagas extend this ────────────────────

/// <summary>
/// Typed saga base. Concrete sagas extend this and register handlers via
/// <see cref="ConfigureHowToFindSaga"/>.
///
/// A saga is a coordinator over a grouping of message handlers — it implements
/// <see cref="IMessageHandler{TEvent}"/> for each event it handles and registers only
/// the correlation in <see cref="ConfigureHowToFindSaga"/>. It is never dispatched to
/// directly; the generated registration wires a <see cref="SagaDispatcher{TEvent}"/>
/// onto each event's handler fan-out, which loads state, finds the correlated saga,
/// invokes its handler, and saves.
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
///         if (IsNew) Data.CustomerId = e.CustomerId;
///         return default;
///     }
///
///     public ValueTask HandleAsync(CompleteOrder e, CancellationToken ct)
///     {
///         MarkAsComplete();
///         return default;
///     }
/// }
/// </code>
/// </summary>
public abstract class Saga<TSagaState> : Saga
    where TSagaState : SagaState, new()
{
    /// <summary>
    /// Override to register event handlers and correlation mappings.
    /// Called once per saga type during construction.
    /// </summary>
    protected abstract void ConfigureHowToFindSaga(ISagaPropertyMapper<TSagaState> mapper);

    /// <summary>Pre‑loaded saga data. Null only before any event is handled.</summary>
    protected TSagaState? Data => Accessor is SagaStateAccessor<TSagaState> a ? a.Data : null;

    /// <summary>
    /// Maps an event to a saga. Use <c>MapStartedBy</c> for events that can
    /// create a new saga instance; <c>MapHandledBy</c> for events that require
    /// an existing saga.
    ///
    /// You only register the correlation here — the handler is the saga's
    /// <see cref="IMessageHandler{TEvent}.HandleAsync"/> implementation, bound
    /// automatically. State is available via the <see cref="Data"/> property.
    /// </summary>
    protected interface ISagaPropertyMapper<out TState>
    {
        /// <summary>
        /// Maps <typeparamref name="TEvent"/> to a correlation ID for an event that
        /// can START a new saga instance. If no existing saga is found for the
        /// correlation ID, a new one is created. The saga's
        /// <c>IMessageHandler&lt;<typeparamref name="TEvent"/>&gt;.HandleAsync</c>
        /// implementation is invoked.
        /// </summary>
        void MapStartedBy<TEvent>(Func<TEvent, Guid> correlation);

        /// <summary>
        /// Maps <typeparamref name="TEvent"/> to a correlation ID for an event that
        /// goes to an EXISTING saga instance. If no saga is found, the event is
        /// discarded. The saga's
        /// <c>IMessageHandler&lt;<typeparamref name="TEvent"/>&gt;.HandleAsync</c>
        /// implementation is invoked.
        /// </summary>
        void MapHandledBy<TEvent>(Func<TEvent, Guid> correlation);
    }

    internal void BuildHandlers()
    {
        var mapper = new Mapper(this);
        ConfigureHowToFindSaga(mapper);
    }

    private sealed class Mapper : ISagaPropertyMapper<TSagaState>
    {
        private readonly Saga<TSagaState> _saga;

        public Mapper(Saga<TSagaState> saga) => _saga = saga;

        public void MapStartedBy<TEvent>(Func<TEvent, Guid> correlation)
            => Register<TEvent>(correlation, isStartedBy: true);

        public void MapHandledBy<TEvent>(Func<TEvent, Guid> correlation)
            => Register<TEvent>(correlation, isStartedBy: false);

        // Binds the handler with a plain generic-interface type check. No reflection,
        // GetMethod, or CreateDelegate — fully AOT/trim-safe. State is reached via the
        // saga's Data property, so the handler takes no state parameter.
        private void Register<TEvent>(Func<TEvent, Guid> correlation, bool isStartedBy)
        {
            if (_saga is not IMessageHandler<TEvent> handler)
            {
                throw new InvalidOperationException(
                    $"Saga '{_saga.GetType().Name}' maps '{typeof(TEvent).Name}' but does not implement " +
                    $"IMessageHandler<{typeof(TEvent).Name}>. Add the interface and a " +
                    $"'ValueTask HandleAsync({typeof(TEvent).Name} message, CancellationToken ct)' method.");
            }

            _saga.Handlers[typeof(TEvent)] = new SagaHandlerRegistration
            {
                IsStartedBy = isStartedBy,
                GetCorrelationId = e => correlation((TEvent)e),
                Handler = (e, _, ct) => handler.HandleAsync((TEvent)e, ct),
            };
        }
    }
}
