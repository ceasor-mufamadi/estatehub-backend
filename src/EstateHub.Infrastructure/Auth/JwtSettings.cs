namespace EstateHub.Infrastructure.Auth;

/// <summary>Bound from the "Jwt" configuration section.</summary>
public sealed class JwtSettings
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "estatehub";
    public string Audience { get; set; } = "estatehub";

    /// <summary>Access token lifetime (AUTH-08: 15 minutes).</summary>
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>
    /// Optional, one-off: an existing PEM EC P-256 key to import as the FIRST signing key when the
    /// signing_keys table is empty. Normally leave it unset: the first key is generated, stored
    /// encrypted, and rotated by ⏰ SigningKeyRotationJob (SigningKeyRing).
    /// </summary>
    public string? SigningKeyPem { get; set; }
}
