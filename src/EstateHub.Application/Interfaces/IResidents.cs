using EstateHub.Application.Auth;
using EstateHub.Application.Privacy;

namespace EstateHub.Application.Interfaces;

/// <summary>
/// The resident portal (slice 3.4): the caller's own details in the active estate, the privacy notice,
/// contact preferences, changing their email or mobile, and downloading their own data.
/// </summary>
public interface IResidentService
{
    Task<MyDetailsDto> GetMyDetailsAsync(CancellationToken ct = default);
    Task<MyDetailsDto> AcknowledgePrivacyNoticeAsync(string version, CancellationToken ct = default);
    Task<MyDetailsDto> UpdatePreferencesAsync(bool noticesByEmail, bool noticesBySms, CancellationToken ct = default);
    Task<OtpStep> StartContactChangeAsync(string newContact, ClientInfo client, CancellationToken ct = default);
    Task<MyDetailsDto> ConfirmContactChangeAsync(string challengeToken, string code, ClientInfo client, CancellationToken ct = default);
    Task<PersonExportDto> ExportMyDataAsync(CancellationToken ct = default);
}

/// <summary>POPIA tools for staff: everything held about a person, and anonymising a former resident.</summary>
public interface IPersonDataService
{
    Task<PersonExportDto> ExportAsync(Guid personId, CancellationToken ct = default);

    /// <summary>
    /// Only for someone with no current or future ownership or occupancy; <paramref name="confirmName"/>
    /// must match their name. Can't be undone.
    /// </summary>
    Task AnonymiseAsync(Guid personId, string confirmName, CancellationToken ct = default);
}

/// <summary>
/// <paramref name="HasRecord"/> false: signed in here without a person record (e.g. staff), so there is
/// nothing to acknowledge or edit.
/// </summary>
public record MyDetailsDto(
    bool HasRecord, string Name, string? Email, string? Phone, bool NoticesByEmail, bool NoticesBySms,
    string NoticeVersion, bool NoticeAcknowledged, IReadOnlyList<PrivacyNotice.Section> Notice);

public record PersonExportDto(string FileName, byte[] Json);
