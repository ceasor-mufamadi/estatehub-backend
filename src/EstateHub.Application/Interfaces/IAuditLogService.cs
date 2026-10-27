using EstateHub.Application.DTOs;

namespace EstateHub.Application.Interfaces;

public interface IAuditLogService
{
    Task<AuditLogDto> CreateAsync(CreateAuditLogDto request, CancellationToken ct = default);

    /// <summary>Only entries of the active estate are visible.</summary>
    Task<AuditLogDto?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>The active estate's trail, newest first, optionally narrowed to one entity type / entity.</summary>
    Task<PagedResult<AuditLogDto>> ListAsync(
        string? entityType, string? entityId, int page, int pageSize, CancellationToken ct = default);

    /// <summary>New entries of the active estate as they are written.</summary>
    IAsyncEnumerable<AuditLogDto> SubscribeAsync(string? entityType, CancellationToken ct);
}
