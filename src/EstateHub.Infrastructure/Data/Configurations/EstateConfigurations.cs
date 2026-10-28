using EstateHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EstateHub.Infrastructure.Data.Configurations;

public class OrganisationConfiguration : IEntityTypeConfiguration<Organisation>
{
    public void Configure(EntityTypeBuilder<Organisation> entity)
    {
        entity.ToTable("organisations");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Name).IsRequired().HasMaxLength(200);
    }
}

public class EstateConfiguration : IEntityTypeConfiguration<Estate>
{
    public void Configure(EntityTypeBuilder<Estate> entity)
    {
        entity.ToTable("estates");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Name).IsRequired().HasMaxLength(200);
        entity.Property(x => x.SchemeType).IsRequired().HasMaxLength(32);
        entity.Property(x => x.TimeZone).IsRequired().HasMaxLength(64);
        entity.Property(x => x.Currency).IsRequired().HasMaxLength(3);
        entity.Property(x => x.SchemeNumber).HasMaxLength(50);
        entity.Property(x => x.CsosRegistrationNumber).HasMaxLength(50);
        entity.Property(x => x.Address).HasMaxLength(500);
        entity.Property(x => x.InformationOfficerName).HasMaxLength(200);
        entity.Property(x => x.InformationOfficerEmail).HasMaxLength(254);

