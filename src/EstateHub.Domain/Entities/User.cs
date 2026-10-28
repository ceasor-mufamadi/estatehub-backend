namespace EstateHub.Domain.Entities;

/// <summary>
/// A person who can sign in. Platform-wide (one account across estates), so not tenant-scoped:
/// what they may do in an estate comes from their <see cref="Membership"/> there.
/// </summary>
public class User : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Lower-cased and trimmed (see <see cref="NormaliseEmail"/>). Unique when set; a resident invited by SMS may have none.</summary>
    public string? Email { get; set; }
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>E.164 South African mobile number (+27…), unique when set. Used for SMS codes.</summary>
    public string? Phone { get; set; }

    /// <summary>Null until the user sets a password (residents may never have one).</summary>
    public string? PasswordHash { get; set; }

    /// <summary>Platform operator. May run platform-level RPCs; never bypasses an estate's data (SECURITY.md layer 5).</summary>
    public bool IsSuperAdmin { get; set; }

    public string Status { get; set; } = UserStatus.Active;

    public bool MfaEnabled { get; set; }

    /// <summary>TOTP secret, encrypted with ASP.NET Data Protection (AUTH-18). Null when MFA isn't set up.</summary>
    public string? TotpSecretEncrypted { get; set; }

    /// <summary>The last 30-second step a TOTP code was accepted for, so a code can't be used twice.</summary>
    public long? TotpLastUsedStep { get; set; }

    /// <summary>Changes on password, MFA or role changes; sessions started before it are revoked (AUTH-11).</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Wrong passwords / MFA codes since the last success or lockout (AUTH-05).</summary>
    public int FailedSignInCount { get; set; }

    /// <summary>Lockouts in a row; each one lasts twice as long as the previous (15 min, 30 min …).</summary>
    public int LockoutCount { get; set; }
    public DateTime? LockoutEnd { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public static string NormaliseEmail(string email) => email.Trim().ToLowerInvariant();

    /// <summary>How the user is shown and addressed: email, else phone.</summary>
    public string Contact => Email ?? Phone ?? string.Empty;
}

public static class UserStatus
{
    public const string Active = "active";
    public const string Disabled = "disabled";
}
