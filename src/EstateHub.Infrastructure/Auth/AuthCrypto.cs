using System.Security.Cryptography;
using System.Text;
using EstateHub.Application.Auth;
using EstateHub.Domain.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace EstateHub.Infrastructure.Auth;

/// <summary>ASP.NET Core Identity's password hasher (PBKDF2-HMAC-SHA512, salted, versioned format).</summary>
public sealed class PasswordHashing : IPasswordHashing
{
    private static readonly User Nobody = new();
    private readonly PasswordHasher<User> _hasher = new();

    public string Hash(string password) => _hasher.HashPassword(Nobody, password);

    public bool Verify(string hash, string password, out bool rehash)
    {
        var result = _hasher.VerifyHashedPassword(Nobody, hash, password);
        rehash = result == PasswordVerificationResult.SuccessRehashNeeded;
        return result != PasswordVerificationResult.Failed;
    }
}

/// <summary>Data Protection with a purpose string per use, keys in the key ring outside the database (AUTH-18).</summary>
public sealed class DataProtectionSecretProtector : ISecretProtector
{
    private readonly IDataProtector _protector;

    public DataProtectionSecretProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("EstateHub.Auth.Secrets.v1");
    }

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}

/// <summary>
/// Have I Been Pwned "range" API: sends the first 5 characters of the password's SHA-1 and checks the
/// returned suffixes locally, so neither the password nor its full hash leaves the server. Padding hides
/// the response size. If the service can't be reached the password is allowed (logged): HIBP being down
/// must not stop anyone setting a password.
/// </summary>
public sealed class PwnedPasswordsChecker : IBreachedPasswords
{
    public const string HttpClientName = "pwned-passwords";

    private readonly IHttpClientFactory _http;
    private readonly ILogger<PwnedPasswordsChecker> _logger;

    public PwnedPasswordsChecker(IHttpClientFactory http, ILogger<PwnedPasswordsChecker> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<bool> IsBreachedAsync(string password, CancellationToken ct = default)
    {
        var sha1 = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password)));
        var (prefix, suffix) = (sha1[..5], sha1[5..]);

        try
        {
            var client = _http.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"range/{prefix}");
            request.Headers.Add("Add-Padding", "true");
            using var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);
            foreach (var line in body.Split('\n'))
            {
                var parts = line.Trim().Split(':');
                if (parts.Length == 2 && parts[0].Equals(suffix, StringComparison.OrdinalIgnoreCase) && parts[1] != "0")
                    return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Breached-password check unavailable; allowing the password.");
            return false;
        }
    }
}
