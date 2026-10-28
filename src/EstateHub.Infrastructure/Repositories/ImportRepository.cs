using EstateHub.Application.Imports;
using EstateHub.Domain.Entities;
using EstateHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EstateHub.Infrastructure.Repositories;

// Import batches are tenant-scoped like everything they create: every query is confined to the active estate.
public class ImportRepository : IImportRepository
{
    private readonly EstateHubDbContext _db;

    public ImportRepository(EstateHubDbContext db)
    {
        _db = db;
    }

    public Task<ImportBatch?> GetAsync(Guid id, CancellationToken ct = default) => _db.ImportBatches.FirstOrDefaultAsync(b => b.Id == id, ct);

    public Task<ImportBatch?> PeekAsync(Guid id, CancellationToken ct = default) =>
        _db.ImportBatches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);

    public async Task<IReadOnlyList<ImportBatch>> ListRecentAsync(int take, CancellationToken ct = default) =>
        await _db.ImportBatches.AsNoTracking().Where(b => b.Status != ImportStatuses.Discarded)
            .OrderByDescending(b => b.CreatedAt).Take(take).ToListAsync(ct);

    public Task<ImportBatch?> FindActiveAsync(CancellationToken ct = default) =>
        _db.ImportBatches.FirstOrDefaultAsync(b => b.Status == ImportStatuses.Queued || b.Status == ImportStatuses.Running, ct);

    public async Task<IReadOnlyList<ImportBatch>> ListInterruptedAsync(CancellationToken ct = default) =>
        await _db.ImportBatches.Where(b => b.Status == ImportStatuses.Queued || b.Status == ImportStatuses.Running).ToListAsync(ct);

    public async Task<IReadOnlyList<ImportBatch>> ListHoldingRowsAsync(CancellationToken ct = default) =>
        await _db.ImportBatches.Where(b => b.RowsJson != "[]").ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> BatchesWithPendingInvitesAsync(CancellationToken ct = default) =>
        await _db.ImportInvites.Where(i => i.Status == ImportInviteStatuses.Pending).Select(i => i.ImportBatchId).Distinct().ToListAsync(ct);

    public async Task<IReadOnlyList<ImportInvite>> InvitesAsync(Guid batchId, CancellationToken ct = default) =>
        await _db.ImportInvites.Where(i => i.ImportBatchId == batchId).OrderBy(i => i.CreatedAt).ToListAsync(ct);

    public async Task<ImportEstateState> LoadStateAsync(DateOnly today, CancellationToken ct = default)
    {
        var units = await _db.Units.Select(u => new { u.Id, u.SectionNumber, u.ParticipationQuota }).ToListAsync(ct);
        var blocks = await _db.Blocks.Select(b => new { b.Id, b.Name }).ToListAsync(ct);
        var people = await _db.People
            .Select(p => new ImportExistingPerson(p.Id, p.Name, p.Email, p.Phone, p.UserId != null)).ToListAsync(ct);
        var homes = await _db.Occupancies.Where(o => o.ToDate == null || o.ToDate >= today)
            .Select(o => new { o.PersonId, o.UnitId }).ToListAsync(ct);

        var sections = units.ToDictionary(u => u.Id, u => u.SectionNumber);
        return new ImportEstateState(
            units.Select(u => u.SectionNumber).ToHashSet(),
            blocks.ToDictionary(b => b.Name, b => b.Id, StringComparer.OrdinalIgnoreCase),
            people,
            homes.GroupBy(h => h.PersonId).ToDictionary(g => g.Key, g => sections.GetValueOrDefault(g.First().UnitId)),
            units.Sum(u => u.ParticipationQuota));
    }

    public async Task<IImportTransaction> BeginTransactionAsync(CancellationToken ct = default) =>
        new Transaction(await _db.Database.BeginTransactionAsync(ct));

    public void Add(object entity) => _db.Add(entity);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);

    private sealed class Transaction(IDbContextTransaction inner) : IImportTransaction
    {
        public Task CommitAsync(CancellationToken ct = default) => inner.CommitAsync(ct);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
