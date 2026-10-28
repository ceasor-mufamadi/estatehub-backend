using System.Security.Cryptography;
using System.Text;

namespace EstateHub.Application.Auth;

/// <summary>
/// Random tokens (refresh tokens, MFA challenges) and recovery codes. Only their SHA-256 hashes are
/// stored. A fast hash is enough because they are long and random (≥ 80 bits): unlike passwords,
/// there is nothing to guess.
/// </summary>
public static class AuthSecrets
{
    /// <summary>256-bit random token, base64url.</summary>
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>Ten codes like "k7m2-q9xd-4hpa-w3ne" (80 bits each), shown to the user once.</summary>
    public static IReadOnlyList<string> NewRecoveryCodes(int count = 10) =>
        Enumerable.Range(0, count)
            .Select(_ => Base32.Encode(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant())
            .Select(code => string.Join('-', code.Chunk(4).Select(chunk => new string(chunk))))
            .ToList();

    /// <summary>Recovery codes are compared without dashes, spaces or case, so typing them is forgiving.</summary>
    public static string NormaliseRecoveryCode(string code) =>
        new(code.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
