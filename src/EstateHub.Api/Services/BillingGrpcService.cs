using EstateHub.Api.Authorization;
using EstateHub.Application.Auth;
using EstateHub.Application.Features;
using EstateHub.Application.Interfaces;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using static EstateHub.Api.Services.GrpcRequest;
using AccountFilterDto = EstateHub.Application.Interfaces.AccountFilter;
using ChargeTypeInputDto = EstateHub.Application.Interfaces.ChargeTypeInput;

namespace EstateHub.Api.Services;

[RequiresFeature(FeatureCatalog.Billing)]
[NoOwnershipRequired("Estate-wide billing setup for staff; the tenant layer confines it to the caller's estate.")]
public class BillingGrpcService : BillingService.BillingServiceBase
{
    private readonly IBillingService _billing;

    public BillingGrpcService(IBillingService billing)
    {
        _billing = billing;
    }

    [RequiresPermission(Permissions.BillingRead)]
    public override async Task<BillingSettings> GetBillingSettings(GetBillingSettingsRequest request, ServerCallContext context) =>
        ToProto(await _billing.GetSettingsAsync(context.CancellationToken));

    [RequiresPermission(Permissions.BillingWrite)]
    public override async Task<BillingSettings> UpdateBillingSettings(UpdateBillingSettingsRequest request, ServerCallContext context) =>
        ToProto(await _billing.UpdateSettingsAsync(new BillingSettingsInput(
            request.FinancialYearStartMonth, request.BillingDay, request.DueDay, request.LevyDisplay, request.InterestEnabled,
            OptionalMoney(request.InterestRatePercent, "Interest rate") ?? 0, request.QueriesEmail, request.QueriesPhone), context.CancellationToken));

    [RequiresPermission(Permissions.BillingWrite)]
    [RequiresRecentMfa]
    public override async Task<BillingSettings> UpdateBankDetails(UpdateBankDetailsRequest request, ServerCallContext context) =>
        ToProto(await _billing.UpdateBankDetailsAsync(
            new BankDetailsInput(request.BankName, request.BranchCode, request.AccountName, request.AccountNumber), context.CancellationToken));

    [RequiresPermission(Permissions.BillingRead)]
    public override async Task<ListChargeTypesResponse> ListChargeTypes(ListChargeTypesRequest request, ServerCallContext context)
    {
        var response = new ListChargeTypesResponse();
        response.Items.AddRange((await _billing.ListChargeTypesAsync(context.CancellationToken)).Select(ToProto));
        return response;
    }

    [RequiresPermission(Permissions.BillingWrite)]
    public override async Task<ChargeType> CreateChargeType(CreateChargeTypeRequest request, ServerCallContext context) =>
        ToProto(await _billing.CreateChargeTypeAsync(ToInput(request.Charge), context.CancellationToken));

    [RequiresPermission(Permissions.BillingWrite)]
    public override async Task<ChargeType> UpdateChargeType(UpdateChargeTypeRequest request, ServerCallContext context) =>
        ToProto(await _billing.UpdateChargeTypeAsync(RequiredGuid(request.ChargeTypeId, "charge_type_id"), ToInput(request.Charge), context.CancellationToken));

    [RequiresPermission(Permissions.BillingRead)]
    public override async Task<ListUnitChargesResponse> ListUnitCharges(ListUnitChargesRequest request, ServerCallContext context) =>
        ToProto(await _billing.ListUnitChargesAsync(RequiredGuid(request.ChargeTypeId, "charge_type_id"), context.CancellationToken));

    [RequiresPermission(Permissions.BillingWrite)]
    public override async Task<ListUnitChargesResponse> SetUnitCharges(SetUnitChargesRequest request, ServerCallContext context) =>
        ToProto(await _billing.SetUnitChargesAsync(
            RequiredGuid(request.ChargeTypeId, "charge_type_id"),
            request.Amounts.Select(a => new UnitAmount(RequiredGuid(a.UnitId, "unit_id"), OptionalMoney(a.Amount, "Amount") ?? 0)).ToList(),
            context.CancellationToken));

    private static ChargeTypeInputDto ToInput(ChargeTypeInput? c) =>
        c is null ? throw InvalidArgument("charge is required.")
        : new ChargeTypeInputDto(c.Name, c.Method, OptionalMoney(c.Amount, "Amount"), c.IsActive, c.SortOrder);

    private static BillingSettings ToProto(BillingSettingsDto s) => new()
    {
        FinancialYearStartMonth = s.FinancialYearStartMonth,
        BillingDay = s.BillingDay,
        DueDay = s.DueDay,
        LevyDisplay = s.LevyDisplay,
        InterestEnabled = s.InterestEnabled,
        InterestRatePercent = Money(s.InterestRatePercent),
        QueriesEmail = s.QueriesEmail ?? "",
        QueriesPhone = s.QueriesPhone ?? "",
        BankName = s.BankName ?? "",
        BankBranchCode = s.BankBranchCode ?? "",
        BankAccountName = s.BankAccountName ?? "",
        BankAccountNumber = s.BankAccountNumber ?? "",
    };

