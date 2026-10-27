namespace EstateHub.Domain.Entities;

/// <summary>
/// Marks an entity that belongs to exactly one estate (the tenant). The DbContext filters every
/// query on these by the active estate, stamps EstateId on insert, refuses cross-estate writes,
/// and Postgres row-level security backs that up. See docs/tenancy-and-features.md.
/// </summary>
public interface ITenantScoped
{
    Guid EstateId { get; set; }
}
