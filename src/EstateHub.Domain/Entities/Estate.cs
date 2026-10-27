namespace EstateHub.Domain.Entities;

/// <summary>A scheme on the platform (the tenant). Not tenant-scoped itself.</summary>
public class Estate : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? OrganisationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SchemeType { get; set; } = SchemeTypes.SectionalTitle;
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
