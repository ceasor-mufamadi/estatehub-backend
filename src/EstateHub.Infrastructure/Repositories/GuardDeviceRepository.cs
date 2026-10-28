using EstateHub.Application.Interfaces;
using EstateHub.Domain.Entities;
using EstateHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EstateHub.Infrastructure.Repositories;

// Tenant-scoped: the DbContext confines every query here to the active estate.
public class GuardDeviceRepository : IGuardDeviceRepository
{
    private readonly EstateHubDbContext _db;

    public GuardDeviceRepository(EstateHubDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<GuardDevice>> ListActiveAsync(CancellationToken ct = default) =>
        await _db.GuardDevices.Where(d => d.RevokedAt == null).OrderBy(d => d.Label).ToListAsync(ct);

    public Task<GuardDevice?> GetAsync(Guid id, CancellationToken ct = default) =>
        _db.GuardDevices.FirstOrDefaultAsync(d => d.Id == id, ct);

    public Task<PairingCode?> FindPairingCodeAsync(string codeHash, CancellationToken ct = default) =>
        _db.PairingCodes.FirstOrDefaultAsync(c => c.CodeHash == codeHash, ct);

    public void Add(object entity) => _db.Add(entity);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
