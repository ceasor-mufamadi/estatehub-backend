using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace EstateHub.Infrastructure.Auth;

/// <summary>
/// The ES256 key tokens are signed with (AUTH-08). Loaded once from Jwt:SigningKeyPem.
/// In Development/Testing a missing key is replaced by a throwaway one (tokens die on restart);
/// anywhere else the app refuses to start without it.
/// </summary>
public sealed class SigningKeyStore : IDisposable
{
    private readonly ECDsa _ecdsa;

    public SigningKeyStore(IOptions<JwtSettings> settings, IHostEnvironment environment, ILogger<SigningKeyStore> logger)
    {
        _ecdsa = ECDsa.Create();
        var pem = settings.Value.SigningKeyPem;

        if (string.IsNullOrWhiteSpace(pem))
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
                throw new InvalidOperationException(
                    "Jwt:SigningKeyPem is not configured. Generate one with: openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256");

            logger.LogWarning("Jwt:SigningKeyPem not set: using a throwaway signing key (tokens stop working on restart).");
            _ecdsa.GenerateKey(ECCurve.NamedCurves.nistP256);
        }
        else
        {
            _ecdsa.ImportFromPem(pem);
            if (_ecdsa.KeySize != 256)
                throw new InvalidOperationException("Jwt:SigningKeyPem must be an EC P-256 key (ES256).");
        }

        var publicSpki = _ecdsa.ExportSubjectPublicKeyInfo();
        KeyId = Base64UrlEncoder.Encode(SHA256.HashData(publicSpki)[..16]);

        SigningKey = new ECDsaSecurityKey(_ecdsa) { KeyId = KeyId };
        SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.EcdsaSha256);

        var publicOnly = ECDsa.Create(_ecdsa.ExportParameters(includePrivateParameters: false));
        ValidationKey = new ECDsaSecurityKey(publicOnly) { KeyId = KeyId };
    }

    public string KeyId { get; }

    public ECDsaSecurityKey SigningKey { get; }

    public SigningCredentials SigningCredentials { get; }

    /// <summary>Public key only: used for validation and published in the JWKS.</summary>
    public ECDsaSecurityKey ValidationKey { get; }

    public void Dispose() => _ecdsa.Dispose();
}
