using EstateHub.Application.Auth;
using EstateHub.Application.DTOs;
using EstateHub.Domain.Entities;

namespace EstateHub.Application.Services;

// Slice 2.4: support access (AUTH-13) and guard tablets (AUTH-14).
public partial class AuthService
{
    public static readonly TimeSpan SupportAccessLifetime = TimeSpan.FromMinutes(30);
    public const int MinSupportReasonLength = 10;
    public const int MaxPinAttempts = 5;
    public static readonly TimeSpan PinLockout = TimeSpan.FromMinutes(15);

    public const string TabletNotPaired = "This tablet isn't paired any more. Ask a manager to pair it again.";
    public const string PinFailed = "That PIN didn't work. After 5 wrong tries the PIN pauses for 15 minutes.";

    public async Task<SignedIn> StartSupportAccessAsync(
        Guid userId, Guid sessionId, Guid estateId, string reason, ClientInfo client, CancellationToken ct = default)
    {
        var (user, session) = await OwnSessionAsync(userId, sessionId, ct);
        if (!user.IsSuperAdmin)
            throw new ActionNotAllowedException("Only platform operators can open support access.");

        reason = (reason ?? string.Empty).Trim();
        if (reason.Length < MinSupportReasonLength)
            throw new ArgumentException($"Say why you need access (at least {MinSupportReasonLength} characters). The estate will see it.");
        if (reason.Length > 500)
            throw new ArgumentException("Keep the reason under 500 characters.");

        var estate = await _estates.GetAsync(estateId, ct) ?? throw new KeyNotFoundException("Estate not found.");

        session.ActiveEstateId = estateId;
        session.ImpersonationEstateId = estateId;
        session.ImpersonationReason = reason;
        session.ImpersonationEndsAt = Now + SupportAccessLifetime;
        await _repo.SaveChangesAsync(ct);
        _validator.Forget(session.Id);

        // In the estate's own trail, so its staff see it on the Activity page (AUTH-13).
        await AuditAsync(user.Id.ToString(), "SupportAccessStarted", user.Id.ToString(), client,
            $"until={Sast(session.ImpersonationEndsAt.Value):HH:mm} reason={reason}", ct, estateId);
        await _alerts.RaiseAsync("Support access started", $"{user.Contact} opened read-only support access to {estate.Name} for 30 minutes. Reason: {reason}", ct);

        return await IssueAsync(user, session, refreshToken: null, ct);
    }

    public async Task<SignedIn> EndSupportAccessAsync(Guid userId, Guid sessionId, ClientInfo client, CancellationToken ct = default)
    {
        var (user, session) = await OwnSessionAsync(userId, sessionId, ct);
        if (session.ImpersonationEstateId is { } estateId)
        {
            ClearSupportAccess(session);
            session.ActiveEstateId = null;
            await _repo.SaveChangesAsync(ct);
            _validator.Forget(session.Id);
            await AuditAsync(user.Id.ToString(), "SupportAccessEnded", user.Id.ToString(), client, null, ct, estateId);
        }

        return await IssueAsync(user, session, refreshToken: null, ct);
    }

    private static void ClearSupportAccess(AuthSession session)
    {
        session.ImpersonationEstateId = null;
        session.ImpersonationReason = null;
        session.ImpersonationEndsAt = null;
    }

    public async Task<GuardTablet> GetGuardTabletAsync(string deviceCredential, CancellationToken ct = default)
    {
        var device = await OpenTabletAsync(deviceCredential, ct);
        var estate = await _estates.GetAsync(device.EstateId, ct);
        device.LastSeenAt = Now;
        await _repo.SaveChangesAsync(ct);
        return new GuardTablet(estate?.Name ?? string.Empty, device.Label, await _repo.ListTabletGuardsAsync(ct));
    }

