using System.Globalization;
using System.Runtime.CompilerServices;
using EstateHub.Api.Authorization;
using EstateHub.Api.Streaming;
using EstateHub.Application.Auth;
using EstateHub.Application.Imports;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.Extensions.Options;
using EstateHub.Domain.Entities;

namespace EstateHub.Api.Services;

[RequiresPermission(Permissions.ImportsRun)]
[NoOwnershipRequired("Staff import into their own estate; the tenant layer confines batch ids.")]
[AlwaysOn("Estate setup is core.")]
public class ImportGrpcService : ImportService.ImportServiceBase
{
    /// <summary>How often a watch checks for news (progress is in memory, status in the database).</summary>
    public static readonly TimeSpan WatchInterval = TimeSpan.FromMilliseconds(500);

    private readonly IImportService _imports;
    private readonly IImportTemplate _template;
    private readonly ICurrentUser _user;
    private readonly StreamConnectionLimiter _streams;
    private readonly IOptions<StreamingOptions> _streaming;

    public ImportGrpcService(
        IImportService imports, IImportTemplate template, ICurrentUser user, StreamConnectionLimiter streams, IOptions<StreamingOptions> streaming)
    {
        _imports = imports;
        _template = template;
        _user = user;
        _streams = streams;
        _streaming = streaming;
    }

    public override Task<ImportTemplate> GetImportTemplate(GetImportTemplateRequest request, ServerCallContext context) =>
        Task.FromResult(new ImportTemplate { FileName = Infrastructure.Imports.ImportTemplate.FileName, Content = ByteString.CopyFrom(_template.Create()) });

    public override async Task<ImportBatch> PreviewImport(PreviewImportRequest request, ServerCallContext context) =>
        ToProto(await _imports.PreviewAsync(request.FileName, request.Content.ToByteArray(), context.CancellationToken));

    [RequiresRecentMfa]
    public override async Task<ImportBatch> CommitImport(CommitImportRequest request, ServerCallContext context) =>
        ToProto(await _imports.CommitAsync(GrpcRequest.RequiredGuid(request.ImportId, "import_id"), request.SendInvitations, context.CancellationToken));

    public override async Task<ImportBatch> GetImport(GetImportRequest request, ServerCallContext context) =>
        ToProto(await _imports.GetAsync(GrpcRequest.RequiredGuid(request.ImportId, "import_id"), context.CancellationToken));

    public override async Task<ListImportsResponse> ListImports(ListImportsRequest request, ServerCallContext context)
    {
        var response = new ListImportsResponse();
        response.Items.AddRange((await _imports.ListAsync(context.CancellationToken)).Select(ToProto));
        return response;
    }

    public override async Task<DiscardImportResponse> DiscardImport(DiscardImportRequest request, ServerCallContext context)
    {
        await _imports.DiscardAsync(GrpcRequest.RequiredGuid(request.ImportId, "import_id"), context.CancellationToken);
        return new DiscardImportResponse();
    }

    [DisableRequestTimeout]
    public override async Task WatchImport(WatchImportRequest request, IServerStreamWriter<ImportStreamEvent> responseStream, ServerCallContext context)
    {
        var id = GrpcRequest.RequiredGuid(request.ImportId, "import_id");
        await _imports.GetAsync(id, context.CancellationToken); // not found → NotFound before the stream opens
        using var lease = _streams.Acquire(_user.UserId!);

        await GrpcStreaming.PumpAsync(
            ChangesAsync(id, context.CancellationToken),
            responseStream, context,
            toMessage: batch => new ImportStreamEvent { Batch = ToProto(batch) },
            heartbeat: hb => new ImportStreamEvent { Heartbeat = hb },
            _streaming.Value.HeartbeatInterval);
    }

    /// <summary>The batch each time it changes, until it is no longer queued or running.</summary>
    private async IAsyncEnumerable<ImportBatchDto> ChangesAsync(Guid id, [EnumeratorCancellation] CancellationToken ct)
    {
        string? last = null;
        while (!ct.IsCancellationRequested)
        {
            var batch = await _imports.GetAsync(id, ct);
            var fingerprint = $"{batch.Status}|{batch.Progress}|{batch.Message}|{batch.Invites.Pending}";
            if (fingerprint != last)
            {
                last = fingerprint;
                yield return batch;
            }
            if (!ImportStatuses.IsActive(batch.Status) && batch.Progress is null)
                yield break;
            await Task.Delay(WatchInterval, ct);
        }
    }

    private static Timestamp? Time(DateTime? value) =>
        value is { } v ? Timestamp.FromDateTime(DateTime.SpecifyKind(v, DateTimeKind.Utc)) : null;

    private static ImportBatch ToProto(ImportBatchDto b)
    {
        var batch = new ImportBatch
        {
            Id = b.Id.ToString(),
            FileName = b.FileName,
            Status = b.Status,
            RowCount = b.RowCount,
            CreatedAt = Time(b.CreatedAt),
            Summary = new ImportSummary
            {
                Units = b.Summary.Units,
                NewBlocks = b.Summary.NewBlocks,
                NewPeople = b.Summary.NewPeople,
                ExistingPeople = b.Summary.ExistingPeople,
                Ownerships = b.Summary.Ownerships,
                Occupancies = b.Summary.Occupancies,
                Invitations = b.Summary.Invitations,
                QuotaTotalAfter = b.Summary.QuotaTotalAfter.ToString("F4", CultureInfo.InvariantCulture),
                QuotaComplete = b.Summary.QuotaComplete,
            },
            MoreProblems = b.MoreProblems,
            Message = b.Message ?? "",
            SendInvitations = b.SendInvitations,
            Invites = new ImportInvites
            {
                Pending = b.Invites.Pending,
                Sent = b.Invites.Sent,
                Linked = b.Invites.Linked,
                Failed = b.Invites.Failed,
                NextBatchBy = Time(b.Invites.NextBatchBy),
            },
            FinishedAt = Time(b.FinishedAt),
        };
        batch.Invites.Failures.AddRange(b.Invites.Failures);
        batch.Problems.AddRange(b.Problems.Select(p => new ImportProblem { Row = p.Row, Column = p.Column, Message = p.Message }));
        batch.Preview.AddRange(b.Preview.Select(u => new ImportPreviewUnit
        {
            Section = u.Section,
            Door = u.Door ?? "",
            Block = u.Block ?? "",
            Quota = u.Quota.ToString("F4", CultureInfo.InvariantCulture),
            Owners = u.Owners,
            Occupants = u.Occupants,
        }));
        if (b.Progress is { } p)
            batch.Progress = new ImportProgress { Done = p.Done, Total = p.Total, Message = p.Message };
        return batch;
    }
}
