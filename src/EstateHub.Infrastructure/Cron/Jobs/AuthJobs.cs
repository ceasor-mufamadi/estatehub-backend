using EstateHub.Application.Cron;
using EstateHub.Infrastructure.Auth;
using EstateHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EstateHub.Infrastructure.Cron.Jobs;

/// <summary>⏰ Daily: rotates the token signing key every 30 days and retires the previous one (SigningKeyRotation).</summary>
[CronJob("SigningKeyRotationJob")]
public class SigningKeyRotationJob : ICronJob
{
    private readonly SigningKeyRotation _rotation;
    private readonly ILogger<SigningKeyRotationJob> _logger;

    public SigningKeyRotationJob(SigningKeyRotation rotation, ILogger<SigningKeyRotationJob> logger)
    {
        _rotation = rotation;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var (created, retired) = await _rotation.RotateAsync(immediately: false, cancellationToken);
        _logger.LogInformation("SigningKeyRotationJob: new key {Created}, retired {Retired}", created, retired);
    }
}

/// <summary>
/// ⏰ Daily: deletes sign-in leftovers nobody needs any more. Codes, MFA and passkey steps after 2 days (the
/// hourly caps only look back one hour); refresh tokens a day after they expired; sessions 90 days
/// after they ended (kept that long for the sessions list and investigations). The audit log keeps
/// the history either way.
/// </summary>
[CronJob("AuthCleanupJob")]
public class AuthCleanupJob : ICronJob
{
    public static readonly TimeSpan KeepChallenges = TimeSpan.FromDays(2);
    public static readonly TimeSpan KeepExpiredTokens = TimeSpan.FromDays(1);
    public static readonly TimeSpan KeepEndedSessions = TimeSpan.FromDays(90);
    public static readonly TimeSpan KeepSignInReports = TimeSpan.FromDays(30);
    public static readonly TimeSpan ForgetUnusedDevices = TimeSpan.FromDays(365);
    private const int Batch = 1000;

    private readonly EstateHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<AuthCleanupJob> _logger;

    public AuthCleanupJob(EstateHubDbContext db, TimeProvider clock, ILogger<AuthCleanupJob> logger)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        var otps = await DeleteAsync(_db.OtpChallenges.Where(c => c.CreatedAt < now - KeepChallenges), cancellationToken);
        var mfa = await DeleteAsync(_db.MfaChallenges.Where(c => c.CreatedAt < now - KeepChallenges), cancellationToken)
                  + await DeleteAsync(_db.PasskeyChallenges.Where(c => c.CreatedAt < now - KeepChallenges), cancellationToken);
        var tokens = await DeleteAsync(_db.RefreshTokens.Where(t => t.ExpiresAt < now - KeepExpiredTokens), cancellationToken);
        var sessions = await DeleteAsync(_db.AuthSessions.Where(s =>
            (s.RevokedAt != null && s.RevokedAt < now - KeepEndedSessions) || s.ExpiresAt < now - KeepEndedSessions), cancellationToken);

        // A browser unused for a year is "new" again (its next sign-in is emailed); old report links are dead anyway.
        await DeleteAsync(_db.SignInReports.Where(r => r.ExpiresAt < now - KeepSignInReports), cancellationToken);
        await DeleteAsync(_db.KnownDevices.Where(d => d.LastSeenAt < now - ForgetUnusedDevices), cancellationToken);

        _logger.LogInformation(
            "AuthCleanupJob: removed {Otps} codes, {Mfa} MFA/passkey steps, {Tokens} refresh tokens, {Sessions} sessions", otps, mfa, tokens, sessions);
    }

    // Load-and-remove in batches (works on every provider, keeps transactions small).
    private async Task<int> DeleteAsync<T>(IQueryable<T> query, CancellationToken ct) where T : class
    {
        var total = 0;
        while (true)
        {
            var batch = await query.Take(Batch).ToListAsync(ct);
            if (batch.Count == 0)
                return total;
            _db.RemoveRange(batch);
            await _db.SaveChangesAsync(ct);
            total += batch.Count;
        }
    }
}
