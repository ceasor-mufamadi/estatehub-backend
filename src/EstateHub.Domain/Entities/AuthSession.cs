namespace EstateHub.Domain.Entities;

/// <summary>
/// One signed-in device (AUTH-11). Every access token carries its id ("sid"); revoking the session
/// stops its tokens within ~30 s and its refresh token at once.
/// </summary>
public class AuthSession : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    /// <summary>
    /// The estate the session currently works in (SwitchEstate changes it); null = platform level.
    /// Not called EstateId on purpose: a session belongs to the user, not to an estate.
    /// </summary>
    public Guid? ActiveEstateId { get; set; }

    /// <summary>How the user signed in, space-separated "amr" values (e.g. "pwd mfa").</summary>
    public string AuthMethods { get; set; } = string.Empty;

    /// <summary>When the user last actively authenticated (drives step-up MFA, AUTH-12).</summary>
    public DateTime AuthTime { get; set; }

    /// <summary>Staff sessions end 12 h after sign-in; others slide 30 days from the last refresh (AUTH-09).</summary>
    public bool IsStaff { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime LastSeenAt { get; set; }

    /// <summary>User.SecurityStamp at sign-in. A later change to the user's stamp ends this session.</summary>
    public string SecurityStamp { get; set; } = string.Empty;

    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }

    /// <summary>
    /// Support access (AUTH-13): a super admin looking at one estate read-only, until
    /// <see cref="ImpersonationEndsAt"/> (30 minutes). The reason is recorded in the estate's audit log.
    /// </summary>
    public Guid? ImpersonationEstateId { get; set; }
    public string? ImpersonationReason { get; set; }
    public DateTime? ImpersonationEndsAt { get; set; }

    /// <summary>Set for a guard's shift session on a paired tablet (AUTH-14); revoking the tablet ends it.</summary>
    public Guid? GuardDeviceId { get; set; }

    public DateTime? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A refresh token (AUTH-09): random 256-bit, only its SHA-256 hash is stored, single use.
/// Each use replaces it with a new one in the same session; presenting a replaced one again
/// (after a short grace period) means it was stolen, and the whole session is revoked.
/// </summary>
public class RefreshToken : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SessionId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }

    /// <summary>Set when it was exchanged for a new token.</summary>
    public DateTime? UsedAt { get; set; }
    public Guid? ReplacedById { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Ten single-use MFA recovery codes per user, stored hashed (AUTH-03).</summary>
public class RecoveryCode : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public string CodeHash { get; set; } = string.Empty;
    public DateTime? UsedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// The half-way point of a password sign-in: the password was right, the second factor is still
/// to come (or to be set up). Identified by a random token the browser holds for 5 minutes.
/// </summary>
public class MfaChallenge : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Set up a new authenticator first (staff without MFA yet) rather than verify one.</summary>
    public bool IsEnrolment { get; set; }

    /// <summary>How the first step was passed: "pwd" (password) or "otp" (emailed/texted code).</summary>
    public string FirstFactor { get; set; } = "pwd";

    /// <summary>Estate to land in once MFA is done (accepting an invitation); null = the usual choice.</summary>
    public Guid? LandInEstateId { get; set; }

    /// <summary>The not-yet-confirmed TOTP secret during enrolment, encrypted.</summary>
    public string? PendingTotpSecretEncrypted { get; set; }

    public int Attempts { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
    public string? IpAddress { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
