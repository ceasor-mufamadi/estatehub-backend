using System.Security.Cryptography;
using EstateHub.Application.Auth;
using EstateHub.Domain.Entities;
using EstateHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace EstateHub.Infrastructure.Auth;

/// <summary>
/// The live signing keys (AUTH-08), read from the signing_keys table and kept in memory. The newest
/// signs; all of them validate. Loaded on first use (creating the first key if there is none) and
/// re-read every few minutes, so a rotation by the jobs container reaches the API on its own.
/// </summary>
public sealed class SigningKeyRing
{
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly ISecretProtector _secrets;
    private readonly JwtSettings _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger<SigningKeyRing> _logger;
    private readonly object _loadLock = new();
    private volatile Snapshot? _current;
    private int _refreshing;

    public SigningKeyRing(
        IServiceScopeFactory scopes, ISecretProtector secrets, IOptions<JwtSettings> settings, TimeProvider clock, ILogger<SigningKeyRing> logger)
    {
        _scopes = scopes;
        _secrets = secrets;
        _settings = settings.Value;
        _clock = clock;
        _logger = logger;
    }

    public SigningCredentials SigningCredentials => Current.Signing;

    public IReadOnlyList<ECDsaSecurityKey> ValidationKeys => Current.Validation;

    private Snapshot Current
    {
        get
        {
            var snapshot = _current;
            if (snapshot is null)
            {
                // First use: load synchronously (once), creating the first key if needed.
                lock (_loadLock)
                    snapshot = _current ??= LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            else if (_clock.GetUtcNow() - snapshot.LoadedAt > RefreshEvery && Interlocked.Exchange(ref _refreshing, 1) == 0)
            {
                _ = Task.Run(RefreshInBackgroundAsync);
            }

            return snapshot;
        }
    }

    /// <summary>Re-read now (after a rotation in this process, e.g. the incident command).</summary>
    public async Task ReloadAsync(CancellationToken ct = default) => _current = await LoadAsync(ct);

    private async Task RefreshInBackgroundAsync()
    {
        try
        {
            _current = await LoadAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Keep the keys we have; try again at the next refresh.
            _logger.LogError(ex, "Reloading signing keys failed");
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private async Task<Snapshot> LoadAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EstateHubDbContext>();

        var keys = await LiveKeysAsync(db, ct);
        if (keys.Count == 0)
        {
            db.SigningKeys.Add(CreateKey(_settings.SigningKeyPem));
            await db.SaveChangesAsync(ct);
            keys = await LiveKeysAsync(db, ct);
            _logger.LogInformation("Created the first token signing key");
        }

        var validation = keys.Select(k => PublicKey(k)).ToList();
        return new Snapshot(Signing(keys[0]), validation, _clock.GetUtcNow());
    }

    private static async Task<List<SigningKey>> LiveKeysAsync(EstateHubDbContext db, CancellationToken ct) =>
        await db.SigningKeys.AsNoTracking().Where(k => k.RetiredAt == null).OrderByDescending(k => k.CreatedAt).ToListAsync(ct);

    /// <summary>A new P-256 key, from <paramref name="pem"/> when given (one-off import), otherwise freshly generated.</summary>
    public SigningKey CreateKey(string? pem = null)
    {
        using var ecdsa = ECDsa.Create();
        if (string.IsNullOrWhiteSpace(pem))
            ecdsa.GenerateKey(ECCurve.NamedCurves.nistP256);
        else
            ecdsa.ImportFromPem(pem);
        if (ecdsa.KeySize != 256)
            throw new InvalidOperationException("Signing keys must be EC P-256 (ES256).");

        var spki = ecdsa.ExportSubjectPublicKeyInfo();
        return new SigningKey
        {
            Id = Base64UrlEncoder.Encode(SHA256.HashData(spki)[..16]),
            PublicKey = Convert.ToBase64String(spki),
            PrivateKeyEncrypted = _secrets.Protect(Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey())),
        };
    }

    private SigningCredentials Signing(SigningKey key)
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(_secrets.Unprotect(key.PrivateKeyEncrypted)), out _);
        return new SigningCredentials(new ECDsaSecurityKey(ecdsa) { KeyId = key.Id }, SecurityAlgorithms.EcdsaSha256);
    }

    private static ECDsaSecurityKey PublicKey(SigningKey key)
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key.PublicKey), out _);
        return new ECDsaSecurityKey(ecdsa) { KeyId = key.Id };
    }

    private sealed record Snapshot(SigningCredentials Signing, IReadOnlyList<ECDsaSecurityKey> Validation, DateTimeOffset LoadedAt);
}

/// <summary>
/// Key rotation rules, used by the ⏰ SigningKeyRotationJob and the rotate-signing-keys command.
/// Routine: a new key every 30 days; the previous one is retired once the new one has been around
/// for an hour (the API picks it up within 5 minutes; tokens live 15). Incident: new key, all others
/// retired at once, so every access token stops working (SECURITY.md §7).
/// </summary>
public sealed class SigningKeyRotation
{
    public static readonly TimeSpan RotateEvery = TimeSpan.FromDays(30);
    public static readonly TimeSpan Overlap = TimeSpan.FromHours(1);

    private readonly EstateHubDbContext _db;
    private readonly SigningKeyRing _ring;
    private readonly TimeProvider _clock;

    public SigningKeyRotation(EstateHubDbContext db, SigningKeyRing ring, TimeProvider clock)
    {
        _db = db;
        _ring = ring;
        _clock = clock;
    }

    /// <returns>What changed, for the job log.</returns>
    public async Task<(bool Created, int Retired)> RotateAsync(bool immediately, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var live = await _db.SigningKeys.Where(k => k.RetiredAt == null).OrderByDescending(k => k.CreatedAt).ToListAsync(ct);

        var created = false;
        var newest = live.FirstOrDefault();
        if (immediately || newest is null || now - newest.CreatedAt >= RotateEvery)
        {
            newest = _ring.CreateKey();
            _db.SigningKeys.Add(newest);
            created = true;
            await _db.SaveChangesAsync(ct); // stamps CreatedAt
        }

        var retired = 0;
        if (immediately || now - newest.CreatedAt >= Overlap)
        {
            foreach (var old in live.Where(k => k.Id != newest.Id))
            {
                old.RetiredAt = now;
                retired++;
            }
        }

        await _db.SaveChangesAsync(ct);
        return (created, retired);
    }
}
