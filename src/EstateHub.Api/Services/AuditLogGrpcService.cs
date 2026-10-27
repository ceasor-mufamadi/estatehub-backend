using EstateHub.Api.Authorization;
using EstateHub.Api.Streaming;
using EstateHub.Application.Auth;
using EstateHub.Application.DTOs;
using EstateHub.Application.Interfaces;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.Extensions.Options;

namespace EstateHub.Api.Services;

[RequiresPermission(Permissions.AuditLogRead)]
[NoOwnershipRequired("Estate-wide trail for staff; the tenant layer confines it to the caller's estate.")]
[AlwaysOn("The audit trail can't be switched off.")]
public class AuditLogGrpcService : AuditLogService.AuditLogServiceBase
{
    private readonly IAuditLogService _auditLogService;
    private readonly ICurrentUser _user;
    private readonly StreamConnectionLimiter _streams;
    private readonly IOptions<StreamingOptions> _streaming;

    public AuditLogGrpcService(
        IAuditLogService auditLogService, ICurrentUser user, StreamConnectionLimiter streams, IOptions<StreamingOptions> streaming)
    {
        _auditLogService = auditLogService;
        _user = user;
        _streams = streams;
        _streaming = streaming;
    }

    public override async Task<AuditLogEntry> GetAuditLog(GetAuditLogRequest request, ServerCallContext context)
    {
        var id = GrpcRequest.RequiredGuid(request.Id, "id");

        var entry = await _auditLogService.GetByIdAsync(id, context.CancellationToken)
            ?? throw GrpcRequest.NotFound($"Audit log '{request.Id}' was not found.");

        return ToEntry(entry);
    }

    public override async Task<ListAuditLogsResponse> ListAuditLogs(ListAuditLogsRequest request, ServerCallContext context)
    {
        var result = await _auditLogService.ListAsync(
            request.EntityType, request.EntityId, request.Page, request.PageSize, context.CancellationToken);

        var response = new ListAuditLogsResponse
        {
            TotalCount = result.TotalCount,
            Page = result.Page,
            PageSize = result.PageSize,
        };
        response.Items.AddRange(result.Items.Select(ToEntry));

        return response;
    }

    [DisableRequestTimeout]
    public override async Task SubscribeAuditLogs(
        SubscribeAuditLogsRequest request, IServerStreamWriter<AuditLogStreamEvent> responseStream, ServerCallContext context)
    {
        using var lease = _streams.Acquire(_user.UserId!);

        await GrpcStreaming.PumpAsync(
            _auditLogService.SubscribeAsync(request.EntityType, context.CancellationToken),
            responseStream, context,
            toMessage: dto => new AuditLogStreamEvent { Entry = ToEntry(dto) },
            heartbeat: hb => new AuditLogStreamEvent { Heartbeat = hb },
            _streaming.Value.HeartbeatInterval);
    }

    private static AuditLogEntry ToEntry(AuditLogDto dto)
    {
        var entry = new AuditLogEntry
        {
            Id = dto.Id.ToString(),
            EntityType = dto.EntityType,
            EntityId = dto.EntityId,
            Action = dto.Action,
            PerformedBy = dto.PerformedBy,
            PerformedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(dto.PerformedAt, DateTimeKind.Utc)),
        };

        if (dto.Details is not null) entry.Details = dto.Details;
        if (dto.IpAddress is not null) entry.IpAddress = dto.IpAddress;

        return entry;
    }
}
