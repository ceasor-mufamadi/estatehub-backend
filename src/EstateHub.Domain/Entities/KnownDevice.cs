namespace EstateHub.Domain.Entities;

/// <summary>
/// A browser this account has signed in from. Each browser carries a random device id in an HttpOnly
/// cookie (only its hash is stored); a sign-in from a browser without a known id is a new device and
/// gets the "New sign-in" email (SECURITY.md layer 3). Comparing browser names isn't enough: a private
/// window, or a thief using the same browser, would look identical.
/// </summary>
public class KnownDevice : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public string DeviceHash { get; set; } = string.Empty;

    /// <summary>"Chrome on macOS", for the email and the record.</summary>
    public string Label { get; set; } = string.Empty;
    public DateTime LastSeenAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// The "This wasn't me" link in a new sign-in email: single use, 7 days, stored hashed. Using it
/// secures the account (every device signed out, password cancelled, passkeys added since removed)
/// and alerts the operator.
/// </summary>
public class SignInReport : IHasTimestamps
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public Guid SessionId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public string Device { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
