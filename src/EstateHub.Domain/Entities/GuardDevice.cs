namespace EstateHub.Domain.Entities;

/// <summary>
/// A tablet at the gate, paired to one estate by a manager (AUTH-14). It holds a long-lived device
/// credential (only its SHA-256 hash is stored, the tablet keeps it in an HttpOnly cookie). Guards
/// sign in on it with their own PIN, so every entry is tied to a person. Revoking it signs them all out.
/// </summary>
public class GuardDevice : ITenantScoped, IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EstateId { get; set; }
    public string Label { get; set; } = string.Empty;
    public string CredentialHash { get; set; } = string.Empty;
    public Guid PairedByUserId { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>A one-time code a manager creates to pair a tablet: 15 minutes, single use, stored hashed.</summary>
public class PairingCode : ITenantScoped, IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EstateId { get; set; }
    public string Label { get; set; } = string.Empty;
    public string CodeHash { get; set; } = string.Empty;
    public Guid CreatedByUserId { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public Guid? DeviceId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>A guard's personal 6-digit PIN for this estate's tablets, hashed like a password; 5 wrong tries pause it.</summary>
public class GuardPin : ITenantScoped, IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EstateId { get; set; }
    public Guid UserId { get; set; }
    public string PinHash { get; set; } = string.Empty;
    public int FailedCount { get; set; }
    public DateTime? LockoutEnd { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
