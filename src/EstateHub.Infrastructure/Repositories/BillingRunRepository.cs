using EstateHub.Application.Billing;
using EstateHub.Application.Imports;
using EstateHub.Domain.Entities;
using EstateHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EstateHub.Infrastructure.Repositories;

// Billing runs, budgets and metered amounts are tenant-scoped: every query is confined to the active estate.
public class BillingRunRepository : IBillingRunRepository
{
    private readonly EstateHubDbContext _db;

    public BillingRunRepository(EstateHubDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<LevyBudget>> ListBudgetsAsync(CancellationToken ct = default) => await _db.LevyBudgets.ToListAsync(ct);

    public Task<LevyBudget?> GetBudgetAsync(Guid id, CancellationToken ct = default) => _db.LevyBudgets.FirstOrDefaultAsync(b => b.Id == id, ct);

    public async Task<IReadOnlyList<MeteredAmount>> MeteredAsync(string period, CancellationToken ct = default) =>
        await _db.MeteredAmounts.Where(m => m.Period == period).ToListAsync(ct);

    public async Task<IReadOnlyList<MeteredAmount>> MeteredAsync(string period, Guid chargeTypeId, CancellationToken ct = default) =>
        await _db.MeteredAmounts.Where(m => m.Period == period && m.ChargeTypeId == chargeTypeId).ToListAsync(ct);

    public async Task<IReadOnlyList<BillingRun>> ListRunsAsync(CancellationToken ct = default) => await _db.BillingRuns.AsNoTracking().ToListAsync(ct);

    public Task<BillingRun?> GetRunAsync(Guid id, CancellationToken ct = default) => _db.BillingRuns.FirstOrDefaultAsync(r => r.Id == id, ct);

    public Task<BillingRun?> PeekRunAsync(Guid id, CancellationToken ct = default) => _db.BillingRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);

    public Task<BillingRun?> FindRunAsync(string period, CancellationToken ct = default) => _db.BillingRuns.FirstOrDefaultAsync(r => r.Period == period, ct);

    public async Task<IReadOnlyDictionary<Guid, decimal>> PostedTotalsByAccountAsync(Guid runId, CancellationToken ct = default) =>
        await _db.LedgerEntries.Where(e => e.SourceId == runId && e.Source == LedgerSources.Run)
            .GroupBy(e => e.AccountId).Select(g => new { g.Key, Total = g.Sum(e => e.Debit) })
            .ToDictionaryAsync(x => x.Key, x => x.Total, ct);

    public async Task<IImportTransaction> BeginTransactionAsync(CancellationToken ct = default) =>
        new Transaction(await _db.Database.BeginTransactionAsync(ct));

    public void Add(object entity) => _db.Add(entity);

    public void Remove(object entity) => _db.Remove(entity);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);

    private sealed class Transaction(IDbContextTransaction inner) : IImportTransaction
    {
        public Task CommitAsync(CancellationToken ct = default) => inner.CommitAsync(ct);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
