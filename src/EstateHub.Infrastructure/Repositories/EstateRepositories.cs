using EstateHub.Application.DTOs;
using EstateHub.Application.Interfaces;
using EstateHub.Domain.Entities;
using EstateHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EstateHub.Infrastructure.Repositories;

public class EstateRepository : IEstateRepository
{
    private readonly EstateHubDbContext _db;

    public EstateRepository(EstateHubDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(Estate estate, CancellationToken ct = default)
    {
        _db.Estates.Add(estate);
        await _db.SaveChangesAsync(ct);
    }

    public Task<Estate?> GetAsync(Guid id, CancellationToken ct = default) =>
        _db.Estates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);

    public async Task<IReadOnlyList<Estate>> ListAsync(CancellationToken ct = default) =>
        await _db.Estates.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);

    // lower() on both sides, matching the ux_estates_name_ci index (migration EstateNameUnique).
    public Task<bool> NameExistsAsync(string name, CancellationToken ct = default) =>
        _db.Estates.AnyAsync(x => x.Name.ToLower() == name.ToLower(), ct);

    public Task<Estate?> GetForUpdateAsync(Guid id, CancellationToken ct = default) =>
        _db.Estates.FirstOrDefaultAsync(x => x.Id == id, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}

public class FeatureRepository : IFeatureRepository
{
    private readonly EstateHubDbContext _db;

    public FeatureRepository(EstateHubDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyDictionary<string, bool>> GetOverridesAsync(Guid estateId, CancellationToken ct = default) =>
        await _db.EstateFeatures.AsNoTracking()
            .Where(x => x.EstateId == estateId)
            .ToDictionaryAsync(x => x.FeatureKey, x => x.Enabled, StringComparer.Ordinal, ct);

    public async Task UpsertAsync(Guid estateId, string featureKey, bool enabled, string updatedBy, CancellationToken ct = default)
    {
        var row = await _db.EstateFeatures.FirstOrDefaultAsync(x => x.EstateId == estateId && x.FeatureKey == featureKey, ct);
        if (row is null)
        {
            row = new EstateFeature { EstateId = estateId, FeatureKey = featureKey };
            _db.EstateFeatures.Add(row);
        }

        row.Enabled = enabled;
        row.UpdatedBy = updatedBy;
        await _db.SaveChangesAsync(ct);
    }
}

// Blocks are ITenantScoped: the DbContext filters reads and stamps EstateId on insert, so this
// repository never mentions an estate.
public class BlockRepository : IBlockRepository
{
    private readonly EstateHubDbContext _db;

    public BlockRepository(EstateHubDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(Block block, CancellationToken ct = default)
    {
        _db.Blocks.Add(block);
        await _db.SaveChangesAsync(ct);
    }

    public Task<bool> NameExistsAsync(string name, CancellationToken ct = default) =>
        _db.Blocks.AnyAsync(x => x.Name == name, ct);

    public async Task<IReadOnlyList<Block>> ListAsync(CancellationToken ct = default) =>
        await _db.Blocks.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);

    public Task<Block?> GetAsync(Guid id, CancellationToken ct = default) =>
        _db.Blocks.FirstOrDefaultAsync(x => x.Id == id, ct);

    public Task<bool> NameExistsAsync(string name, Guid? exceptBlockId, CancellationToken ct = default) =>
        _db.Blocks.AnyAsync(x => x.Name == name && x.Id != exceptBlockId, ct);

    public void Remove(Block block) => _db.Blocks.Remove(block);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}

// Units are ITenantScoped: every query here is confined to the active estate by the DbContext.
public class UnitRepository : IUnitRepository
{
    private readonly EstateHubDbContext _db;

    public UnitRepository(EstateHubDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<UnitDto>> ListAsync(CancellationToken ct = default) =>
        await _db.Units.AsNoTracking()
            .OrderBy(u => u.SectionNumber)
            .Select(u => new UnitDto(
                u.Id, u.SectionNumber, u.DoorNumber, u.BlockId,
                _db.Blocks.Where(b => b.Id == u.BlockId).Select(b => b.Name).FirstOrDefault(),
                u.ParticipationQuota, u.SizeSquareMetres))
            .ToListAsync(ct);

    public Task<Unit?> GetAsync(Guid id, CancellationToken ct = default) => _db.Units.FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<bool> SectionExistsAsync(int sectionNumber, Guid? exceptUnitId, CancellationToken ct = default) =>
        _db.Units.AnyAsync(u => u.SectionNumber == sectionNumber && u.Id != exceptUnitId, ct);

    public Task<bool> AnyInBlockAsync(Guid blockId, CancellationToken ct = default) => _db.Units.AnyAsync(u => u.BlockId == blockId, ct);

    public void Add(Unit unit) => _db.Units.Add(unit);

    public void Remove(Unit unit) => _db.Units.Remove(unit);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
