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

    public void Add<T>(T entity) where T : class => _db.Add(entity);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