        entity.HasOne<Organisation>().WithMany().HasForeignKey(x => x.OrganisationId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class EstateFeatureConfiguration : IEntityTypeConfiguration<EstateFeature>
{
    public void Configure(EntityTypeBuilder<EstateFeature> entity)
    {
        entity.ToTable("estate_features");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.FeatureKey).IsRequired().HasMaxLength(64);
        entity.Property(x => x.SettingsJson).HasColumnType("jsonb");
        entity.Property(x => x.UpdatedBy).IsRequired().HasMaxLength(200);

        entity.HasIndex(x => new { x.EstateId, x.FeatureKey }).IsUnique();
        entity.HasOne<Estate>().WithMany().HasForeignKey(x => x.EstateId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public void Configure(EntityTypeBuilder<Membership> entity)
    {
        entity.ToTable("memberships");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Role).IsRequired().HasMaxLength(32);

        entity.HasIndex(x => new { x.EstateId, x.UserId }).IsUnique();
        entity.HasIndex(x => x.UserId);
        entity.HasOne<Estate>().WithMany().HasForeignKey(x => x.EstateId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

// Tenant-scoped (RLS). Accepting needs the estate before RLS lets the row be read, so the
// estatehub_invitation_estate SECURITY DEFINER function maps a token hash to its estate first.
public class InvitationConfiguration : IEntityTypeConfiguration<Invitation>
{
    public void Configure(EntityTypeBuilder<Invitation> entity)
    {
        entity.ToTable("invitations");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Name).IsRequired().HasMaxLength(200);
        entity.Property(x => x.Email).HasMaxLength(254);
        entity.Property(x => x.Phone).HasMaxLength(16);
        entity.Property(x => x.Role).IsRequired().HasMaxLength(32);
        entity.Property(x => x.TokenHash).IsRequired().HasMaxLength(64);

        entity.HasIndex(x => x.TokenHash).IsUnique();
        entity.HasIndex(x => new { x.EstateId, x.LastSentAt });
        entity.HasOne<Estate>().WithMany().HasForeignKey(x => x.EstateId).OnDelete(DeleteBehavior.Cascade);
    }
}

// Guard tablets (AUTH-14): tenant-scoped (RLS). A tablet's credential and a pairing code are used
// before any estate is active, so estatehub_device_estate maps their hashes to the estate first.
public class GuardDeviceConfiguration : IEntityTypeConfiguration<GuardDevice>
{
    public void Configure(EntityTypeBuilder<GuardDevice> entity)
    {
        entity.ToTable("guard_devices");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Label).IsRequired().HasMaxLength(100);
        entity.Property(x => x.CredentialHash).IsRequired().HasMaxLength(64);
        entity.HasIndex(x => x.CredentialHash).IsUnique();
        entity.HasOne<Estate>().WithMany().HasForeignKey(x => x.EstateId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class PairingCodeConfiguration : IEntityTypeConfiguration<PairingCode>
{
    public void Configure(EntityTypeBuilder<PairingCode> entity)
    {
        entity.ToTable("pairing_codes");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Label).IsRequired().HasMaxLength(100);
        entity.Property(x => x.CodeHash).IsRequired().HasMaxLength(64);
        entity.HasIndex(x => x.CodeHash).IsUnique();
        entity.HasOne<Estate>().WithMany().HasForeignKey(x => x.EstateId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class GuardPinConfiguration : IEntityTypeConfiguration<GuardPin>
{
    public void Configure(EntityTypeBuilder<GuardPin> entity)
    {
        entity.ToTable("guard_pins");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.PinHash).IsRequired().HasMaxLength(200);
        entity.HasIndex(x => new { x.EstateId, x.UserId }).IsUnique();
        entity.HasOne<Estate>().WithMany().HasForeignKey(x => x.EstateId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

// Tenant-scoped (RLS). Section numbers are unique per estate.
public class UnitConfiguration : IEntityTypeConfiguration<Unit>
{
    public void Configure(EntityTypeBuilder<Unit> entity)
    {
        entity.ToTable("units");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.DoorNumber).HasMaxLength(20);
        entity.Property(x => x.ParticipationQuota).HasPrecision(9, 6);
        entity.Property(x => x.SizeSquareMetres).HasPrecision(8, 2);

        entity.HasIndex(x => new { x.EstateId, x.SectionNumber }).IsUnique();
        entity.HasIndex(x => x.BlockId);
        entity.HasOne<Estate>().WithMany().HasForeignKey(x => x.EstateId).OnDelete(DeleteBehavior.Cascade);
        // A block with units can't be removed (BlockService refuses; the database backs it up).
        entity.HasOne<Block>().WithMany().HasForeignKey(x => x.BlockId).OnDelete(DeleteBehavior.Restrict);
    }
}

// People, ownership and occupancy are tenant-scoped (RLS) and personal information (POPIA).
public class PersonConfiguration : IEntityTypeConfiguration<Person>
{
    public void Configure(EntityTypeBuilder<Person> entity)
    {
        entity.ToTable("people");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Name).IsRequired().HasMaxLength(200);
        entity.Property(x => x.Email).HasMaxLength(254);
        entity.Property(x => x.Phone).HasMaxLength(16);
        entity.Property(x => x.PrivacyNoticeVersion).HasMaxLength(20);

        entity.HasIndex(x => new { x.EstateId, x.Email }).IsUnique();
        entity.HasIndex(x => new { x.EstateId, x.Phone }).IsUnique();
        entity.HasIndex(x => new { x.EstateId, x.UserId });
        entity.HasOne<Estate>().WithMany().HasForeignKey(x => x.EstateId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.SetNull);
    }
}

public class OwnershipConfiguration : IEntityTypeConfiguration<Ownership>
{
    public void Configure(EntityTypeBuilder<Ownership> entity)
    {
        entity.ToTable("ownerships");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.SharePercent).HasPrecision(5, 2);
        entity.HasIndex(x => new { x.EstateId, x.UnitId });
        entity.HasIndex(x => new { x.EstateId, x.PersonId });
        entity.HasOne<Estate>().WithMany().HasForeignKey(x => x.EstateId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Unit>().WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Person>().WithMany().HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class OccupancyConfiguration : IEntityTypeConfiguration<Occupancy>
{
    public void Configure(EntityTypeBuilder<Occupancy> entity)
    {
        entity.ToTable("occupancies");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Kind).IsRequired().HasMaxLength(20);
        entity.HasIndex(x => new { x.EstateId, x.UnitId });
        entity.HasIndex(x => new { x.EstateId, x.PersonId });
        // A person lives in one unit at a time (owners may own many; that's ownerships, not here).
        entity.HasIndex(x => x.PersonId, "ux_occupancies_one_open_home").IsUnique().HasFilter("to_date IS NULL").HasDatabaseName("ux_occupancies_one_open_home");
        entity.HasIndex(x => x.PersonId, "ix_occupancies_person_id");
        entity.HasOne<Estate>().WithMany().HasForeignKey(x => x.EstateId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Unit>().WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Person>().WithMany().HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class BlockConfiguration : IEntityTypeConfiguration<Block>
{
    public void Configure(EntityTypeBuilder<Block> entity)
    {
        entity.ToTable("blocks");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Name).IsRequired().HasMaxLength(100);

        entity.HasIndex(x => new { x.EstateId, x.Name }).IsUnique();
        entity.HasOne<Estate>().WithMany().HasForeignKey(x => x.EstateId).OnDelete(DeleteBehavior.Cascade);
    }
}
