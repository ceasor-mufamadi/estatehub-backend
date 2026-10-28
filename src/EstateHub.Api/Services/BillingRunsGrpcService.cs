using System.Runtime.CompilerServices;
using EstateHub.Api.Authorization;
using EstateHub.Api.Streaming;
using EstateHub.Application.Auth;
using EstateHub.Application.Billing;
using EstateHub.Application.Features;
using EstateHub.Domain.Entities;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.Extensions.Options;
using static EstateHub.Api.Services.GrpcRequest;

namespace EstateHub.Api.Services;

[RequiresFeature(FeatureCatalog.Billing)]
[NoOwnershipRequired("Estate-wide billing for staff; the tenant layer confines ids to the caller's estate.")]
public class BillingRunsGrpcService : BillingRunsService.BillingRunsServiceBase
{
    private readonly IBillingRunService _runs;
    private readonly ICurrentUser _user;
    private readonly StreamConnectionLimiter _streams;
    private readonly IOptions<StreamingOptions> _streaming;

    public BillingRunsGrpcService(IBillingRunService runs, ICurrentUser user, StreamConnectionLimiter streams, IOptions<StreamingOptions> streaming)
    {
        _runs = runs;
        _user = user;
        _streams = streams;
        _streaming = streaming;
    }

    [RequiresPermission(Permissions.BillingRead)]
    public override async Task<ListBudgetsResponse> ListBudgets(ListBudgetsRequest request, ServerCallContext context)
    {
        var response = new ListBudgetsResponse();
        response.Items.AddRange((await _runs.ListBudgetsAsync(context.CancellationToken)).Select(ToProto));
        return response;
    }

    [RequiresPermission(Permissions.BillingWrite)]
    public override async Task<LevyBudget> AddBudget(AddBudgetRequest request, ServerCallContext context) =>
        ToProto(await _runs.AddBudgetAsync(request.EffectiveFrom, RequiredMoney(request.AdminFundMonthly, "Admin fund"),
            OptionalMoney(request.ReserveFundMonthly, "Reserve fund") ?? 0, request.Note, context.CancellationToken));

    [RequiresPermission(Permissions.BillingWrite)]
    public override async Task<RemoveBudgetResponse> RemoveBudget(RemoveBudgetRequest request, ServerCallContext context)
    {
        await _runs.RemoveBudgetAsync(RequiredGuid(request.BudgetId, "budget_id"), context.CancellationToken);
        return new RemoveBudgetResponse();
    }

    [RequiresPermission(Permissions.BillingWrite)]
    public override async Task<UploadMeteredResponse> UploadMetered(UploadMeteredRequest request, ServerCallContext context)
    {
        var result = await _runs.UploadMeteredAsync(request.Period, RequiredGuid(request.ChargeTypeId, "charge_type_id"), request.FileName,
            request.Content.ToByteArray(), context.CancellationToken);
        var response = new UploadMeteredResponse { Saved = result.Saved, Rows = result.Rows, Total = Money(result.Total) };
        response.Problems.AddRange(result.Problems);
        return response;
    }

