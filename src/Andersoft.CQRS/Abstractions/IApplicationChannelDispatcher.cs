using System;
using System.Threading;
using System.Threading.Tasks;

namespace Andersoft.CQRS.Abstractions;

public interface IApplicationChannelDispatcher
{
    ValueTask<TResult> DispatchAsync<TResult>(
        Func<CancellationToken, ValueTask<TResult>> operation,
        CancellationToken ct,
        string operationName);
}