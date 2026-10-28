namespace EstateHub.Domain.Entities;

/// <summary>A scheme on the platform (the tenant). Not tenant-scoped itself.</summary>
public class Estate : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? OrganisationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SchemeType { get; set; } = SchemeTypes.SectionalTitle;

    /// <summary>Sectional title scheme number from the Deeds Office, e.g. "SS 123/2005".</summary>
    public string? SchemeNumber { get; set; }

    /// <summary>Community Schemes Ombud Service registration number.</summary>
    public string? CsosRegistrationNumber { get; set; }

    /// <summary>Street address, as it appears on statements.</summary>
    public string? Address { get; set; }
    /// <summary>POPIA: the estate's Information Officer, named in the residents' privacy notice.</summary>
    public string? InformationOfficerName { get; set; }
    public string? InformationOfficerEmail { get; set; }

    /// <summary>Record visitors' ID numbers at the gate (Sprint 6). Off by default; encrypted and masked when on.</summary>
    public bool VisitorIdCapture { get; set; }

    /// <summary>How long visitor records (and any ID numbers, photos) are kept: 30–365 days.</summary>
    public int VisitorRetentionDays { get; set; } = 90;

    public string TimeZone { get; set; } = "Africa/Johannesburg";
    public string Currency { get; set; } = "ZAR";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class SchemeTypes
{
    public const string SectionalTitle = "sectional_title";
    public const string Hoa = "hoa";
    public const string Rental = "rental";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { SectionalTitle, Hoa, Rental };
}
