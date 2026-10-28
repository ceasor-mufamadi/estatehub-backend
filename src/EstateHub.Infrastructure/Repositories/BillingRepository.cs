using EstateHub.Application.Interfaces;
using EstateHub.Domain.Entities;
using EstateHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EstateHub.Infrastructure.Repositories;

// Billing is tenant-scoped like everything else: every query is confined to the active estate.
public class BillingRepository : IBillingRepository
{
    private readonly EstateHubDbContext _db;

    public BillingRepository(EstateHubDbContext db)
    {
        _db = db;
    }

    public Task<BillingSettings?> GetSettingsAsync(CancellationToken ct = default) => _db.BillingSettings.FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<ChargeType>> ListChargeTypesAsync(CancellationToken ct = default) =>
        await _db.ChargeTypes.OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);

    public Task<ChargeType?> GetChargeTypeAsync(Guid id, CancellationToken ct = default) => _db.ChargeTypes.FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<IReadOnlyList<UnitCharge>> ListUnitChargesAsync(Guid chargeTypeId, CancellationToken ct = default) =>
        await _db.UnitCharges.Where(u => u.ChargeTypeId == chargeTypeId).ToListAsync(ct);

    public async Task<IReadOnlyList<Account>> ListAccountsAsync(CancellationToken ct = default) => await _db.Accounts.ToListAsync(ct);

    public Task<Account?> GetAccountAsync(Guid id, CancellationToken ct = default) => _db.Accounts.FirstOrDefaultAsync(a => a.Id == id, ct);

    public async Task<IReadOnlyList<AccountHolder>> HoldersAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken ct = default) =>
        await _db.AccountHolders.Where(h => accountIds.Contains(h.AccountId)).ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> AccountsHeldByAsync(IReadOnlyCollection<Guid> personIds, CancellationToken ct = default) =>
        await _db.AccountHolders.Where(h => personIds.Contains(h.PersonId)).Select(h => h.AccountId).Distinct().ToListAsync(ct);

    public async Task<IReadOnlyList<LedgerEntry>> EntriesAsync(Guid accountId, CancellationToken ct = default) =>
        await _db.LedgerEntries.AsNoTracking().Where(e => e.AccountId == accountId).ToListAsync(ct);

    public Task<LedgerEntry?> GetEntryAsync(Guid id, CancellationToken ct = default) =>
        _db.LedgerEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);

    public Task<bool> AnyEntriesAsync(Guid accountId, CancellationToken ct = default) => _db.LedgerEntries.AnyAsync(e => e.AccountId == accountId, ct);

    public async Task<IReadOnlyDictionary<Guid, decimal>> BalancesAsync(CancellationToken ct = default) =>
        await _db.LedgerEntries.GroupBy(e => e.AccountId)
            .Select(g => new { g.Key, Balance = g.Sum(e => e.Debit) - g.Sum(e => e.Credit) })
            .ToDictionaryAsync(x => x.Key, x => x.Balance, ct);

    public void Add(object entity) => _db.Add(entity);

    public void Remove(object entity) => _db.Remove(entity);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
