namespace EstateHub.Domain.Entities;

/// <summary>
/// An ES256 key access tokens are signed with (AUTH-08). The newest live key signs; every live key
/// validates and is published in the JWKS. Rotation adds a new key, and the previous one is retired
/// once tokens it signed can no longer be valid. The private key is encrypted with Data Protection (AUTH-18).
/// </summary>
public class SigningKey : IHasTimestamps
{
    /// <summary>The JWT "kid": derived from the public key.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>SubjectPublicKeyInfo, base64.</summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>PKCS#8 private key, base64, then encrypted.</summary>
    public string PrivateKeyEncrypted { get; set; } = string.Empty;

    /// <summary>Set when the key stops validating (and is removed from the JWKS).</summary>
    public DateTime? RetiredAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
