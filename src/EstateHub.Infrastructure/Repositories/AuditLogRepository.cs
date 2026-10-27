using EstateHub.Application.Interfaces;
using EstateHub.Domain.Entities;
using EstateHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EstateHub.Infrastructure.Repositories;

// AuditLog is not ITenantScoped (platform events have no estate), so every read names the estate explicitly.
public class AuditLogRepository : IAuditLogRepository
{
    private readonly EstateHubDbContext _db;

    public AuditLogRepository(EstateHubDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(AuditLog log, CancellationToken ct = default)
    {
        _db.AuditLogs.Add(log);
        await _db.SaveChangesAsync(ct);
    }

    public Task<AuditLog?> GetByIdAsync(Guid estateId, Guid id, CancellationToken ct = default) =>
        _db.AuditLogs.AsNoTracking().FirstOrDefaultAsync(x => x.EstateId == estateId && x.Id == id, ct);

    public async Task<(IReadOnlyList<AuditLog> Items, int TotalCount)> ListAsync(
        Guid estateId, string? entityType, string? entityId, int page, int pageSize, CancellationToken ct = default)
    {
        var query = _db.AuditLogs.AsNoTracking().Where(x => x.EstateId == estateId);

        if (entityType is not null)
            query = query.Where(x => x.EntityType == entityType);

        if (entityId is not null)
            query = query.Where(x => x.EntityId == entityId);

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(x => x.PerformedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }
}
