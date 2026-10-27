using EstateHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EstateHub.Infrastructure.Data.Configurations;

// The app database role only gets INSERT/SELECT on this table (see docker/postgres/init and the
// migration that creates it): the audit trail is append-only even if the API is compromised.
public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> entity)
    {
        entity.ToTable("audit_logs");

        entity.HasKey(x => x.Id);

        entity.Property(x => x.EntityType).IsRequired().HasMaxLength(200);
        entity.Property(x => x.EntityId).IsRequired().HasMaxLength(100);
        entity.Property(x => x.Action).IsRequired().HasMaxLength(200);
        entity.Property(x => x.PerformedBy).IsRequired().HasMaxLength(200);
        entity.Property(x => x.PerformedAt).IsRequired();
        entity.Property(x => x.Details).HasMaxLength(4000);
        entity.Property(x => x.IpAddress).HasMaxLength(45);

        entity.HasIndex(x => new { x.EstateId, x.PerformedAt });
        entity.HasIndex(x => new { x.EstateId, x.EntityType, x.EntityId });
    }
}
