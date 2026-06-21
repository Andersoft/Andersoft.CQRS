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
    public static IServiceCollection AddSagaRepository<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        services.TryAddScoped<DbContext>(sp => sp.GetRequiredService<TContext>());
        services.TryAddScoped(typeof(ISagaRepository<>), typeof(EFCoreSagaRepository<>));
        return services;
    }
}
