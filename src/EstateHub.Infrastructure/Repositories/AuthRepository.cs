using EstateHub.Application.Auth;
using EstateHub.Domain.Entities;
using EstateHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EstateHub.Infrastructure.Repositories;

// Returns tracked entities: AuthService changes them and calls SaveChangesAsync once per step.
public class AuthRepository : IAuthRepository
{
    private readonly EstateHubDbContext _db;

    public AuthRepository(EstateHubDbContext db)
    {
        _db = db;
    }

    public Task<User?> FindUserByEmailAsync(string normalisedEmail, CancellationToken ct = default) =>
        _db.Users.FirstOrDefaultAsync(x => x.Email == normalisedEmail, ct);

    public Task<User?> FindUserByPhoneAsync(string e164, CancellationToken ct = default) =>
        _db.Users.FirstOrDefaultAsync(x => x.Phone == e164, ct);

    public Task<OtpChallenge?> FindOtpChallengeAsync(string tokenHash, CancellationToken ct = default) =>
        _db.OtpChallenges.FirstOrDefaultAsync(x => x.TokenHash == tokenHash, ct);

    public Task<int> CountOtpChallengesAsync(string identifierHash, DateTime since, CancellationToken ct = default) =>
        _db.OtpChallenges.CountAsync(x => x.IdentifierHash == identifierHash && x.CreatedAt >= since, ct);

    public Task<int> CountOtpChallengesFromIpAsync(string ipAddress, string? channel, DateTime since, CancellationToken ct = default) =>
        _db.OtpChallenges.CountAsync(x => x.IpAddress == ipAddress && x.CreatedAt >= since && (channel == null || x.Channel == channel), ct);

    public Task<Invitation?> FindInvitationAsync(string tokenHash, CancellationToken ct = default) =>
        _db.Invitations.FirstOrDefaultAsync(x => x.TokenHash == tokenHash, ct);

    public Task<Invitation?> GetInvitationAsync(Guid id, CancellationToken ct = default) =>
        _db.Invitations.FirstOrDefaultAsync(x => x.Id == id, ct);

    public Task<User?> GetUserAsync(Guid id, CancellationToken ct = default) =>
        _db.Users.FirstOrDefaultAsync(x => x.Id == id, ct);

    public Task<AuthSession?> GetSessionAsync(Guid id, CancellationToken ct = default) =>
        _db.AuthSessions.FirstOrDefaultAsync(x => x.Id == id, ct);

    public async Task<IReadOnlyList<AuthSession>> ListOpenSessionsAsync(Guid userId, CancellationToken ct = default) =>
        await _db.AuthSessions.Where(x => x.UserId == userId && x.RevokedAt == null).ToListAsync(ct);

    public async Task<IReadOnlyList<KnownDevice>> ListKnownDevicesAsync(Guid userId, CancellationToken ct = default) =>
        await _db.KnownDevices.Where(x => x.UserId == userId).ToListAsync(ct);

    public Task<SignInReport?> FindSignInReportAsync(string tokenHash, CancellationToken ct = default) =>
        _db.SignInReports.FirstOrDefaultAsync(x => x.TokenHash == tokenHash, ct);

    public async Task<IReadOnlyList<AuthSession>> ListOpenDeviceSessionsAsync(Guid deviceId, CancellationToken ct = default) =>
        await _db.AuthSessions.Where(x => x.GuardDeviceId == deviceId && x.RevokedAt == null).ToListAsync(ct);

    public Task<GuardDevice?> FindGuardDeviceAsync(string credentialHash, CancellationToken ct = default) =>
        _db.GuardDevices.FirstOrDefaultAsync(x => x.CredentialHash == credentialHash, ct);

    public Task<GuardPin?> FindGuardPinAsync(Guid userId, CancellationToken ct = default) =>
        _db.GuardPins.FirstOrDefaultAsync(x => x.UserId == userId, ct);

    // Memberships and PINs are tenant-scoped: this is the active estate's guards only.
    public async Task<IReadOnlyList<TabletGuard>> ListTabletGuardsAsync(CancellationToken ct = default) =>
        await _db.Memberships.Where(m => m.IsActive && m.Role == Roles.Guard)
            .Join(_db.Users, m => m.UserId, u => u.Id, (m, u) => u)
            .OrderBy(u => u.DisplayName)
            .Select(u => new TabletGuard(u.Id, u.DisplayName, _db.GuardPins.Any(p => p.UserId == u.Id)))
            .ToListAsync(ct);

    public Task<RefreshToken?> FindRefreshTokenAsync(string tokenHash, CancellationToken ct = default) =>
        _db.RefreshTokens.FirstOrDefaultAsync(x => x.TokenHash == tokenHash, ct);

    public Task<MfaChallenge?> FindChallengeAsync(string tokenHash, CancellationToken ct = default) =>
        _db.MfaChallenges.FirstOrDefaultAsync(x => x.TokenHash == tokenHash, ct);

    public async Task<IReadOnlyList<RecoveryCode>> ListUnusedRecoveryCodesAsync(Guid userId, CancellationToken ct = default) =>
        await _db.RecoveryCodes.Where(x => x.UserId == userId && x.UsedAt == null).ToListAsync(ct);

    public async Task DeleteRecoveryCodesAsync(Guid userId, CancellationToken ct = default) =>
        _db.RecoveryCodes.RemoveRange(await _db.RecoveryCodes.Where(x => x.UserId == userId).ToListAsync(ct));

    // Memberships are tenant-scoped: the filter and write guard confine this to the active estate.
    public async Task UpsertMembershipAsync(Guid userId, string role, CancellationToken ct = default)
    {
        var membership = await _db.Memberships.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (membership is null)
        {
            membership = new Membership { UserId = userId };
            _db.Memberships.Add(membership);
        }

        membership.Role = role;
        membership.IsActive = true;
        await _db.SaveChangesAsync(ct);
    }

    public Task<PasskeyChallenge?> FindPasskeyChallengeAsync(string tokenHash, CancellationToken ct = default) =>
        _db.PasskeyChallenges.FirstOrDefaultAsync(x => x.TokenHash == tokenHash, ct);

    public async Task<IReadOnlyList<UserPasskey>> ListPasskeysAsync(Guid userId, CancellationToken ct = default) =>
        await _db.UserPasskeys.Where(x => x.UserId == userId).OrderBy(x => x.CreatedAt).ToListAsync(ct);

    public void Remove(object entity) => _db.Remove(entity);

    public async Task LinkPersonAsync(Guid personId, Guid userId, CancellationToken ct = default)
    {
        var person = await _db.People.FirstOrDefaultAsync(p => p.Id == personId, ct);
        if (person is null)
            return;
        person.UserId = userId;
        await _db.SaveChangesAsync(ct);
    }

    public void Add<T>(T entity) where T : class => _db.Add(entity);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
