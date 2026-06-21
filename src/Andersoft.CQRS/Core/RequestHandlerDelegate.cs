using System.Threading.Tasks;

namespace Andersoft.CQRS;

/// <summary>Continuation in the interceptor pipeline of a message that returns a value.</summary>
public delegate ValueTask<TResult> RequestHandlerDelegate<TResult>();

/// <summary>Continuation in the interceptor pipeline of a message that returns no value.</summary>
public delegate ValueTask RequestHandlerDelegate();
