using System;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Andersoft.CQRS.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Andersoft.CQRS.EntityFrameworkCore;

/// <summary>
/// EF Core implementation of <see cref="ISagaRepository{TState}"/>.
/// </summary>
/// <typeparam name="TState">The saga state type, derived from <see cref="SagaState"/>.</typeparam>
/// <remarks>
/// Single type parameter so it can be registered as an open generic
/// (<c>ISagaRepository&lt;&gt;</c> → <c>EFCoreSagaRepository&lt;&gt;</c>), which is AOT‑safe.
/// The concrete <see cref="DbContext"/> is supplied by DI — register it as
/// <see cref="DbContext"/> via <c>AddSagaPersistence&lt;TContext&gt;()</c>.
/// </remarks>
public class EFCoreSagaRepository<TState> : ISagaRepository<TState>
    where TState : SagaState
{
    private readonly DbContext _context;

    public EFCoreSagaRepository(DbContext context)
    {
        _context = context;
    }

    public async ValueTask<TState?> LoadAsync(Expression<Func<TState, bool>> match, CancellationToken ct = default)
    {
        return await _context.Set<TState>()
            .AsNoTracking()
            .FirstOrDefaultAsync(match, ct);
    }

    public async ValueTask SaveAsync(TState state, CancellationToken ct = default)
    {
        if (state.Version == 0)
        {
            // New saga — insert. EF Core will assign any store-generated values.
            _context.Set<TState>().Add(state);
        }
        else
        {
            // Existing saga — attach and mark modified.
            // Set the original Version for optimistic concurrency; EF Core
            // throws DbUpdateConcurrencyException if another process saved between load and save.
            var entry = _context.Set<TState>().Attach(state);
            entry.State = EntityState.Modified;
            entry.Property(nameof(SagaState.Version)).OriginalValue = state.Version;
        }

        await _context.SaveChangesAsync(ct);
        state.Version++;
    }

    public async ValueTask DeleteAsync(TState state, CancellationToken ct = default)
    {
        _context.Set<TState>().Remove(state);
        await _context.SaveChangesAsync(ct);
    }
}
