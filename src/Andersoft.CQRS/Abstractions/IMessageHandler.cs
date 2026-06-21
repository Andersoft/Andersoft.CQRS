using System.Threading;
using System.Threading.Tasks;

namespace Andersoft.CQRS.Abstractions;

/// <summary>
/// Handles a message that returns no value.
/// </summary>
/// <remarks>
/// A message dispatched through the generated <c>TypedDispatcher</c> is delivered to
/// <em>every</em> registered handler for its type. Whether that's one handler
/// ("command" style) or many ("event" style) is just a matter of how many are
/// registered — it is not a distinction the library models. Surface failures via
/// exceptions; there is no return channel.
/// </remarks>
public interface IMessageHandler<in TMessage>
{
    ValueTask HandleAsync(TMessage message, CancellationToken ct = default);
}

/// <summary>
/// Handles a message that returns a <typeparamref name="TResult"/>. A message with a
/// result is dispatched to exactly one handler ("query"/"request" style).
/// </summary>
public interface IMessageHandler<in TMessage, TResult>
{
    ValueTask<TResult> HandleAsync(TMessage message, CancellationToken ct = default);
}
