using EstateHub.Application.Auth;
using EstateHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EstateHub.Tests.Infrastructure;

/// <summary>
/// The production directory calls a Postgres SECURITY DEFINER function; EF InMemory can't, so tests
/// read the same data directly. Same contract: one user's ACTIVE memberships, ordered by estate name.
/// </summary>
public sealed class InMemoryMembershipDirectory(EstateHubDbContext db) : IMembershipDirectory
{
    public async Task<IReadOnlyList<UserMembership>> ForUserAsync(Guid userId, CancellationToken ct = default) =>
        await db.Memberships.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.UserId == userId && m.IsActive)
            .Join(db.Estates, m => m.EstateId, e => e.Id, (m, e) => new UserMembership(e.Id, e.Name, m.Role))
            .OrderBy(m => m.EstateName)
            .ToListAsync(ct);
}

/// <summary>Breached-password check without the network: only the listed passwords count as breached.</summary>
public sealed class FakeBreachedPasswords : IBreachedPasswords
{
    public HashSet<string> Breached { get; } = ["password1234"];

    public Task<bool> IsBreachedAsync(string password, CancellationToken ct = default) =>
        Task.FromResult(Breached.Contains(password));
}

/// <summary>Stand-in for the estatehub_invitation_estate SQL function (EF InMemory can't run it).</summary>
public sealed class InMemoryInvitationDirectory(EstateHubDbContext db) : IInvitationDirectory
{
    public async Task<Guid?> EstateForTokenAsync(string tokenHash, CancellationToken ct = default) =>
        await db.Invitations.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.TokenHash == tokenHash).Select(i => (Guid?)i.EstateId).FirstOrDefaultAsync(ct);
}

/// <summary>Everything "sent" by email or SMS, newest last, so tests can read codes and links.</summary>
public sealed class Outbox : EstateHub.Application.Interfaces.IEmailSender, EstateHub.Application.Interfaces.ISmsSender
{
    private readonly List<(string To, string Text)> _messages = [];

    Task EstateHub.Application.Interfaces.IEmailSender.SendAsync(string to, string subject, string text, CancellationToken ct)
    {
        lock (_messages) _messages.Add((to, subject + "\n" + text));
        return Task.CompletedTask;
    }

    Task EstateHub.Application.Interfaces.ISmsSender.SendAsync(string toE164, string text, CancellationToken ct)
    {
        lock (_messages) _messages.Add((toE164, text));
        return Task.CompletedTask;
    }

    public int CountTo(string to)
    {
        lock (_messages) return _messages.Count(m => m.To == to);
    }

    public string LastTo(string to)
    {
        lock (_messages)
            return _messages.LastOrDefault(m => m.To == to).Text ?? throw new InvalidOperationException($"Nothing was sent to {to}.");
    }

    /// <summary>The 6-digit code in the latest message to <paramref name="to"/>.</summary>
    public string CodeFor(string to) => System.Text.RegularExpressions.Regex.Match(LastTo(to), @"\b(\d{6})\b").Groups[1].Value;

    /// <summary>The invitation token (after "#") in the latest message to <paramref name="to"/>.</summary>
    public string InvitationTokenFor(string to) => System.Text.RegularExpressions.Regex.Match(LastTo(to), @"/invite#([A-Za-z0-9_-]+)").Groups[1].Value;
}
