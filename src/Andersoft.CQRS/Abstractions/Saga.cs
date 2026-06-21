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
/// Usage — ZERO boilerplate, no IDomainEventHandler, no load/save calls:
/// <code>
/// public sealed class OrderSaga : Saga&lt;OrderSagaState&gt;
/// {
///     protected override void ConfigureHowToFindSaga(ISagaPropertyMapper&lt;OrderSagaState&gt; m)
///     {
///         m.MapStartedBy&lt;StartOrder&gt;(e => e.OrderId);
///         m.MapHandledBy&lt;CompleteOrder&gt;(e => e.OrderId);
///     }
///
///     public ValueTask Handle(StartOrder e, CancellationToken ct)
///     {
///         if (IsNew) Data.CustomerId = e.CustomerId;
///         return default;
///     }
///
///     public ValueTask Handle(CompleteOrder e, CancellationToken ct)
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
    protected TSagaState? Data => Accessor is TypedAccessor<TSagaState> a ? a.State : null;

    /// <summary>
    /// Maps an event to a saga. Use <c>MapStartedBy</c> for events that can
    /// create a new saga instance; <c>MapHandledBy</c> for events that require
    /// an existing saga.
    /// </summary>
    protected interface ISagaPropertyMapper<out TState>
    {
        /// <summary>
        /// Registers a handler for <typeparamref name="TEvent"/> that can START
        /// a new saga instance. If no existing saga is found for the correlation
        /// ID, a new one is created.
        /// </summary>
        void MapStartedBy<TEvent>(Func<TEvent, Guid> correlation, Func<TEvent, TSagaState, CancellationToken, ValueTask> handler);

        /// <summary>
        /// Registers a handler for <typeparamref name="TEvent"/> that goes to
        /// an EXISTING saga instance. If no saga is found, the event is discarded.
        /// </summary>
        void MapHandledBy<TEvent>(Func<TEvent, Guid> correlation, Func<TEvent, TSagaState, CancellationToken, ValueTask> handler);
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

        public void MapStartedBy<TEvent>(Func<TEvent, Guid> correlation, Func<TEvent, TSagaState, CancellationToken, ValueTask> handler)
        {
            _saga.Handlers[typeof(TEvent)] = new SagaHandlerRegistration
            {
                IsStartedBy = true,
                GetCorrelationId = e => correlation((TEvent)e),
                Handler = (e, state, ct) => handler((TEvent)e, (TSagaState)state, ct),
            };
        }

        public void MapHandledBy<TEvent>(Func<TEvent, Guid> correlation, Func<TEvent, TSagaState, CancellationToken, ValueTask> handler)
        {
            _saga.Handlers[typeof(TEvent)] = new SagaHandlerRegistration
            {
                IsStartedBy = false,
                GetCorrelationId = e => correlation((TEvent)e),
                Handler = (e, state, ct) => handler((TEvent)e, (TSagaState)state, ct),
            };
        }
    }
}
