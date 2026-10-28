namespace EstateHub.Domain.Entities;

/// <summary>
/// A passkey (WebAuthn credential) a staff member registered (AUTH-03): phishing-resistant sign-in that
/// counts as MFA. Only the public key is stored; the private key never leaves the person's device.
/// </summary>
public class UserPasskey : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public byte[] CredentialId { get; set; } = [];
    public byte[] PublicKey { get; set; } = [];

    /// <summary>What the person called it, e.g. "MacBook Touch ID".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Authenticator counter; a value going backwards suggests a cloned key (checked by the handler).</summary>
    public long SignCount { get; set; }
    public string Transports { get; set; } = string.Empty;
    public bool IsUserVerified { get; set; }
    public bool IsBackupEligible { get; set; }
    public bool IsBackedUp { get; set; }
    public byte[]? AttestationObject { get; set; }
    public byte[]? ClientDataJson { get; set; }
    public DateTime? LastUsedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A passkey ceremony in progress (register or sign in): the handler's state, kept server-side for
/// 5 minutes behind a random token the browser holds.
/// </summary>
public class PasskeyChallenge : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Set when registering (the signed-in user); null when signing in.</summary>
    public Guid? UserId { get; set; }
    public string Purpose { get; set; } = PasskeyPurpose.SignIn;
    public string TokenHash { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class PasskeyPurpose
{
    public const string Register = "register";
    public const string SignIn = "sign_in";
}
