namespace EstateHub.Application.Imports;

/// <summary>The template's columns. Headers are matched by name (case, spaces and symbols ignored), so order doesn't matter.</summary>
public static class ImportColumns
{
    public const string Section = "section";
    public const string Door = "door";
    public const string Block = "block";
    public const string Quota = "pq";
    public const string Size = "size";
    public const string OwnerName = "owner_name";
    public const string OwnerEmail = "owner_email";
    public const string OwnerMobile = "owner_mobile";
    public const string OwnerShare = "owner_share";
    public const string OwnerSince = "owner_since";
    public const string OwnerLivesHere = "owner_lives_here";
    public const string TenantName = "tenant_name";
    public const string TenantEmail = "tenant_email";
    public const string TenantMobile = "tenant_mobile";
    public const string TenantSince = "tenant_since";

    /// <summary>Key, header as the template writes it, and what it means (the template's "Read me").</summary>
    public static readonly IReadOnlyList<(string Key, string Header, string Help)> All =
    [
        (Section, "Section", "Required. Section number on the sectional plan (1–99999). New to the estate: the import only adds units."),
        (Door, "Door", "Optional. What residents call it, e.g. A12 (up to 20 characters)."),
        (Block, "Block", "Optional. Block name; created if the estate doesn't have it yet."),
        (Quota, "PQ %", "Required on the unit's first row. Participation quota as a percentage, up to 4 decimals (e.g. 2.3456). Fractions totalling 1 also work."),
        (Size, "Size m²", "Optional. Floor area in square metres."),
        (OwnerName, "Owner name", "The owner's full name. One row per owner: repeat the section number for co-owners."),
        (OwnerEmail, "Owner email", "Optional. Used for the portal invitation; an existing person with this email is reused."),
        (OwnerMobile, "Owner mobile", "Optional. South African mobile, e.g. 082 123 4567."),
        (OwnerShare, "Owner share %", "Optional. Blank = 100 for a single owner; co-owners must give shares totalling at most 100."),
        (OwnerSince, "Owner since", "Optional. Date, e.g. 2020-01-31. Blank = today. For billing, give the real date: an owner is billed from the first billing day on or after it."),
        (OwnerLivesHere, "Owner lives here", "Optional. yes or no (blank = no). A person lives in one unit only."),
        (TenantName, "Tenant name", "Optional. The tenant renting this unit."),
        (TenantEmail, "Tenant email", "Optional."),
        (TenantMobile, "Tenant mobile", "Optional."),
        (TenantSince, "Tenant since", "Optional. Date the lease started. Blank = today."),
    ];

    private static readonly Dictionary<string, string> Aliases = BuildAliases();

    /// <summary>The column key for a header cell, or null if it isn't one of ours (ignored).</summary>
    public static string? Match(string header) => Aliases.GetValueOrDefault(Normalise(header));

    public static string Label(string key) => All.FirstOrDefault(c => c.Key == key).Header ?? key;

    private static string Normalise(string header) =>
        new(header.Trim().ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());

    private static Dictionary<string, string> BuildAliases()
    {
        var aliases = All.ToDictionary(c => Normalise(c.Header), c => c.Key);
        void Add(string key, params string[] names)
        {
            foreach (var name in names)
                aliases[Normalise(name)] = key;
        }

        Add(Section, "Section number", "Section no", "Unit");
        Add(Door, "Door number", "Door no", "Unit number");
        Add(Quota, "PQ", "Participation quota", "Quota");
        Add(Size, "Size", "Size m2", "Size (m²)");
        Add(OwnerMobile, "Owner phone", "Owner cell");
        Add(OwnerShare, "Owner share", "Share", "Share %");
        Add(TenantMobile, "Tenant phone", "Tenant cell");
        return aliases;
    }
}
