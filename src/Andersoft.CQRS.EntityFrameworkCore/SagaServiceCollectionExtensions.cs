using System;
using System.Collections.Generic;
using Andersoft.CQRS.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Andersoft.CQRS.EntityFrameworkCore;

public static class SagaServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ISagaRepository{TState}"/> for every saga state type, backed by
    /// <see cref="EFCoreSagaRepository{TState}"/> over the given <typeparamref name="TContext"/>.
    /// </summary>
    /// <remarks>
    /// AOT‑safe: uses open‑generic registration (no <c>MakeGenericType</c> / reflection), so the
    /// container constructs the closed <c>EFCoreSagaRepository&lt;TState&gt;</c> for whichever state
    /// type is requested. The concrete <typeparamref name="TContext"/> is exposed as
    /// <see cref="DbContext"/> so the open‑generic repository can depend on it.
    /// </remarks>
    public static IServiceCollection AddSagaPersistence<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        services.TryAddScoped<DbContext>(sp => sp.GetRequiredService<TContext>());
        services.TryAddScoped(typeof(ISagaRepository<>), typeof(EFCoreSagaRepository<>));
        return services;
    }

    /// <summary>
    /// Registers <typeparamref name="TSaga"/> as a coordinator. The saga becomes resolvable in the
    /// <c>IEnumerable&lt;Saga&gt;</c> that <see cref="SagaDispatcher{TEvent}"/> fans out to, with its
    /// state accessor wired (over <see cref="ISagaRepository{TState}"/>) and its correlation handlers
    /// built from <c>ConfigureHowToFindSaga</c>.
    /// </summary>
    /// <param name="configure">
    /// Optional per-instance configuration run after the saga's internals are wired. The generated
    /// registration uses this to inject the scoped <c>TypedDispatcher</c> into the saga's generated
    /// partial — <c>TypedDispatcher</c> is a consumer-assembly type this library cannot name, so the
    /// wiring is supplied by generated code rather than performed here.
    /// </param>
    /// <remarks>
    /// Lives here (not in the generated registration) because it sets the <c>internal</c>
    /// <c>Saga.Accessor</c> and calls the <c>internal</c> <c>BuildHandlers()</c> — accessible via
    /// <c>InternalsVisibleTo</c>, which the consumer's generated code does not have.
    /// </remarks>
    public static IServiceCollection AddSaga<TSaga, TState>(
        this IServiceCollection services,
        Action<TSaga, IServiceProvider>? configure = null)
        where TSaga : Saga
        where TState : SagaState, new()
    {
        services.AddScoped<TSaga>();
        services.AddScoped<Saga>(sp =>
        {
            var saga = sp.GetRequiredService<TSaga>();
            saga.Accessor = new SagaStateAccessor<TState>(sp.GetRequiredService<ISagaRepository<TState>>());
            saga.BuildHandlers();
            configure?.Invoke(saga, sp);
            return saga;
        });
        return services;
    }
}
