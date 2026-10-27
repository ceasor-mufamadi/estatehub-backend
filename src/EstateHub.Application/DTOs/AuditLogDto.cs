namespace EstateHub.Application.DTOs;

public record AuditLogDto(
    Guid Id,
    Guid? EstateId,
    string EntityType,
    string EntityId,
    string Action,
    string PerformedBy,
    DateTime PerformedAt,
    string? Details,
    string? IpAddress);

/// <summary>
/// EstateId defaults to the active estate (ITenantContext). Platform code may name another estate
/// explicitly, e.g. super admin changing an estate's features is recorded in that estate's trail.
/// </summary>
public record CreateAuditLogDto(
    string EntityType,
    string EntityId,
    string Action,
    string PerformedBy,
    string? Details = null,
    string? IpAddress = null,
    Guid? EstateId = null);

public record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
