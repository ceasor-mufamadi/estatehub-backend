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
}
