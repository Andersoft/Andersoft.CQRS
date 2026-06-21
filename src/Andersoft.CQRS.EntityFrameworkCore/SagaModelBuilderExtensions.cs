using Andersoft.CQRS.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Andersoft.CQRS.EntityFrameworkCore;

public static class SagaModelBuilderExtensions
{
    /// <summary>
    /// Configures EF Core mapping for a saga state type: <see cref="SagaState.CorrelationId"/> as the
    /// primary key and <see cref="SagaState.Version"/> as an app-managed optimistic concurrency token.
    /// </summary>
    /// <remarks>
    /// <see cref="SagaState.Version"/> is a concurrency token, not a store-generated row version:
    /// <see cref="EFCoreSagaRepository{TState}"/> sets its original value for the concurrency check and
    /// increments it after save. Mapping it as a row version would conflict with that.
    /// </remarks>
    public static ModelBuilder ConfigureSagaState<TState>(this ModelBuilder modelBuilder)
        where TState : SagaState
    {
        var entity = modelBuilder.Entity<TState>();
        entity.HasKey(s => s.CorrelationId);
        entity.Property(s => s.Version).IsConcurrencyToken();
        return modelBuilder;
    }
}