    [RequiresPermission(Permissions.BillingRead)]
    public override async Task<ListMeteredResponse> ListMetered(ListMeteredRequest request, ServerCallContext context)
    {
        var response = new ListMeteredResponse();
        response.Items.AddRange((await _runs.ListMeteredAsync(request.Period, context.CancellationToken)).Select(m => new MeteredSummary
        {
            ChargeTypeId = m.ChargeTypeId.ToString(), ChargeName = m.ChargeName, Units = m.Units, Total = Money(m.Total), FileName = m.FileName ?? "",
            UploadedAt = m.UploadedAt is { } at ? Timestamp.FromDateTime(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : null,
        }));
        return response;
    }

    [RequiresPermission(Permissions.BillingWrite)]
    public override async Task<BillingRun> CreateDraft(CreateDraftRequest request, ServerCallContext context) =>
        ToProto(await _runs.CreateDraftAsync(request.Period, context.CancellationToken));

    [RequiresPermission(Permissions.BillingRead)]
    public override async Task<ListRunsResponse> ListRuns(ListRunsRequest request, ServerCallContext context)
    {
        var response = new ListRunsResponse();
        response.Items.AddRange((await _runs.ListRunsAsync(context.CancellationToken)).Select(ToProto));
        return response;
    }

    [RequiresPermission(Permissions.BillingRead)]
    public override async Task<BillingRun> GetRun(GetRunRequest request, ServerCallContext context) =>
        ToProto(await _runs.GetRunAsync(RequiredGuid(request.RunId, "run_id"), context.CancellationToken));

    [RequiresPermission(Permissions.BillingWrite)]
    public override async Task<DiscardDraftResponse> DiscardDraft(DiscardDraftRequest request, ServerCallContext context)
    {
        await _runs.DiscardDraftAsync(RequiredGuid(request.RunId, "run_id"), context.CancellationToken);
        return new DiscardDraftResponse();
    }

    [RequiresPermission(Permissions.BillingRead)]
    public override async Task<RunPreview> PreviewRun(PreviewRunRequest request, ServerCallContext context)
    {
        var p = await _runs.PreviewAsync(RequiredGuid(request.RunId, "run_id"), context.CancellationToken);
        var preview = new RunPreview { Run = ToProto(p.Run), GrandTotal = Money(p.GrandTotal) };
        preview.Accounts.AddRange(p.Accounts.Select(a =>
        {
            var account = new RunAccount
            {
                AccountId = a.AccountId.ToString(), Number = a.Number, SectionNumber = a.SectionNumber, Total = Money(a.Total),
                PreviousTotal = a.PreviousTotal is { } prev ? Money(prev) : "",
            };
            account.Holders.AddRange(a.Holders);
            account.Lines.AddRange(a.Lines.Select(l => new RunLine { Charge = l.Charge, Type = l.Type, Amount = Money(l.Amount), Reference = l.Reference ?? "" }));
            account.Flags.AddRange(a.Flags);
            return account;
        }));
        preview.Totals.AddRange(p.Totals.Select(t => new RunChargeTotal { Charge = t.Charge, Total = Money(t.Total), Accounts = t.Accounts }));
        preview.Blockers.AddRange(p.Blockers);
        preview.Warnings.AddRange(p.Warnings);
        return preview;
    }

    [RequiresPermission(Permissions.BillingPost)]
    [RequiresRecentMfa]
    public override async Task<BillingRun> PostRun(PostRunRequest request, ServerCallContext context) =>
        ToProto(await _runs.PostAsync(RequiredGuid(request.RunId, "run_id"), context.CancellationToken));

    [RequiresPermission(Permissions.BillingRead)]
    [DisableRequestTimeout]
    public override async Task WatchRun(WatchRunRequest request, IServerStreamWriter<RunStreamEvent> responseStream, ServerCallContext context)
    {
        var id = RequiredGuid(request.RunId, "run_id");
        await _runs.GetRunAsync(id, context.CancellationToken);
        using var lease = _streams.Acquire(_user.UserId!);
        await GrpcStreaming.PumpAsync(ChangesAsync(id, context.CancellationToken), responseStream, context,
            toMessage: run => new RunStreamEvent { Run = ToProto(run) },
            heartbeat: hb => new RunStreamEvent { Heartbeat = hb },
            _streaming.Value.HeartbeatInterval);
    }

    private async IAsyncEnumerable<BillingRunDto> ChangesAsync(Guid id, [EnumeratorCancellation] CancellationToken ct)
    {
        string? last = null;
        while (!ct.IsCancellationRequested)
        {
            var run = await _runs.GetRunAsync(id, ct);
            var fingerprint = $"{run.Status}|{run.Progress}|{run.Message}";
            if (fingerprint != last)
            {
                last = fingerprint;
                yield return run;
            }
            if (!BillingRunStatuses.IsActive(run.Status) && run.Progress is null)
                yield break;
            await Task.Delay(ImportGrpcService.WatchInterval, ct);
        }
    }

    private static LevyBudget ToProto(LevyBudgetDto b) => new()
    {
        Id = b.Id.ToString(), EffectiveFrom = b.EffectiveFrom, AdminFundMonthly = Money(b.AdminFundMonthly), ReserveFundMonthly = Money(b.ReserveFundMonthly),
        Note = b.Note ?? "", Used = b.Used,
    };

    private static BillingRun ToProto(BillingRunDto r)
    {
        var run = new BillingRun
        {
            Id = r.Id.ToString(), Period = r.Period, Status = r.Status, BillingDate = Date(r.BillingDate), TotalPosted = Money(r.TotalPosted),
            AccountsPosted = r.AccountsPosted, Message = r.Message ?? "",
            PostedAt = r.PostedAt is { } at ? Timestamp.FromDateTime(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : null,
        };
        if (r.Progress is { } p)
            run.Progress = new RunProgress { Done = p.Done, Total = p.Total, Message = p.Message };
        return run;
    }
}
