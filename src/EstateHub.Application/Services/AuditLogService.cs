using EstateHub.Application.DTOs;
using EstateHub.Application.Interfaces;
using EstateHub.Application.Streaming;
using EstateHub.Application.Tenancy;
using EstateHub.Domain.Entities;

namespace EstateHub.Application.Services;

public class AuditLogService : IAuditLogService
{
    private const int MaxPageSize = 200;

    private readonly IAuditLogRepository _repository;
    private readonly ITenantContext _tenant;
    private readonly IEventBroadcaster<AuditLogDto> _broadcaster;

    public AuditLogService(IAuditLogRepository repository, ITenantContext tenant, IEventBroadcaster<AuditLogDto> broadcaster)
    {
        _repository = repository;
        _tenant = tenant;
        _broadcaster = broadcaster;
    }

    public async Task<AuditLogDto> CreateAsync(CreateAuditLogDto request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.EntityType))
            throw new ArgumentException("EntityType is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.EntityId))
            throw new ArgumentException("EntityId is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Action))
            throw new ArgumentException("Action is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.PerformedBy))
            throw new ArgumentException("PerformedBy is required.", nameof(request));

        var entry = new AuditLog
        {
            EstateId = request.EstateId ?? _tenant.EstateId,
            EntityType = request.EntityType.Trim(),
            EntityId = request.EntityId.Trim(),
            Action = request.Action.Trim(),
            PerformedBy = request.PerformedBy.Trim(),
            Details = request.Details,
            IpAddress = request.IpAddress,
            PerformedAt = DateTime.UtcNow,
        };

        await _repository.AddAsync(entry, ct);

        var dto = ToDto(entry);
        await _broadcaster.PublishAsync(dto, ct);
        return dto;
    }

    public async Task<AuditLogDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entry = await _repository.GetByIdAsync(ActiveEstate(), id, ct);
        return entry is null ? null : ToDto(entry);
    }

    public async Task<PagedResult<AuditLogDto>> ListAsync(
        string? entityType, string? entityId, int page, int pageSize, CancellationToken ct = default)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize switch
        {
            <= 0 => 20,
            > MaxPageSize => MaxPageSize,
            _ => pageSize,
        };

        var (items, totalCount) = await _repository.ListAsync(
            ActiveEstate(), Blank(entityType), Blank(entityId), page, pageSize, ct);

        return new PagedResult<AuditLogDto>(items.Select(ToDto).ToList(), totalCount, page, pageSize);
    }

    public IAsyncEnumerable<AuditLogDto> SubscribeAsync(string? entityType, CancellationToken ct)
    {
        var estateId = ActiveEstate();
        entityType = Blank(entityType);

        return _broadcaster.SubscribeAsync(
            dto => dto.EstateId == estateId && (entityType is null || dto.EntityType == entityType), ct);
    }

    private Guid ActiveEstate() =>
        _tenant.EstateId ?? throw new InvalidOperationException("No active estate for this call.");

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static AuditLogDto ToDto(AuditLog entry) => new(
        entry.Id,
        entry.EstateId,
        entry.EntityType,
        entry.EntityId,
        entry.Action,
        entry.PerformedBy,
        entry.PerformedAt,
        entry.Details,
        entry.IpAddress);
}
