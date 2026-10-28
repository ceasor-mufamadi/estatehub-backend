namespace EstateHub.Domain.Entities;

/// <summary>
/// An invitation to join an estate with a role (AUTH-01): single use, 7 days, bound to the email or
/// phone it was sent to. Accepting proves control of that address with a one-time code, so a
/// forwarded link alone is useless. Only the SHA-256 hash of the link token is stored.
/// </summary>
public class Invitation : ITenantScoped, IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EstateId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string Role { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }

    public Guid InvitedByUserId { get; set; }
    public int SendCount { get; set; }
    public DateTime LastSentAt { get; set; }

    public DateTime? AcceptedAt { get; set; }
    public Guid? AcceptedByUserId { get; set; }
    public DateTime? RevokedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A one-time code sent by email or SMS (AUTH-04): 6 digits, stored hashed, 10 minutes, 5 attempts,
/// single use. Also created for unknown addresses (nothing is sent), so responses never reveal
/// whether an account exists (AUTH-07), and so the hourly caps count every request (AUTH-06).
/// </summary>
public class OtpChallenge : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Purpose { get; set; } = OtpPurpose.SignIn;
    public string Channel { get; set; } = OtpChannel.Email;

    /// <summary>SHA-256 of the normalised email/phone: counts requests per address without storing it.</summary>
    public string IdentifierHash { get; set; } = string.Empty;

    /// <summary>Null when nobody has this address (the code is never sent).</summary>
    public Guid? UserId { get; set; }

    /// <summary>Set for invitation codes: which invitation, in which estate (so it can be loaded under RLS).</summary>
    public Guid? InvitationId { get; set; }
    public Guid? InvitationEstateId { get; set; }

    /// <summary>The browser's handle for this code step (SHA-256 only).</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>SHA-256 of challenge id + code (the id salts it).</summary>
    public string CodeHash { get; set; } = string.Empty;

    public int Attempts { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
    public string? IpAddress { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class OtpPurpose
{
    public const string SignIn = "sign_in";
    public const string Invitation = "invitation";
}

public static class OtpChannel
{
    public const string Email = "email";
    public const string Sms = "sms";
}
