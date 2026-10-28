using System.Reflection;
using EstateHub.Application.Tenancy;
using EstateHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace EstateHub.Infrastructure.Data;

/// <summary>
/// Tenant isolation, layer 2 of 3 (interceptor → this → Postgres RLS):
/// every <see cref="ITenantScoped"/> query is filtered to the active estate, inserts are stamped
/// with it, and writes that would touch another estate's rows throw. With no active estate,
/// tenant-scoped data is invisible and unwritable.
/// </summary>
public class EstateHubDbContext : DbContext
{
    private static readonly MethodInfo ApplyTenantFilterMethod =
        typeof(EstateHubDbContext).GetMethod(nameof(ApplyTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;

    public EstateHubDbContext(DbContextOptions<EstateHubDbContext> options, ITenantContext tenant, TimeProvider clock) : base(options)
    {
        _tenant = tenant;
        _clock = clock;
    }

    // Read by the query filters. EF evaluates it per query against this context instance.
    private Guid CurrentEstateId => _tenant.EstateId ?? Guid.Empty;

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Organisation> Organisations => Set<Organisation>();
    public DbSet<Estate> Estates => Set<Estate>();
    public DbSet<EstateFeature> EstateFeatures => Set<EstateFeature>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Block> Blocks => Set<Block>();
    public DbSet<Unit> Units => Set<Unit>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<BillingSettings> BillingSettings => Set<BillingSettings>();
    public DbSet<ChargeType> ChargeTypes => Set<ChargeType>();
    public DbSet<UnitCharge> UnitCharges => Set<UnitCharge>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<AccountHolder> AccountHolders => Set<AccountHolder>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<LevyBudget> LevyBudgets => Set<LevyBudget>();
    public DbSet<MeteredAmount> MeteredAmounts => Set<MeteredAmount>();
    public DbSet<BillingRun> BillingRuns => Set<BillingRun>();
    public DbSet<ImportInvite> ImportInvites => Set<ImportInvite>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<Ownership> Ownerships => Set<Ownership>();
    public DbSet<Occupancy> Occupancies => Set<Occupancy>();
    public DbSet<User> Users => Set<User>();
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();
    public DbSet<MfaChallenge> MfaChallenges => Set<MfaChallenge>();
    public DbSet<OtpChallenge> OtpChallenges => Set<OtpChallenge>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<SigningKey> SigningKeys => Set<SigningKey>();
    public DbSet<GuardDevice> GuardDevices => Set<GuardDevice>();
    public DbSet<PairingCode> PairingCodes => Set<PairingCode>();
    public DbSet<GuardPin> GuardPins => Set<GuardPin>();
    public DbSet<UserPasskey> UserPasskeys => Set<UserPasskey>();
    public DbSet<PasskeyChallenge> PasskeyChallenges => Set<PasskeyChallenge>();
    public DbSet<KnownDevice> KnownDevices => Set<KnownDevice>();
    public DbSet<SignInReport> SignInReports => Set<SignInReport>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EstateHubDbContext).Assembly);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(t => typeof(ITenantScoped).IsAssignableFrom(t.ClrType)))
        {
            ApplyTenantFilterMethod.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardTenantWrites();
        StampTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardTenantWrites();
        StampTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // House rule: every row carries created/updated timestamps, set here and never by hand.
    private void StampTimestamps()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var entry in ChangeTracker.Entries<IHasTimestamps>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.UpdatedAt = now;
                    break;
                case EntityState.Modified:
                    entry.Property(e => e.CreatedAt).IsModified = false;
                    entry.Entity.UpdatedAt = now;
                    break;
            }
        }
    }

    private void ApplyTenantFilter<T>(ModelBuilder modelBuilder) where T : class, ITenantScoped =>
        modelBuilder.Entity<T>().HasQueryFilter(e => e.EstateId == CurrentEstateId);

    private void GuardTenantWrites()
    {
        foreach (var entry in ChangeTracker.Entries<ITenantScoped>())
        {
            if (entry.State is EntityState.Unchanged or EntityState.Detached)
                continue;

            var active = _tenant.EstateId
                ?? throw new InvalidOperationException(
                    $"Cannot write {entry.Entity.GetType().Name}: no active estate. Platform code must open a scope for the estate first.");

            if (entry.State == EntityState.Added && entry.Entity.EstateId == Guid.Empty)
                entry.Entity.EstateId = active;

            if (entry.Entity.EstateId != active)
                throw new InvalidOperationException(
                    $"Cannot write {entry.Entity.GetType().Name} of another estate.");

            if (entry.State == EntityState.Modified && entry.Property(e => e.EstateId).IsModified)
                throw new InvalidOperationException(
                    $"Cannot move {entry.Entity.GetType().Name} to another estate.");
        }
    }
}
