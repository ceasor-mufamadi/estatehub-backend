using EstateHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EstateHub.Infrastructure.Data.Configurations;

public class ImportBatchConfiguration : IEntityTypeConfiguration<ImportBatch>
{
    public void Configure(EntityTypeBuilder<ImportBatch> entity)
    {
        entity.ToTable("import_batches");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.FileName).IsRequired().HasMaxLength(200);
        entity.Property(x => x.Status).IsRequired().HasMaxLength(20);
        entity.Property(x => x.RowsJson).IsRequired().HasColumnType("jsonb");
        entity.Property(x => x.ProblemsJson).IsRequired().HasColumnType("jsonb");
        entity.Property(x => x.SummaryJson).IsRequired().HasColumnType("jsonb");
        entity.Property(x => x.Message).HasMaxLength(500);
        entity.Property(x => x.CommittedByRole).HasMaxLength(50);
        entity.HasIndex(x => new { x.EstateId, x.CreatedAt });
        // One import at a time per estate (SECURITY.md layer 6): a second commit racing the first fails here.
        entity.HasIndex(x => x.EstateId, "ux_import_batches_one_active").IsUnique()
            .HasFilter("status IN ('queued', 'running')").HasDatabaseName("ux_import_batches_one_active");
        entity.HasOne<Estate>().WithMany().HasForeignKey(x => x.EstateId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class ImportInviteConfiguration : IEntityTypeConfiguration<ImportInvite>
{
    public void Configure(EntityTypeBuilder<ImportInvite> entity)
    {
        entity.ToTable("import_invites");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Status).IsRequired().HasMaxLength(20);
        entity.Property(x => x.Message).HasMaxLength(500);
        entity.HasIndex(x => new { x.ImportBatchId, x.Status });
        entity.HasOne<Estate>().WithMany().HasForeignKey(x => x.EstateId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<ImportBatch>().WithMany().HasForeignKey(x => x.ImportBatchId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Person>().WithMany().HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.Cascade);
    }
}
