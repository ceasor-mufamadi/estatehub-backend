namespace EstateHub.Domain.Entities;

/// <summary>
/// Row-level audit columns. House rule: EVERY table has them (EntityConventionTests fails the
/// build otherwise), in addition to the AuditLog trail. The DbContext sets both on insert and
/// UpdatedAt on every update; code never assigns them by hand.
/// </summary>
/// <remarks>
/// Time zone convention (all timestamps, not just these): columns are Postgres <c>timestamptz</c>
/// (an absolute instant) and the database session time zone is Africa/Johannesburg, so anyone
/// querying the database sees SAST (e.g. <c>2026-10-26 14:30:00+02</c>), and the app shows SAST.
/// Inside C# the values are UTC-kind DateTimes because the Npgsql driver requires that for timestamptz;
/// convert for display only. SAST has no daylight saving, so nothing is ever ambiguous.
/// </remarks>
public interface IHasTimestamps
{
    DateTime CreatedAt { get; set; }
    DateTime UpdatedAt { get; set; }
}