    public async Task<SignedIn> GuardSignInAsync(
        string deviceCredential, Guid guardUserId, string pin, ClientInfo client, CancellationToken ct = default)
    {
        var device = await OpenTabletAsync(deviceCredential, ct);
        var isGuardHere = (await _repo.ListTabletGuardsAsync(ct)).Any(g => g.UserId == guardUserId);
        var guardPin = isGuardHere ? await _repo.FindGuardPinAsync(guardUserId, ct) : null;
        var user = guardPin is null ? null : await _repo.GetUserAsync(guardUserId, ct);
        if (guardPin is null || user is not { Status: UserStatus.Active })
            throw new AuthFailedException(PinFailed);

        if (guardPin.LockoutEnd is { } until && until > Now)
            throw new AuthFailedException(PinFailed);

        if (!_passwords.Verify(guardPin.PinHash, pin ?? string.Empty, out _))
        {
            guardPin.FailedCount++;
            if (guardPin.FailedCount >= MaxPinAttempts)
            {
                guardPin.FailedCount = 0;
                guardPin.LockoutEnd = Now + PinLockout;
                await _alerts.RaiseAsync("Guard PIN locked", $"A guard's PIN was locked after {MaxPinAttempts} wrong tries on tablet \"{device.Label}\".", ct);
            }

            await _repo.SaveChangesAsync(ct);
            await AuditAsync(user.Id.ToString(), "GuardSignInFailed", user.Id.ToString(), client, $"device={device.Label}", ct);
            throw new AuthFailedException(PinFailed);
        }

        guardPin.FailedCount = 0;
        guardPin.LockoutEnd = null;
        device.LastSeenAt = Now;
        var signedIn = await StartSessionAsync(user, ["pin"], device.EstateId, client, ct, guardDeviceId: device.Id);
        await AuditAsync(user.Id.ToString(), "GuardSignedIn", user.Id.ToString(), client, $"device={device.Label}", ct);
        return signedIn;
    }

    public async Task SetGuardPinAsync(Guid userId, string pin, ClientInfo client, CancellationToken ct = default)
    {
        if (!(await _repo.ListTabletGuardsAsync(ct)).Any(g => g.UserId == userId))
            throw new ActionNotAllowedException("Only guards of this estate have a tablet PIN.");

        pin = (pin ?? string.Empty).Trim();
        if (pin.Length != 6 || !pin.All(char.IsAsciiDigit))
            throw new ArgumentException("Use exactly 6 digits.");
        if (IsGuessablePin(pin))
            throw new ArgumentException("That PIN is too easy to guess (repeated or consecutive digits). Choose another.");

        var existing = await _repo.FindGuardPinAsync(userId, ct);
        if (existing is null)
        {
            existing = new GuardPin { UserId = userId };
            _repo.Add(existing);
        }

        existing.PinHash = _passwords.Hash(pin);
        existing.FailedCount = 0;
        existing.LockoutEnd = null;
        await _repo.SaveChangesAsync(ct);
        await AuditAsync(userId.ToString(), "GuardPinSet", userId.ToString(), client, null, ct);
    }

    /// <summary>000000, 111111 … and runs like 123456 or 987654: the first PINs anyone tries.</summary>
    internal static bool IsGuessablePin(string pin)
    {
        if (pin.Distinct().Count() == 1)
            return true;
        var steps = pin.Zip(pin.Skip(1), (a, b) => b - a).Distinct().ToList();
        return steps is [1] or [-1];
    }

    /// <summary>The paired, not revoked tablet for a credential, inside its estate (RLS).</summary>
    private async Task<GuardDevice> OpenTabletAsync(string deviceCredential, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceCredential))
            throw new AuthFailedException(TabletNotPaired);

        var hash = AuthSecrets.Hash(deviceCredential);
        var estateId = await _devices.EstateForDeviceAsync(hash, ct) ?? throw new AuthFailedException(TabletNotPaired);
        _tenant.Use(estateId);

        var device = await _repo.FindGuardDeviceAsync(hash, ct);
        if (device is null || device.RevokedAt is not null)
            throw new AuthFailedException(TabletNotPaired);
        return device;
    }

    private static readonly TimeZoneInfo SouthAfrica = TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg");

    /// <summary>For messages people read: SAST, as everywhere in EstateHub.</summary>
    private static DateTime Sast(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), SouthAfrica);
}
