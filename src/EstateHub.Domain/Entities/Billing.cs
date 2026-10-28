namespace EstateHub.Domain.Entities;

/// <summary>
/// An estate's billing rules (PHASE1.md "Sprint 4 design"). One row per estate, created with defaults
/// the first time billing is opened. Scheme-specific rules live here, not in code.
/// </summary>
public class BillingSettings : ITenantScoped, IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EstateId { get; set; }

    /// <summary>First month of the financial year (1–12); budgets are per financial year.</summary>
    public int FinancialYearStartMonth { get; set; } = 3;

    /// <summary>Day of the month charges are raised (1–28).</summary>
    public int BillingDay { get; set; } = 1;

    /// <summary>Day of the month payment is due (1–28); interest starts the day after.</summary>
    public int DueDay { get; set; } = 7;

    /// <summary>"combined": one "Levy" line on statements; "split": admin and reserve fund lines. The ledger always keeps both.</summary>
    public string LevyDisplay { get; set; } = LevyDisplays.Combined;

    public bool InterestEnabled { get; set; }

    /// <summary>Annual rate in percent, charged monthly as rate ÷ 12 on overdue balances.</summary>
    public decimal InterestRatePercent { get; set; }

    public string? QueriesEmail { get; set; }
    public string? QueriesPhone { get; set; }

    // The scheme's bank account, printed on statements. Changing it needs a fresh MFA and alerts every trustee.
    public string? BankName { get; set; }
    public string? BankBranchCode { get; set; }
    public string? BankAccountName { get; set; }
    public string? BankAccountNumber { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class LevyDisplays
{
    public const string Combined = "combined";
    public const string Split = "split";
}

/// <summary>A monthly (or one-off) line the estate charges, under its own name on statements.</summary>
public class ChargeType : ITenantScoped, IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EstateId { get; set; }

    /// <summary>As printed on statements, e.g. "Levy", "CSOS", "Sanitation", "Water".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>See <see cref="ChargeMethods"/>.</summary>
    public string Method { get; set; } = ChargeMethods.Flat;

    /// <summary>For "flat": the amount every unit pays each month.</summary>
    public decimal? Amount { get; set; }

    /// <summary>The levy (by quota) is built in: one per estate, can't be removed or renamed away from its method.</summary>
    public bool IsLevy { get; set; }

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class ChargeMethods
{
    /// <summary>The year's budget split by participation quota (the levy).</summary>
    public const string Quota = "quota";

    /// <summary>The same amount for every unit each month.</summary>
    public const string Flat = "flat";

    /// <summary>A fixed monthly amount set on each unit.</summary>
    public const string PerUnit = "per_unit";

    /// <summary>Uploaded each month (water, electricity).</summary>
    public const string Metered = "metered";

    /// <summary>Raised by staff when needed (fines, keys, legal costs).</summary>
    public const string OneOff = "one_off";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Quota, Flat, PerUnit, Metered, OneOff };
}

