namespace EstateHub.Domain.Entities;

/// <summary>
/// A section (unit) of the scheme. Its participation quota (PQ) from the sectional plan decides its
/// share of the levies (PHASE1.md §4): stored as a percentage (e.g. 2.3456), all units totalling 100.
/// </summary>
public class Unit : ITenantScoped, IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EstateId { get; set; }
    public Guid? BlockId { get; set; }

    /// <summary>Section number on the sectional plan; unique within the estate.</summary>
    public int SectionNumber { get; set; }

    /// <summary>What residents call it, e.g. "A12" or "Unit 7". Optional.</summary>
    public string? DoorNumber { get; set; }

    /// <summary>Participation quota as a percentage (numeric(9,6)).</summary>
    public decimal ParticipationQuota { get; set; }

    public decimal? SizeSquareMetres { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
