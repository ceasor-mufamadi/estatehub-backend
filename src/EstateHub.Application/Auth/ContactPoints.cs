using EstateHub.Domain.Entities;

namespace EstateHub.Application.Auth;

/// <summary>Email addresses and South African mobile numbers as EstateHub stores and compares them.</summary>
public static class ContactPoints
{
    public static bool LooksLikeEmail(string value) => value.Contains('@');

    /// <summary>Lower-cased, trimmed, and roughly valid (one @, a dot in the domain), or an ArgumentException.</summary>
    public static string Email(string value)
    {
        var email = User.NormaliseEmail(value);
        var at = email.IndexOf('@');
        if (email.Length > 254 || at < 1 || at != email.LastIndexOf('@') || !email[(at + 1)..].Contains('.') || email.Any(char.IsWhiteSpace))
            throw new ArgumentException("Enter a valid email address.");
        return email;
    }

    /// <summary>
    /// "082 123 4567", "+27 82 123 4567", "27821234567" → "+27821234567". Only SA mobile numbers
    /// (AUTH-06: SMS to SA numbers only), so SMS can't be pumped to premium foreign numbers.
    /// </summary>
    public static string SaMobile(string value)
    {
        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        if (value.TrimStart().StartsWith('+') && !digits.StartsWith("27"))
            throw new ArgumentException("Use a South African mobile number.");

        var local = digits switch
        {
            { Length: 10 } when digits[0] == '0' => digits[1..],
            { Length: 11 } when digits.StartsWith("27") => digits[2..],
            _ => null,
        };

        // SA mobile ranges start with 6, 7 or 8 after the leading 0.
        if (local is null || local[0] is not ('6' or '7' or '8'))
            throw new ArgumentException("Use a South African mobile number, e.g. 082 123 4567.");
        return "+27" + local;
    }

    /// <summary>"c****r@gmail.com" / "+27 ** *** 4567": enough to recognise, not enough to harvest.</summary>
    public static string Mask(string value)
    {
        if (LooksLikeEmail(value))
        {
            var at = value.IndexOf('@');
            var name = value[..at];
            var shown = name.Length <= 2 ? name[..1] + "*" : name[0] + new string('*', Math.Min(name.Length - 2, 6)) + name[^1];
            return shown + value[at..];
        }

        return value.Length >= 4 ? $"+27 ** *** {value[^4..]}" : "****";
    }
}

/// <summary>
/// Who may give which role (SECURITY.md layer 4): managers invite residents, guards and contractors;
/// trustee and manager roles come from the managing agent or the platform; nobody grants above their
/// own role, nobody changes their own, and nobody is ever made super admin from an estate.
/// </summary>
public static class RoleGrants
{
    private static readonly string[] Residents = [Roles.Owner, Roles.Tenant, Roles.Guard, Roles.Contractor];

    private static readonly Dictionary<string, string[]> Map = new(StringComparer.Ordinal)
    {
        [Roles.SuperAdmin] = [Roles.ManagingAgent, Roles.Trustee, Roles.Manager],
        [Roles.ManagingAgent] = [Roles.Trustee, Roles.Manager, .. Residents],
        [Roles.Manager] = Residents,
    };

    public static IReadOnlyList<string> GrantableBy(string? actorRole) =>
        actorRole is not null && Map.TryGetValue(actorRole, out var roles) ? roles : [];

    public static bool CanGrant(string? actorRole, string role) => GrantableBy(actorRole).Contains(role);
}
