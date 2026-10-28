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
