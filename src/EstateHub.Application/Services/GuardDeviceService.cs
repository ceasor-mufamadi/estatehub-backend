using System.Security.Cryptography;
using EstateHub.Application.Auth;
using EstateHub.Application.DTOs;
using EstateHub.Application.Interfaces;
using EstateHub.Application.Tenancy;
using EstateHub.Domain.Entities;

namespace EstateHub.Application.Services;

/// <summary>
/// Guard tablets (AUTH-14). A manager creates a one-time pairing code; typing it on the tablet swaps
/// it for the tablet's own credential. Revoking a tablet ends every guard session on it at once.
/// </summary>
public class GuardDeviceService : IGuardDeviceService
{
    public static readonly TimeSpan PairingCodeLifetime = TimeSpan.FromMinutes(15);

    private readonly IGuardDeviceRepository _devices;
    private readonly IAuthRepository _auth;
    private readonly IDeviceDirectory _directory;
    private readonly IEstateRepository _estates;
    private readonly ISessionValidator _validator;
    private readonly ICurrentUser _user;
    private readonly ITenantContext _tenant;
    private readonly IAuditLogService _audit;
    private readonly TimeProvider _clock;

    public GuardDeviceService(
        IGuardDeviceRepository devices,
        IAuthRepository auth,
        IDeviceDirectory directory,
        IEstateRepository estates,
        ISessionValidator validator,
        ICurrentUser user,
        ITenantContext tenant,
        IAuditLogService audit,
        TimeProvider clock)
    {
        _devices = devices;
        _auth = auth;
        _directory = directory;
        _estates = estates;
        _validator = validator;
        _user = user;
        _tenant = tenant;
        _audit = audit;
        _clock = clock;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public async Task<PairingCodeDto> CreatePairingCodeAsync(string label, CancellationToken ct = default)
    {
        label = (label ?? string.Empty).Trim();
        if (label.Length is 0 or > 100)
            throw new ArgumentException("Give the tablet a name (up to 100 characters), e.g. \"Main gate\".");

        // 8 characters from a 32-letter alphabet (40 bits), typed as XXXX-XXXX; single use, 15 minutes.
        var raw = Base32.Encode(RandomNumberGenerator.GetBytes(5));
        var code = $"{raw[..4]}-{raw[4..8]}";
        var pairing = new PairingCode
        {
            Label = label,
            CodeHash = AuthSecrets.Hash(NormaliseCode(code)),
            CreatedByUserId = Guid.Parse(_user.UserId!),
            ExpiresAt = Now + PairingCodeLifetime,
        };
        _devices.Add(pairing);
        await _devices.SaveChangesAsync(ct);

        await AuditAsync("PairingCode", pairing.Id, "Created", $"label={label}", _user.UserId!, ct);
        return new PairingCodeDto(code, label, pairing.ExpiresAt);
    }

    public async Task<IReadOnlyList<GuardDeviceDto>> ListAsync(CancellationToken ct = default) =>
        (await _devices.ListActiveAsync(ct)).Select(d => new GuardDeviceDto(d.Id, d.Label, d.CreatedAt, d.LastSeenAt)).ToList();

    public async Task RevokeAsync(Guid deviceId, CancellationToken ct = default)
    {
        var device = await _devices.GetAsync(deviceId, ct);
        if (device is null || device.RevokedAt is not null)
            throw new KeyNotFoundException("Tablet not found.");

        device.RevokedAt = Now;
        var sessions = await _auth.ListOpenDeviceSessionsAsync(device.Id, ct);
        foreach (var session in sessions)
        {
            session.RevokedAt = Now;
            session.RevokedReason = "device_revoked";
            _validator.Forget(session.Id);
        }

        await _devices.SaveChangesAsync(ct);
        await _auth.SaveChangesAsync(ct);
        await AuditAsync("GuardDevice", device.Id, "Revoked", $"label={device.Label} sessions={sessions.Count}", _user.UserId!, ct);
    }

    public async Task<PairedDevice> PairAsync(string code, CancellationToken ct = default)
    {
        const string invalid = "That pairing code isn't valid. Codes work once, for 15 minutes: ask a manager for a new one.";
        var hash = AuthSecrets.Hash(NormaliseCode(code ?? string.Empty));
        var estateId = await _directory.EstateForPairingCodeAsync(hash, ct) ?? throw new AuthFailedException(invalid);
        _tenant.Use(estateId);

        var pairing = await _devices.FindPairingCodeAsync(hash, ct);
        if (pairing is null || pairing.UsedAt is not null || pairing.ExpiresAt <= Now)
            throw new AuthFailedException(invalid);

        var credential = AuthSecrets.NewToken();
        var device = new GuardDevice
        {
            Label = pairing.Label,
            CredentialHash = AuthSecrets.Hash(credential),
            PairedByUserId = pairing.CreatedByUserId,
            LastSeenAt = Now,
        };
        _devices.Add(device);
        pairing.UsedAt = Now;
        pairing.DeviceId = device.Id;
        await _devices.SaveChangesAsync(ct);

        await AuditAsync("GuardDevice", device.Id, "Paired", $"label={device.Label}", "tablet", ct);
        var estate = await _estates.GetAsync(estateId, ct);
        return new PairedDevice(credential, estate?.Name ?? string.Empty, device.Label);
    }

    /// <summary>Codes are compared without dashes, spaces or case.</summary>
    private static string NormaliseCode(string code) => new(code.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private Task AuditAsync(string entityType, Guid id, string action, string details, string performedBy, CancellationToken ct) =>
        _audit.CreateAsync(new CreateAuditLogDto(entityType, id.ToString(), action, performedBy, details), ct);
}
