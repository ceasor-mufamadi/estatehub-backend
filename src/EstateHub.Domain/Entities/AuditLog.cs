namespace EstateHub.Domain.Entities;

// EntityId is a string so entities with any key type can be audited.
// EstateId is null for platform-level events (estate created, super admin actions).
public class AuditLog : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? EstateId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string PerformedBy { get; set; } = string.Empty;
    public DateTime PerformedAt { get; set; } = DateTime.UtcNow;

    public string? Details { get; set; }
    public string? IpAddress { get; set; }

    // Row timestamps (house rule). Audit rows are append-only, so UpdatedAt always equals CreatedAt.
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
