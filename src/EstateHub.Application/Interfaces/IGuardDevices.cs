namespace EstateHub.Application.Interfaces;

/// <summary>Guard tablets of the active estate (AUTH-14): pairing, the list, revoking.</summary>
public interface IGuardDeviceService
{
    /// <summary>A one-time code (15 minutes) to type on the tablet. Shown once.</summary>
    Task<PairingCodeDto> CreatePairingCodeAsync(string label, CancellationToken ct = default);

    Task<IReadOnlyList<GuardDeviceDto>> ListAsync(CancellationToken ct = default);

    /// <summary>The tablet stops working and every guard signed in on it is signed out.</summary>
    Task RevokeAsync(Guid deviceId, CancellationToken ct = default);

    /// <summary>On the tablet, anonymous: swaps a pairing code for the tablet's credential (it goes in an HttpOnly cookie).</summary>
    Task<PairedDevice> PairAsync(string code, CancellationToken ct = default);
}

public record PairingCodeDto(string Code, string Label, DateTime ExpiresAt);

public record GuardDeviceDto(Guid Id, string Label, DateTime PairedAt, DateTime LastSeenAt);

public record PairedDevice(string Credential, string EstateName, string Label);

/// <summary>Tablets and pairing codes of the ACTIVE estate (tenant-scoped).</summary>
public interface IGuardDeviceRepository
{
    Task<IReadOnlyList<EstateHub.Domain.Entities.GuardDevice>> ListActiveAsync(CancellationToken ct = default);
    Task<EstateHub.Domain.Entities.GuardDevice?> GetAsync(Guid id, CancellationToken ct = default);
    Task<EstateHub.Domain.Entities.PairingCode?> FindPairingCodeAsync(string codeHash, CancellationToken ct = default);
    void Add(object entity);
    Task SaveChangesAsync(CancellationToken ct = default);
}
