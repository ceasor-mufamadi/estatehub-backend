using EstateHub.Domain.Entities;

namespace EstateHub.Application.Interfaces;

/// <summary>Billing setup (slice 4.1): settings, the scheme's bank details, charge types and per-unit amounts.</summary>
public interface IBillingService
{
    /// <summary>The estate's settings; created with defaults (and the built-in Levy charge type) the first time.</summary>
    Task<BillingSettingsDto> GetSettingsAsync(CancellationToken ct = default);
    Task<BillingSettingsDto> UpdateSettingsAsync(BillingSettingsInput input, CancellationToken ct = default);

    /// <summary>Printed on statements. Needs a fresh MFA (API) and every trustee is emailed.</summary>
    Task<BillingSettingsDto> UpdateBankDetailsAsync(BankDetailsInput input, CancellationToken ct = default);

    Task<IReadOnlyList<ChargeTypeDto>> ListChargeTypesAsync(CancellationToken ct = default);
    Task<ChargeTypeDto> CreateChargeTypeAsync(ChargeTypeInput input, CancellationToken ct = default);
    Task<ChargeTypeDto> UpdateChargeTypeAsync(Guid id, ChargeTypeInput input, CancellationToken ct = default);
    Task<IReadOnlyList<UnitChargeDto>> ListUnitChargesAsync(Guid chargeTypeId, CancellationToken ct = default);
    Task<IReadOnlyList<UnitChargeDto>> SetUnitChargesAsync(Guid chargeTypeId, IReadOnlyList<UnitAmount> amounts, CancellationToken ct = default);
}

/// <summary>
/// Accounts (one per ownership period, decided 2026-10-28) and the ledger. Accounts follow ownership:
/// <see cref="SyncUnitAsync"/> runs after every ownership change, before a billing run, and nightly.
/// </summary>
public interface IAccountService
{
    Task SyncUnitAsync(Guid unitId, CancellationToken ct = default);
    Task<int> SyncAllAsync(CancellationToken ct = default);

    Task<IReadOnlyList<AccountSummaryDto>> ListAsync(AccountFilter filter, string? search, CancellationToken ct = default);
    Task<IReadOnlyList<AccountSummaryDto>> OfUnitAsync(Guid unitId, CancellationToken ct = default);
    Task<AccountDetailDto> GetAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>Residents: accounts they hold (current, and former ones after a sale).</summary>
    Task<IReadOnlyList<AccountSummaryDto>> MineAsync(CancellationToken ct = default);

    /// <summary>
    /// Go-live: what the account owed (positive) or had in credit (negative) on <paramref name="date"/>.
    /// One per account; a mistake is reversed first.
    /// </summary>
    Task<AccountDetailDto> RecordOpeningBalanceAsync(Guid accountId, decimal amount, DateOnly date, CancellationToken ct = default);

    /// <summary>Cancels an entry with an opposite one ("Incorrect …"), linked to it. Each entry once.</summary>
    Task<AccountDetailDto> ReverseAsync(Guid entryId, string? reason, CancellationToken ct = default);
}

public enum AccountFilter { All, Open, Owing, InCredit, FormerOwnersOwing }

public record BillingSettingsDto(
    int FinancialYearStartMonth, int BillingDay, int DueDay, string LevyDisplay, bool InterestEnabled, decimal InterestRatePercent,
    string? QueriesEmail, string? QueriesPhone, string? BankName, string? BankBranchCode, string? BankAccountName, string? BankAccountNumber);

public record BillingSettingsInput(
    int FinancialYearStartMonth, int BillingDay, int DueDay, string LevyDisplay, bool InterestEnabled, decimal InterestRatePercent,
    string? QueriesEmail, string? QueriesPhone);

public record BankDetailsInput(string? BankName, string? BranchCode, string? AccountName, string? AccountNumber);

public record ChargeTypeDto(Guid Id, string Name, string Method, decimal? Amount, bool IsLevy, bool IsActive, int SortOrder);

public record ChargeTypeInput(string Name, string Method, decimal? Amount, bool IsActive, int SortOrder);

public record UnitAmount(Guid UnitId, decimal Amount);

public record UnitChargeDto(Guid UnitId, int SectionNumber, string? DoorNumber, decimal Amount);

public record AccountSummaryDto(
    Guid Id, Guid UnitId, int SectionNumber, string Number, IReadOnlyList<string> Holders, DateOnly OpenedOn, DateOnly? ClosedOn, decimal Balance);

public record LedgerLineDto(
    Guid Id, DateOnly Date, string Type, string Description, string? Reference, decimal Debit, decimal Credit, decimal RunningBalance,
    Guid? ReversesEntryId, bool IsReversed);

public record AccountDetailDto(AccountSummaryDto Account, IReadOnlyList<LedgerLineDto> Entries);

public interface IBillingRepository
{
    Task<BillingSettings?> GetSettingsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ChargeType>> ListChargeTypesAsync(CancellationToken ct = default);
    Task<ChargeType?> GetChargeTypeAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<UnitCharge>> ListUnitChargesAsync(Guid chargeTypeId, CancellationToken ct = default);

    Task<IReadOnlyList<Account>> ListAccountsAsync(CancellationToken ct = default);
    Task<Account?> GetAccountAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<AccountHolder>> HoldersAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> AccountsHeldByAsync(IReadOnlyCollection<Guid> personIds, CancellationToken ct = default);
    Task<IReadOnlyList<LedgerEntry>> EntriesAsync(Guid accountId, CancellationToken ct = default);
    Task<LedgerEntry?> GetEntryAsync(Guid id, CancellationToken ct = default);
    Task<bool> AnyEntriesAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>Balance (debits − credits) per account, in one query.</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> BalancesAsync(CancellationToken ct = default);

    void Add(object entity);
    void Remove(object entity);
    Task SaveChangesAsync(CancellationToken ct = default);
}
