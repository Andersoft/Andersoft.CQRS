using System.Threading.Tasks;

namespace Andersoft.CQRS;

public delegate ValueTask<TResult> RequestHandlerDelegate<TResult>();