    private static ChargeType ToProto(ChargeTypeDto c) => new()
    {
        Id = c.Id.ToString(), Name = c.Name, Method = c.Method, Amount = c.Amount is { } a ? Money(a) : "",
        IsLevy = c.IsLevy, IsActive = c.IsActive, SortOrder = c.SortOrder,
    };

    private static ListUnitChargesResponse ToProto(IReadOnlyList<UnitChargeDto> rows)
    {
        var response = new ListUnitChargesResponse();
        response.Items.AddRange(rows.Select(r => new UnitChargeAmount { UnitId = r.UnitId.ToString(), SectionNumber = r.SectionNumber, DoorNumber = r.DoorNumber ?? "", Amount = Money(r.Amount) }));
        return response;
    }
}

[RequiresFeature(FeatureCatalog.Billing)]
public class AccountsGrpcService : AccountsService.AccountsServiceBase
{
    private readonly IAccountService _accounts;

    public AccountsGrpcService(IAccountService accounts)
    {
        _accounts = accounts;
    }

    [RequiresPermission(Permissions.BillingRead)]
    [NoOwnershipRequired("Staff see every account of their estate; the tenant layer confines it.")]
    public override async Task<ListAccountsResponse> ListAccounts(ListAccountsRequest request, ServerCallContext context)
    {
        var filter = request.Filter switch
        {
            "" or "all" => AccountFilterDto.All,
            "open" => AccountFilterDto.Open,
            "owing" => AccountFilterDto.Owing,
            "in_credit" => AccountFilterDto.InCredit,
            "former_owners_owing" => AccountFilterDto.FormerOwnersOwing,
            _ => throw InvalidArgument("filter must be all, open, owing, in_credit or former_owners_owing."),
        };
        return ToProto(await _accounts.ListAsync(filter, request.Search, context.CancellationToken));
    }

    [RequiresPermission(Permissions.BillingRead)]
    [NoOwnershipRequired("Staff see every account of their estate; the tenant layer confines it.")]
    public override async Task<ListAccountsResponse> ListUnitAccounts(ListUnitAccountsRequest request, ServerCallContext context) =>
        ToProto(await _accounts.OfUnitAsync(RequiredGuid(request.UnitId, "unit_id"), context.CancellationToken));

    [Authorize]
    [RequiresAccess(ResourceKind.Account, "account_id")]
    public override async Task<AccountDetail> GetAccount(GetAccountRequest request, ServerCallContext context) =>
        ToProto(await _accounts.GetAsync(RequiredGuid(request.AccountId, "account_id"), context.CancellationToken));

    [Authorize]
    [AccessScopedInAction("Only accounts the caller holds (current or former owner).")]
    public override async Task<ListAccountsResponse> MyAccounts(MyAccountsRequest request, ServerCallContext context) =>
        ToProto(await _accounts.MineAsync(context.CancellationToken));

    [RequiresPermission(Permissions.BillingWrite)]
    [RequiresAccess(ResourceKind.Account, "account_id")]
    public override async Task<AccountDetail> RecordOpeningBalance(RecordOpeningBalanceRequest request, ServerCallContext context) =>
        ToProto(await _accounts.RecordOpeningBalanceAsync(
            RequiredGuid(request.AccountId, "account_id"), RequiredMoney(request.Amount, "Amount"),
            OptionalDate(request.Date, "date") ?? throw InvalidArgument("The date is required."), context.CancellationToken));

    [RequiresPermission(Permissions.BillingWrite)]
    [NoOwnershipRequired("Staff correct entries of their estate; the tenant layer confines entry ids.")]
    public override async Task<AccountDetail> ReverseEntry(ReverseEntryRequest request, ServerCallContext context) =>
        ToProto(await _accounts.ReverseAsync(RequiredGuid(request.EntryId, "entry_id"), request.Reason, context.CancellationToken));

    private static AccountSummary ToProto(AccountSummaryDto a)
    {
        var summary = new AccountSummary
        {
            Id = a.Id.ToString(), UnitId = a.UnitId.ToString(), SectionNumber = a.SectionNumber, Number = a.Number,
            OpenedOn = Date(a.OpenedOn), ClosedOn = Date(a.ClosedOn), Balance = Money(a.Balance),
        };
        summary.Holders.AddRange(a.Holders);
        return summary;
    }

    private static ListAccountsResponse ToProto(IReadOnlyList<AccountSummaryDto> items)
    {
        var response = new ListAccountsResponse();
        response.Items.AddRange(items.Select(ToProto));
        return response;
    }

    private static AccountDetail ToProto(AccountDetailDto d)
    {
        var detail = new AccountDetail { Account = ToProto(d.Account) };
        detail.Entries.AddRange(d.Entries.Select(e => new LedgerLine
        {
            Id = e.Id.ToString(), Date = Date(e.Date), Type = e.Type, Description = e.Description, Reference = e.Reference ?? "",
            Debit = Money(e.Debit), Credit = Money(e.Credit), RunningBalance = Money(e.RunningBalance),
            ReversesEntryId = e.ReversesEntryId?.ToString() ?? "", IsReversed = e.IsReversed,
        }));
        return detail;
    }
}
