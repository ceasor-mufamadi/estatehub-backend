using EstateHub.Application.Interfaces;
using EstateHub.Domain.Entities;
using EstateHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EstateHub.Infrastructure.Repositories;

// Memberships and invitations are tenant-scoped: the DbContext confines every query here to the
// active estate. Users aren't, so they are joined in by id.
public class MembersRepository : IMembersRepository
{
    private readonly EstateHubDbContext _db;

    public MembersRepository(EstateHubDbContext db)
    {
        _db = db;
    }

    // Joined into an anonymous pair (which EF can sort and translate), then wrapped in MemberRow.
    public async Task<IReadOnlyList<MemberRow>> ListMembersAsync(CancellationToken ct = default) =>
        (await _db.Memberships.Where(m => m.IsActive)
            .Join(_db.Users, m => m.UserId, u => u.Id, (m, u) => new { Membership = m, User = u })
            .OrderBy(r => r.User.DisplayName)
            .ToListAsync(ct))
        .Select(r => new MemberRow(r.Membership, r.User))
        .ToList();

    public async Task<MemberRow?> GetMemberAsync(Guid membershipId, CancellationToken ct = default) =>
        await _db.Memberships.Where(m => m.Id == membershipId && m.IsActive)
            .Join(_db.Users, m => m.UserId, u => u.Id, (m, u) => new { Membership = m, User = u })
            .Select(r => new MemberRow(r.Membership, r.User))
            .FirstOrDefaultAsync(ct);

    public Task<Membership?> FindMembershipAsync(Guid userId, CancellationToken ct = default) =>
        _db.Memberships.FirstOrDefaultAsync(m => m.UserId == userId, ct);

    public async Task<IReadOnlyList<Invitation>> ListPendingInvitationsAsync(DateTime now, CancellationToken ct = default) =>
        await _db.Invitations.Where(i => i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .OrderByDescending(i => i.LastSentAt)
            .ToListAsync(ct);

    public Task<Invitation?> GetInvitationAsync(Guid id, CancellationToken ct = default) =>
        _db.Invitations.FirstOrDefaultAsync(i => i.Id == id, ct);

    public Task<bool> HasPendingInvitationAsync(string? email, string? phone, DateTime now, CancellationToken ct = default) =>
        _db.Invitations.AnyAsync(i => i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now
                                      && ((email != null && i.Email == email) || (phone != null && i.Phone == phone)), ct);

    public Task<int> CountInvitationsSentSinceAsync(DateTime since, CancellationToken ct = default) =>
        _db.Invitations.CountAsync(i => i.LastSentAt >= since, ct);

    public async Task<IReadOnlyList<Invitation>> InvitationsForPersonAsync(Guid personId, CancellationToken ct = default) =>
        await _db.Invitations.Where(i => i.PersonId == personId).OrderBy(i => i.CreatedAt).ToListAsync(ct);

    public void Add(Invitation invitation) => _db.Invitations.Add(invitation);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
