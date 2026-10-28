using EstateHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EstateHub.Infrastructure.Data.Configurations;

// Money is numeric(14,2) everywhere (PHASE1.md §4): rands and cents, never floating point.
public class BillingSettingsConfiguration : IEntityTypeConfiguration<BillingSettings>
{
    public void Configure(EntityTypeBuilder<BillingSettings> entity)
    {
        entity.ToTable("billing_settings");
        entity.HasKey(x => x.Id);
        entity.HasIndex(x => x.EstateId).IsUnique();
        entity.Property(x => x.LevyDisplay).IsRequired().HasMaxLength(10);
        entity.Property(x => x.InterestRatePercent).HasColumnType("numeric(5,2)");
        entity.Property(x => x.QueriesEmail).HasMaxLength(254);
        entity.Property(x => x.QueriesPhone).HasMaxLength(30);
        entity.Property(x => x.BankName).HasMaxLength(100);
        entity.Property(x => x.BankBranchCode).HasMaxLength(10);
        entity.Property(x => x.BankAccountName).HasMaxLength(200);
        entity.Property(x => x.BankAccountNumber).HasMaxLength(20);
        entity.HasOne<Estate>().WithMany().HasForeignKey(x => x.EstateId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class ChargeTypeConfiguration : IEntityTypeConfiguration<ChargeType>
{
    public void Configure(EntityTypeBuilder<ChargeType> entity)
    {
        entity.ToTable("charge_types");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Name).IsRequired().HasMaxLength(60);
        entity.Property(x => x.Method).IsRequired().HasMaxLength(10);
        entity.Property(x => x.Amount).HasColumnType("numeric(14,2)");
        entity.HasIndex(x => new { x.EstateId, x.Name }).IsUnique();
        // One built-in levy per estate.
        entity.HasIndex(x => x.EstateId, "ux_charge_types_one_levy").IsUnique().HasFilter("is_levy").HasDatabaseName("ux_charge_types_one_levy");
        entity.HasOne<Estate>().WithMany().HasForeignKey(x => x.EstateId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class UnitChargeConfiguration : IEntityTypeConfiguration<UnitCharge>
{
    public void Configure(EntityTypeBuilder<UnitCharge> entity)
    {
        entity.ToTable("unit_charges");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Amount).HasColumnType("numeric(14,2)");
        entity.HasIndex(x => new { x.ChargeTypeId, x.UnitId }).IsUnique();
        entity.HasOne<Estate>().WithMany().HasForeignKey(x => x.EstateId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<ChargeType>().WithMany().HasForeignKey(x => x.ChargeTypeId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Unit>().WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> entity)
    {
        entity.ToTable("accounts");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Number).IsRequired().HasMaxLength(20);
        entity.HasIndex(x => new { x.EstateId, x.UnitId });
        // One open account per unit at a time.
        entity.HasIndex(x => x.UnitId, "ux_accounts_one_open_per_unit").IsUnique().HasFilter("closed_on IS NULL").HasDatabaseName("ux_accounts_one_open_per_unit");
        entity.HasOne<Estate>().WithMany().HasForeignKey(x => x.EstateId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Unit>().WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class AccountHolderConfiguration : IEntityTypeConfiguration<AccountHolder>
{
    public void Configure(EntityTypeBuilder<AccountHolder> entity)
    {
        entity.ToTable("account_holders");
        entity.HasKey(x => x.Id);
        entity.HasIndex(x => new { x.AccountId, x.PersonId }).IsUnique();
        entity.HasIndex(x => x.PersonId);
        entity.HasOne<Estate>().WithMany().HasForeignKey(x => x.EstateId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Person>().WithMany().HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class LedgerEntryConfiguration : IEntityTypeConfiguration<LedgerEntry>
{
    public void Configure(EntityTypeBuilder<LedgerEntry> entity)
    {
        entity.ToTable("ledger_entries");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Type).IsRequired().HasMaxLength(20);
        entity.Property(x => x.Description).IsRequired().HasMaxLength(200);
        entity.Property(x => x.Reference).HasMaxLength(40);
        entity.Property(x => x.Debit).HasColumnType("numeric(14,2)");
        entity.Property(x => x.Credit).HasColumnType("numeric(14,2)");
        entity.Property(x => x.Period).HasMaxLength(7);
        entity.Property(x => x.Source).IsRequired().HasMaxLength(10);
        entity.HasIndex(x => new { x.EstateId, x.AccountId, x.Date });
        entity.HasIndex(x => x.SourceId);
        // A reversal cancels one entry, once.
        entity.HasIndex(x => x.ReversesEntryId).IsUnique().HasFilter("reverses_entry_id IS NOT NULL");
        entity.HasOne<Estate>().WithMany().HasForeignKey(x => x.EstateId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<ChargeType>().WithMany().HasForeignKey(x => x.ChargeTypeId).OnDelete(DeleteBehavior.Restrict);
        entity.ToTable(t => t.HasCheckConstraint("ck_ledger_one_side", "(debit >= 0 AND credit >= 0) AND (debit = 0 OR credit = 0) AND (debit + credit > 0)"));
    }
}

public class LevyBudgetConfiguration : IEntityTypeConfiguration<LevyBudget>
{
    public void Configure(EntityTypeBuilder<LevyBudget> entity)
    {
        entity.ToTable("levy_budgets");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.EffectiveFrom).IsRequired().HasMaxLength(7);
        entity.Property(x => x.AdminFundMonthly).HasColumnType("numeric(14,2)");
        entity.Property(x => x.ReserveFundMonthly).HasColumnType("numeric(14,2)");
        entity.Property(x => x.Note).HasMaxLength(200);
        entity.HasIndex(x => new { x.EstateId, x.EffectiveFrom }).IsUnique();
        entity.HasOne<Estate>().WithMany().HasForeignKey(x => x.EstateId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class MeteredAmountConfiguration : IEntityTypeConfiguration<MeteredAmount>
{
    public void Configure(EntityTypeBuilder<MeteredAmount> entity)
    {
        entity.ToTable("metered_amounts");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Period).IsRequired().HasMaxLength(7);
        entity.Property(x => x.Amount).HasColumnType("numeric(14,2)");
        entity.Property(x => x.Reference).HasMaxLength(40);
        entity.Property(x => x.FileName).HasMaxLength(200);
        entity.HasIndex(x => new { x.EstateId, x.Period, x.ChargeTypeId, x.UnitId }).IsUnique();
        entity.HasOne<Estate>().WithMany().HasForeignKey(x => x.EstateId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<ChargeType>().WithMany().HasForeignKey(x => x.ChargeTypeId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Unit>().WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class BillingRunConfiguration : IEntityTypeConfiguration<BillingRun>
{
    public void Configure(EntityTypeBuilder<BillingRun> entity)
    {
        entity.ToTable("billing_runs");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Period).IsRequired().HasMaxLength(7);
        entity.Property(x => x.Status).IsRequired().HasMaxLength(10);
        entity.Property(x => x.TotalPosted).HasColumnType("numeric(14,2)");
        entity.Property(x => x.Message).HasMaxLength(500);
        // A month is billed once per estate (PHASE1.md §1c: natural key + unique index).
        entity.HasIndex(x => new { x.EstateId, x.Period }).IsUnique();
        entity.HasOne<Estate>().WithMany().HasForeignKey(x => x.EstateId).OnDelete(DeleteBehavior.Cascade);
    }
}
