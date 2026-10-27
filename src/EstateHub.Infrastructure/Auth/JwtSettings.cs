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
    /// PEM-encoded EC P-256 private key ("-----BEGIN PRIVATE KEY-----"). Supply it through an
    /// environment variable or user secrets, never appsettings. Required outside Development/Testing.
    /// Rotation via a SigningKey table + SigningKeyRotationJob lands in Sprint 2.
    /// </summary>
    public string? SigningKeyPem { get; set; }
}
