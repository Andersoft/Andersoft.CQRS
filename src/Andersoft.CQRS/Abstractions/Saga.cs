using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
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
///         // Correlate by matching an event key against a saga-state field.
///         m.MapStartedBy&lt;StartOrder, Guid&gt;(e => e.OrderId, s => s.OrderId);
///         m.MapHandledBy&lt;CompleteOrder, Guid&gt;(e => e.OrderId, s => s.OrderId);
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
    /// <param name="messageKey">Extracts the correlation key from the event.</param>
    /// <param name="sagaKey">
    /// Selects the saga-state field to match the key against (an <c>Expression&lt;Func&lt;TState, TKey&gt;&gt;</c>,
    /// typed here as <see cref="LambdaExpression"/>). The expression is composed into an EF find-predicate;
    /// for a started-by mapping it must be a simple property/field so the new instance can be initialized.
    /// </param>
    /// <remarks>
    /// The handler is bound with a plain generic-interface type check — no GetMethod or CreateDelegate.
    /// The find-predicate is built by composing the supplied expression tree (translated by the repository,
    /// not compiled at runtime). Started-by initialization assigns the field via the member captured from
    /// <paramref name="sagaKey"/> — the only reflection on this path, and the chosen trade-off for the
    /// single-selector mapper API. State is reached via the saga's Data property, so the handler takes no
    /// state parameter.
    /// </remarks>
    protected internal void RegisterSagaHandler<TEvent, TKey>(
        Func<TEvent, TKey> messageKey,
        LambdaExpression sagaKey,
        bool isStartedBy)
    {
        if (this is not IMessageHandler<TEvent> handler)
        {
            throw new InvalidOperationException(
                $"Saga '{GetType().Name}' maps '{typeof(TEvent).Name}' but does not implement " +
                $"IMessageHandler<{typeof(TEvent).Name}>. Add the interface and a " +
                $"'ValueTask HandleAsync({typeof(TEvent).Name} message, CancellationToken ct)' method.");
        }

        var stateParam = sagaKey.Parameters[0];
        var keyBody = sagaKey.Body;
        var member = (keyBody as MemberExpression)?.Member;

        if (isStartedBy && member is null)
        {
            throw new InvalidOperationException(
                $"Saga '{GetType().Name}' maps '{typeof(TEvent).Name}' with MapStartedBy using a saga key " +
                $"that is not a simple property or field. A started-by mapping must target a settable member " +
                $"so a new instance can be initialized with the correlation value.");
        }

        Handlers[typeof(TEvent)] = new SagaHandlerRegistration
        {
            IsStartedBy = isStartedBy,
            BuildPredicate = e =>
            {
                var value = messageKey((TEvent)e);
                // Wrap the value in a closure (rather than Expression.Constant) so the repository can
                // parameterize the query and reuse its compiled plan across keys.
                Expression<Func<TKey>> valueExpr = () => value;
                var body = Expression.Equal(keyBody, valueExpr.Body);
                return Expression.Lambda(body, stateParam);
            },
            InitializeState = isStartedBy
                ? (state, e) => SetMember(member!, state, messageKey((TEvent)e))
                : null,
            Handler = (e, _, ct) => handler.HandleAsync((TEvent)e, ct),
        };
    }

    private static void SetMember(MemberInfo member, object target, object? value)
    {
        switch (member)
        {
            case PropertyInfo p:
                p.SetValue(target, value);
                break;
            case FieldInfo f:
                f.SetValue(target, value);
                break;
            default:
                throw new InvalidOperationException(
                    $"Cannot assign a saga correlation value to member '{member.Name}'.");
        }
    }
}