/// <summary>A unit's own monthly amount for a "per_unit" charge type.</summary>
public class UnitCharge : ITenantScoped, IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EstateId { get; set; }
    public Guid ChargeTypeId { get; set; }
    public Guid UnitId { get; set; }
    public decimal Amount { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A unit's account for one ownership period (decided 2026-10-28): co-owners share it; when the owners
/// change it closes and keeps its balance (arrears stay with the former owner), and a new one opens at R 0.
/// </summary>
public class Account : ITenantScoped, IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EstateId { get; set; }
    public Guid UnitId { get; set; }

    /// <summary>The unit's number (door number, else section): what owners use as their payment reference.</summary>
    public string Number { get; set; } = string.Empty;

    public DateOnly OpenedOn { get; set; }
    public DateOnly? ClosedOn { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Who an account belongs to (the owners of that period).</summary>
public class AccountHolder : ITenantScoped, IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EstateId { get; set; }
    public Guid AccountId { get; set; }
    public Guid PersonId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// One line on an account. Never edited or deleted (the database refuses UPDATE and DELETE for the app):
/// a mistake is fixed by a reversal linked to the original, plus a new entry. Balance = debits − credits.
/// </summary>
public class LedgerEntry : ITenantScoped, IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EstateId { get; set; }
    public Guid AccountId { get; set; }

    /// <summary>The day it counts from (statements and the age analysis use it).</summary>
    public DateOnly Date { get; set; }

    /// <summary>See <see cref="LedgerTypes"/>.</summary>
    public string Type { get; set; } = LedgerTypes.Adjustment;

    public Guid? ChargeTypeId { get; set; }
    public string Description { get; set; } = string.Empty;

    /// <summary>E.g. a run or meter reference; shown on statements.</summary>
    public string? Reference { get; set; }

    public decimal Debit { get; set; }
    public decimal Credit { get; set; }

    /// <summary>The billing month it belongs to, "2026-11" (null for payments and opening balances).</summary>
    public string? Period { get; set; }

    /// <summary>Where it came from: opening, run, upload, staff, interest, payment.</summary>
    public string Source { get; set; } = LedgerSources.Staff;
    public Guid? SourceId { get; set; }

    /// <summary>For a reversal: the entry it cancels.</summary>
    public Guid? ReversesEntryId { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class LedgerTypes
{
    public const string OpeningBalance = "opening_balance";
    public const string LevyAdmin = "levy_admin";
    public const string LevyReserve = "levy_reserve";
    public const string Charge = "charge";
    public const string SpecialLevy = "special_levy";
    public const string Interest = "interest";
    public const string Payment = "payment";
    public const string Reversal = "reversal";
    public const string WriteOff = "write_off";
    public const string Adjustment = "adjustment";
}

public static class LedgerSources
{
    public const string Opening = "opening";
    public const string Run = "run";
    public const string Upload = "upload";
    public const string Staff = "staff";
    public const string Interest = "interest";
    public const string Payment = "payment";
}

/// <summary>
/// The levy budget from a month on (slice 4.2): monthly admin and reserve fund totals, split by
/// participation quota. A new budget (e.g. after the AGM) applies from its first month; history stays.
/// </summary>
public class LevyBudget : ITenantScoped, IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EstateId { get; set; }

    /// <summary>First month it applies to, "2026-03".</summary>
    public string EffectiveFrom { get; set; } = string.Empty;

    public decimal AdminFundMonthly { get; set; }
    public decimal ReserveFundMonthly { get; set; }

    public string? Note { get; set; }
    public Guid CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>A unit's amount for a metered charge in one month, from an upload. Replaced by a new upload until the month is posted.</summary>
public class MeteredAmount : ITenantScoped, IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EstateId { get; set; }
    public string Period { get; set; } = string.Empty;
    public Guid ChargeTypeId { get; set; }
    public Guid UnitId { get; set; }
    public decimal Amount { get; set; }

    /// <summary>The meter reading or reference from the file, shown on the statement line.</summary>
    public string? Reference { get; set; }

    public string? FileName { get; set; }
    public Guid UploadedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// One month's charges for the whole estate (slice 4.2). A draft is worked out live from the budget,
/// charges and uploads; posting writes the ledger entries in one transaction, once per month.
/// </summary>
public class BillingRun : ITenantScoped, IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EstateId { get; set; }

    /// <summary>"2026-11": unique per estate.</summary>
    public string Period { get; set; } = string.Empty;

    /// <summary>See <see cref="BillingRunStatuses"/>.</summary>
    public string Status { get; set; } = BillingRunStatuses.Draft;

    /// <summary>The date the entries carry (the estate's billing day in that month).</summary>
    public DateOnly BillingDate { get; set; }

    public decimal TotalPosted { get; set; }
    public int AccountsPosted { get; set; }
    public string? Message { get; set; }

    public Guid? PostedByUserId { get; set; }
    public DateTime? PostedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class BillingRunStatuses
{
    public const string Draft = "draft";
    public const string Queued = "queued";
    public const string Posting = "posting";
    public const string Posted = "posted";

    public static bool IsActive(string status) => status is Queued or Posting;
}
