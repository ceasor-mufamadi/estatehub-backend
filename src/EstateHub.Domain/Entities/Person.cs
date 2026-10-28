namespace EstateHub.Domain.Entities;

/// <summary>
/// Someone who owns or lives in a unit of this estate, whether or not they have an EstateHub account:
/// the estate's record (name and contact details). <see cref="UserId"/> links it to their account once
/// they accept an invitation. Personal information (POPIA): staff see it, residents see only their own.
/// </summary>
public class Person : ITenantScoped, IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EstateId { get; set; }
    public Guid? UserId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Lower-cased; unique within the estate when set.</summary>
    public string? Email { get; set; }

    /// <summary>E.164 SA mobile (+27…); unique within the estate when set.</summary>
    public string? Phone { get; set; }

    /// <summary>Notices (not essential messages: statements, arrears and security always go out) by email.</summary>
    public bool NoticesByEmail { get; set; } = true;

    /// <summary>Notices by SMS: off by default (SMS costs the estate money).</summary>
    public bool NoticesBySms { get; set; }

    /// <summary>The privacy notice version they acknowledged (POPIA), and when.</summary>
    public string? PrivacyNoticeVersion { get; set; }
    public DateTime? PrivacyAcknowledgedAt { get; set; }

    /// <summary>Set when a former resident's personal details were removed (POPIA); history stays, under a placeholder name.</summary>
    public DateTime? AnonymisedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A person owns (a share of) a unit from a date, until a date when sold. Ownerships are ended, not
/// deleted, so the history stays (the owner is the debtor for levies, PHASE1.md §4). Removing is only
/// for data-entry mistakes, and audited.
/// </summary>
public class Ownership : ITenantScoped, IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EstateId { get; set; }
    public Guid UnitId { get; set; }
    public Guid PersonId { get; set; }

    /// <summary>Share of the unit in percent (co-owners); the current owners of a unit total at most 100.</summary>
    public decimal SharePercent { get; set; } = 100m;

    public DateOnly FromDate { get; set; }
    public DateOnly? ToDate { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Who lives in a unit, from a date until a date: the owner themselves, a tenant, or someone else (family).</summary>
public class Occupancy : ITenantScoped, IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EstateId { get; set; }
    public Guid UnitId { get; set; }
    public Guid PersonId { get; set; }
    public string Kind { get; set; } = OccupancyKinds.Tenant;

    public DateOnly FromDate { get; set; }
    public DateOnly? ToDate { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class OccupancyKinds
{
    public const string OwnerOccupier = "owner_occupier";
    public const string Tenant = "tenant";
    public const string Other = "other";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { OwnerOccupier, Tenant, Other };
}
