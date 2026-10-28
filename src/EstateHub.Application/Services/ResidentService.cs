using EstateHub.Application.Auth;
using EstateHub.Application.DTOs;
using EstateHub.Application.Interfaces;
using EstateHub.Application.Privacy;
using EstateHub.Application.Tenancy;
using EstateHub.Domain.Entities;

namespace EstateHub.Application.Services;

/// <summary>
/// The resident portal (slice 3.4, PHASE1.md "Slice 3.4 design"): the caller's own person record in the
/// active estate. A user has at most one person record per estate in practice; changes apply to all of
/// theirs here. Changing contact details and downloading data need a recent sign-in (RequiresRecentSignIn).
/// </summary>
public class ResidentService : IResidentService
{
    private readonly IPeopleRepository _people;
    private readonly IEstateRepository _estates;
    private readonly IAuthService _auth;
    private readonly IPersonDataService _data;
    private readonly ICurrentUser _user;
    private readonly ITenantContext _tenant;
    private readonly IAuditLogService _audit;
    private readonly TimeProvider _clock;

    public ResidentService(
        IPeopleRepository people,
        IEstateRepository estates,
        IAuthService auth,
        IPersonDataService data,
        ICurrentUser user,
        ITenantContext tenant,
        IAuditLogService audit,
        TimeProvider clock)
    {
        _people = people;
        _estates = estates;
        _auth = auth;
        _data = data;
        _user = user;
        _tenant = tenant;
        _audit = audit;
        _clock = clock;
    }

    private Guid UserId => Guid.Parse(_user.UserId!);

    public async Task<MyDetailsDto> GetMyDetailsAsync(CancellationToken ct = default)
    {
        var estate = await _estates.GetAsync(_tenant.EstateId!.Value, ct) ?? throw new KeyNotFoundException("Estate not found.");
        var notice = PrivacyNotice.For(estate);
        var me = (await _people.PeopleOfUserAsync(UserId, ct)).OrderBy(p => p.CreatedAt).FirstOrDefault();
        return me is null
            ? new MyDetailsDto(false, "", null, null, false, false, PrivacyNotice.Version, true, notice)
            : new MyDetailsDto(true, me.Name, me.Email, me.Phone, me.NoticesByEmail, me.NoticesBySms,
                PrivacyNotice.Version, me.PrivacyNoticeVersion == PrivacyNotice.Version, notice);
    }

    public async Task<MyDetailsDto> AcknowledgePrivacyNoticeAsync(string version, CancellationToken ct = default)
    {
        if (version != PrivacyNotice.Version)
            throw new ArgumentException("The privacy notice has changed. Reload the page and read the new one.");

        foreach (var person in await MineAsync(ct))
        {
            person.PrivacyNoticeVersion = version;
            person.PrivacyAcknowledgedAt = _clock.GetUtcNow().UtcDateTime;
            await _people.SaveChangesAsync(ct);
            await AuditAsync(person.Id, "PrivacyNoticeAcknowledged", $"version={version}", ct);
        }
        return await GetMyDetailsAsync(ct);
    }

    public async Task<MyDetailsDto> UpdatePreferencesAsync(bool noticesByEmail, bool noticesBySms, CancellationToken ct = default)
    {
        foreach (var person in await MineAsync(ct))
        {
            if (noticesByEmail && person.Email is null)
                throw new ArgumentException("Add an email address first to get notices by email.");
            if (noticesBySms && person.Phone is null)
                throw new ArgumentException("Add a mobile number first to get notices by SMS.");
            person.NoticesByEmail = noticesByEmail;
            person.NoticesBySms = noticesBySms;
            await _people.SaveChangesAsync(ct);
            await AuditAsync(person.Id, "PreferencesChanged", $"email={noticesByEmail} sms={noticesBySms}", ct);
        }
        return await GetMyDetailsAsync(ct);
    }

    public async Task<OtpStep> StartContactChangeAsync(string newContact, ClientInfo client, CancellationToken ct = default)
    {
        var mine = await MineAsync(ct);
        var value = (newContact ?? string.Empty).Trim();
        var isEmail = ContactPoints.LooksLikeEmail(value);
        var normalised = isEmail ? ContactPoints.Email(value) : ContactPoints.SaMobile(value);

        // Someone else in this estate already has it: go through the motions without sending anything.
        var ids = mine.Select(p => (Guid?)p.Id).ToList();
        var takenHere = false;
        foreach (var id in ids)
            takenHere |= isEmail ? await _people.EmailTakenAsync(normalised, id, ct) : await _people.PhoneTakenAsync(normalised, id, ct);
        return await _auth.StartContactChangeAsync(UserId, normalised, usable: !takenHere, client, ct);
    }

    public async Task<MyDetailsDto> ConfirmContactChangeAsync(string challengeToken, string code, ClientInfo client, CancellationToken ct = default)
    {
        var mine = await MineAsync(ct);
        var changed = await _auth.ConfirmContactChangeAsync(UserId, challengeToken, code, client, ct);
        foreach (var person in mine)
        {
            if (changed.Channel == OtpChannel.Email)
                person.Email = changed.NewValue;
            else
                person.Phone = changed.NewValue;
            await _people.SaveChangesAsync(ct);
            await AuditAsync(person.Id, "ContactChanged", $"channel={changed.Channel} by=self", ct);
        }
        return await GetMyDetailsAsync(ct);
    }

    public async Task<PersonExportDto> ExportMyDataAsync(CancellationToken ct = default)
    {
        var me = (await MineAsync(ct)).OrderBy(p => p.CreatedAt).First();
        return await _data.ExportAsync(me.Id, ct);
    }

    private async Task<IReadOnlyList<Person>> MineAsync(CancellationToken ct)
    {
        var mine = await _people.PeopleOfUserAsync(UserId, ct);
        return mine.Count > 0 ? mine : throw new KeyNotFoundException("You aren't linked to a unit in this estate.");
    }

    private Task AuditAsync(Guid personId, string action, string details, CancellationToken ct) =>
        _audit.CreateAsync(new CreateAuditLogDto("Person", personId.ToString(), action, _user.UserId!, details), ct);
}
