using System.Text.Json;
using EstateHub.Application.Auth;
using EstateHub.Application.DTOs;
using EstateHub.Application.Imports;
using EstateHub.Application.Interfaces;
using EstateHub.Application.Tenancy;
using EstateHub.Domain.Entities;

namespace EstateHub.Application.Services;

/// <summary>
/// POPIA tools (slice 3.4, SECURITY.md "POPIA tools"): export everything this estate holds about a person,
/// and anonymise a former resident. The audit log names people by id only, so anonymising the person
/// record removes them from what the trail shows; security events keep what was typed, as evidence.
/// </summary>
public class PersonDataService : IPersonDataService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly TimeZoneInfo SouthAfrica = TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg");

    private readonly IPeopleRepository _people;
    private readonly IUnitRepository _units;
    private readonly IEstateRepository _estates;
    private readonly IMembersRepository _members;
    private readonly IMembershipDirectory _memberships;
    private readonly IAuthRepository _users;
    private readonly IAuthService _auth;
    private readonly IImportRepository _imports;
    private readonly IAuditLogService _audit;
    private readonly ICurrentUser _user;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;

    public PersonDataService(
        IPeopleRepository people,
        IUnitRepository units,
        IEstateRepository estates,
        IMembersRepository members,
        IMembershipDirectory memberships,
        IAuthRepository users,
        IAuthService auth,
        IImportRepository imports,
        IAuditLogService audit,
        ICurrentUser user,
        ITenantContext tenant,
        TimeProvider clock)
    {
        _people = people;
        _units = units;
        _estates = estates;
        _members = members;
        _memberships = memberships;
        _users = users;
        _auth = auth;
        _imports = imports;
        _audit = audit;
        _user = user;
        _tenant = tenant;
        _clock = clock;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(Now, SouthAfrica));

    private Guid EstateId => _tenant.EstateId ?? throw new InvalidOperationException("No active estate.");

    // ---------- export ----------

    public async Task<PersonExportDto> ExportAsync(Guid personId, CancellationToken ct = default)
    {
        var person = await _people.GetPersonAsync(personId, ct) ?? throw new KeyNotFoundException("Person not found.");
        var estate = await _estates.GetAsync(EstateId, ct);
        var units = (await _units.ListAsync(ct)).ToDictionary(u => u.Id);
        var ownerships = await _people.OwnershipsOfPeopleAsync([person.Id], ct);
        var occupancies = await _people.OccupanciesOfPeopleAsync([person.Id], ct);
        var invitations = await _members.InvitationsForPersonAsync(person.Id, ct);

        object? account = null;
        if (person.UserId is { } userId && await _users.GetUserAsync(userId, ct) is { } user)
        {
            var sessions = await _users.ListOpenSessionsAsync(user.Id, ct);
            var passkeys = await _users.ListPasskeysAsync(user.Id, ct);
            account = new
            {
                user.Email,
                user.Phone,
                user.DisplayName,
                CreatedAt = Utc(user.CreatedAt),
                PasswordSet = user.PasswordHash is not null,
                user.MfaEnabled,
                Passkeys = passkeys.Select(p => new { p.Name, CreatedAt = Utc(p.CreatedAt) }),
                OpenSessions = sessions.Select(s => new { Device = s.UserAgent, s.IpAddress, SignedInAt = Utc(s.CreatedAt), LastSeenAt = Utc(s.LastSeenAt) }),
            };
        }

        // The trail about them in this estate: their record, their ownerships/occupancies and invitations.
        var activity = new List<AuditLogDto>();
        foreach (var (type, id) in new[] { ("Person", person.Id) }
                     .Concat(ownerships.Select(o => ("Ownership", o.Id)))
                     .Concat(occupancies.Select(o => ("Occupancy", o.Id)))
                     .Concat(invitations.Select(i => ("Invitation", i.Id))))
            activity.AddRange((await _audit.ListAsync(type, id.ToString(), 1, 200, ct)).Items);

        string Section(Guid unitId) => units.TryGetValue(unitId, out var u) ? $"Section {u.SectionNumber}{(u.DoorNumber is { } d ? $" ({d})" : "")}" : "(removed unit)";
        var export = new
        {
            About = "Everything this estate holds about you in EstateHub (POPIA section 23). The estate is responsible for this information; EstateHub runs the system for it.",
            ExportedAt = Utc(Now),
            Estate = estate?.Name,
            Person = new
            {
                person.Id,
                person.Name,
                person.Email,
                person.Phone,
                person.NoticesByEmail,
                person.NoticesBySms,
                PrivacyNotice = new { Version = person.PrivacyNoticeVersion, AcknowledgedAt = Utc(person.PrivacyAcknowledgedAt) },
                CreatedAt = Utc(person.CreatedAt),
                AnonymisedAt = Utc(person.AnonymisedAt),
            },
            Owns = ownerships.OrderBy(o => o.FromDate).Select(o => new { Unit = Section(o.UnitId), SharePercent = o.SharePercent, From = o.FromDate, To = o.ToDate }),
            LivesIn = occupancies.OrderBy(o => o.FromDate).Select(o => new { Unit = Section(o.UnitId), o.Kind, From = o.FromDate, To = o.ToDate }),
            Invitations = invitations.Select(i => new
            {
                i.Role,
                SentTo = i.Email ?? i.Phone,
                SentAt = Utc(i.LastSentAt),
                ExpiresAt = Utc(i.ExpiresAt),
                AcceptedAt = Utc(i.AcceptedAt),
                RevokedAt = Utc(i.RevokedAt),
            }),
            Account = account,
            Activity = activity.OrderBy(a => a.PerformedAt).Select(a => new { When = Utc(a.PerformedAt), a.EntityType, a.Action, a.Details }),
        };

        await AuditAsync(person.Id, "Exported", $"by={(person.UserId?.ToString() == _user.UserId ? "self" : "staff")}", ct);
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(Now, SouthAfrica)).ToString("yyyy-MM-dd");
        return new PersonExportDto($"estatehub-personal-data-{date}.json", JsonSerializer.SerializeToUtf8Bytes(export, Json));
    }

    // ---------- anonymise ----------

    public async Task AnonymiseAsync(Guid personId, string confirmName, CancellationToken ct = default)
    {
        var person = await _people.GetPersonAsync(personId, ct) ?? throw new KeyNotFoundException("Person not found.");
        if (person.AnonymisedAt is not null)
            throw new ArgumentException("This person has already been anonymised.");
        if (!string.Equals((confirmName ?? string.Empty).Trim(), person.Name, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Type the person's name exactly as shown to confirm.");

        // Only former residents: every ownership and occupancy ended before today.
        var ownerships = await _people.OwnershipsOfPeopleAsync([person.Id], ct);
        var occupancies = await _people.OccupanciesOfPeopleAsync([person.Id], ct);
        if (ownerships.Any(o => o.ToDate is null || o.ToDate >= Today) || occupancies.Any(o => o.ToDate is null || o.ToDate >= Today))
            throw new ArgumentException($"{person.Name} still owns or lives in a unit here (or will). End those first; only former residents can be anonymised.");

        var (email, phone, name) = (person.Email, person.Phone, person.Name);

        foreach (var invitation in await _members.InvitationsForPersonAsync(person.Id, ct))
        {
            invitation.RevokedAt ??= invitation.AcceptedAt is null ? Now : null;
            invitation.Name = "Former resident";
            invitation.Email = null;
            invitation.Phone = null;
        }

        if (person.UserId is { } userId)
        {
            if (await _members.FindMembershipAsync(userId, ct) is { } membership)
                membership.IsActive = false;
            await _members.SaveChangesAsync(ct);

            // No other estate: the account itself goes (scrubbed and disabled; history keeps its id).
            var elsewhere = (await _memberships.ForUserAsync(userId, ct)).Any(m => m.EstateId != EstateId);
            if (!elsewhere)
                await _auth.ScrubAccountAsync(userId, _user.UserId!, ct);
        }

        person.Name = $"Former resident {person.Id.ToString("N")[..4].ToUpperInvariant()}";
        person.Email = null;
        person.Phone = null;
        person.UserId = null;
        person.NoticesByEmail = false;
        person.NoticesBySms = false;
        person.AnonymisedAt = Now;
        await _people.SaveChangesAsync(ct);

        // Uploaded spreadsheets that still hold their details (previews never imported) are cleared too.
        foreach (var batch in await _imports.ListHoldingRowsAsync(ct))
        {
            if (ImportStatuses.IsActive(batch.Status))
                continue;
            if ((email is not null && batch.RowsJson.Contains(email, StringComparison.OrdinalIgnoreCase))
                || (phone is not null && (batch.RowsJson.Contains(phone) || batch.RowsJson.Contains("0" + phone[3..])))
                || batch.RowsJson.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                batch.RowsJson = "[]";
                batch.ProblemsJson = "[]";
                batch.SummaryJson = "{}";
                if (batch.Status != ImportStatuses.Done)
                    batch.Status = ImportStatuses.Discarded;
            }
        }
        await _imports.SaveChangesAsync(ct);

        await AuditAsync(person.Id, "Anonymised", null, ct);
    }

    private Task AuditAsync(Guid personId, string action, string? details, CancellationToken ct) =>
        _audit.CreateAsync(new CreateAuditLogDto("Person", personId.ToString(), action, _user.UserId!, details), ct);

    private static DateTimeOffset? Utc(DateTime? value) => value is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)) : null;
}
