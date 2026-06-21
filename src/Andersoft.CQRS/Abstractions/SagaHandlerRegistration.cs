using System;
using System.Threading;
using System.Threading.Tasks;

namespace Andersoft.CQRS.Abstractions;

internal sealed class SagaHandlerRegistration
{
    public bool IsStartedBy { get; set; }
    public Func<object, Guid> GetCorrelationId { get; set; } = null!;
    public Func<object, object, CancellationToken, ValueTask> Handler { get; set; } = null!;
}