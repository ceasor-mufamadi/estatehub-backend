namespace EstateHub.Domain.Entities;

/// <summary>
/// Per-estate override of a feature's default (see FeatureCatalog). Only super admin writes these,
/// so it is keyed by EstateId but deliberately not ITenantScoped: platform calls manage it across estates.
/// </summary>
public class EstateFeature : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EstateId { get; set; }
    public string FeatureKey { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string? SettingsJson { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
