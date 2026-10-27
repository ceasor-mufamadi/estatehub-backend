using EstateHub.Domain.Entities;

namespace EstateHub.Application.Interfaces;

public interface IAuditLogRepository
{
    Task AddAsync(AuditLog log, CancellationToken ct = default);

    Task<AuditLog?> GetByIdAsync(Guid estateId, Guid id, CancellationToken ct = default);

    Task<(IReadOnlyList<AuditLog> Items, int TotalCount)> ListAsync(
        Guid estateId, string? entityType, string? entityId, int page, int pageSize, CancellationToken ct = default);
}
