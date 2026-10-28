using EstateHub.Application.Auth;
using EstateHub.Application.DTOs;
using EstateHub.Application.Interfaces;
using EstateHub.Domain.Entities;

namespace EstateHub.Application.Services;

/// <summary>
/// People, ownership and occupancy in the active estate (PHASE1.md §7). "Current" means on today's date
/// in South Africa: started on or before today, and not ended before today. Every change is audited.
/// </summary>
public class PeopleService : IPeopleService
{
    private readonly IPeopleRepository _people;
    private readonly IUnitRepository _units;
    private readonly IAuthRepository _users;
    private readonly IMembersRepository _members;
    private readonly IMembersService _invitations;
    private readonly ICurrentUser _user;
    private readonly IAuditLogService _audit;
    private readonly TimeProvider _clock;

    public PeopleService(
        IPeopleRepository people,
        IUnitRepository units,
        IAuthRepository users,
        IMembersRepository members,
        IMembersService invitations,
        ICurrentUser user,
        IAuditLogService audit,
        TimeProvider clock)
    {
        _people = people;
        _units = units;
        _users = users;
        _members = members;
        _invitations = invitations;
        _user = user;
        _audit = audit;
        _clock = clock;
    }

    private static readonly TimeZoneInfo SouthAfrica = TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg");

    /// <summary>Today in South Africa (ownership dates are calendar dates, not instants).</summary>
    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(_clock.GetUtcNow().UtcDateTime, SouthAfrica));

    private bool IsCurrent(DateOnly from, DateOnly? to) => from <= Today && (to is null || to >= Today);

    // ---------- people ----------

    public async Task<IReadOnlyList<PersonSummaryDto>> ListAsync(string? search, CancellationToken ct = default)
    {
        var people = await _people.ListPeopleAsync(string.IsNullOrWhiteSpace(search) ? null : search.Trim(), ct);
        var ids = people.Select(p => p.Id).ToList();
        var owned = (await _people.OwnershipsOfPeopleAsync(ids, ct)).Where(o => IsCurrent(o.FromDate, o.ToDate)).ToList();
        var lived = (await _people.OccupanciesOfPeopleAsync(ids, ct)).Where(o => IsCurrent(o.FromDate, o.ToDate)).ToList();
        var units = (await _units.ListAsync(ct)).ToDictionary(u => u.Id);

        string Label(Guid unitId) => units.TryGetValue(unitId, out var u) ? $"Section {u.SectionNumber}" : "?";
        return people.Select(p => new PersonSummaryDto(p.Id, p.Name, p.Email, p.Phone, p.UserId is not null,
                owned.Where(o => o.PersonId == p.Id).Select(o => $"{Label(o.UnitId)} (owner)")
                    .Concat(lived.Where(o => o.PersonId == p.Id && o.Kind != OccupancyKinds.OwnerOccupier).Select(o => $"{Label(o.UnitId)} ({o.Kind})"))
                    .ToList()))
            .ToList();
    }

    public async Task<PersonDto> CreateAsync(PersonInput input, CancellationToken ct = default) => ToDto(await NewPersonAsync(input, ct));

    public async Task<PersonDto> UpdateAsync(Guid personId, PersonInput input, CancellationToken ct = default)
    {
        var person = await _people.GetPersonAsync(personId, ct) ?? throw new KeyNotFoundException("Person not found.");
        var (name, email, phone) = await ValidateAsync(input, personId, ct);
        var changed = string.Join(",", new[] { ("name", person.Name != name), ("email", person.Email != email), ("phone", person.Phone != phone) }
            .Where(f => f.Item2).Select(f => f.Item1));
        changed = changed.Length == 0 ? "nothing" : $"fields={changed}";
        person.Name = name;
        person.Email = email;
        person.Phone = phone;
        await _people.SaveChangesAsync(ct);
        await AuditAsync("Person", person.Id, "Updated", changed, ct); // ids only: which fields, never values (POPIA, 2026-10-28)
        return ToDto(person);
    }

    private async Task<Person> NewPersonAsync(PersonInput input, CancellationToken ct)
    {
        var (name, email, phone) = await ValidateAsync(input, null, ct);
        var person = new Person { Name = name, Email = email, Phone = phone };
        _people.Add(person);
        await _people.SaveChangesAsync(ct);
        await AuditAsync("Person", person.Id, "Created", null, ct); // ids only (POPIA, 2026-10-28)
        return person;
    }

    private async Task<(string Name, string? Email, string? Phone)> ValidateAsync(PersonInput input, Guid? exceptPersonId, CancellationToken ct)
    {
        var name = (input.Name ?? string.Empty).Trim();
        if (name.Length is 0 or > 200)
            throw new ArgumentException("Name is required (up to 200 characters).");

        var email = string.IsNullOrWhiteSpace(input.Email) ? null : ContactPoints.Email(input.Email);
        var phone = string.IsNullOrWhiteSpace(input.Phone) ? null : ContactPoints.SaMobile(input.Phone);
        if (email is not null && await _people.EmailTakenAsync(email, exceptPersonId, ct))
            throw new ArgumentException($"Someone in this estate already has the email {email}.");
        if (phone is not null && await _people.PhoneTakenAsync(phone, exceptPersonId, ct))
            throw new ArgumentException($"Someone in this estate already has the mobile number {phone}.");
        return (name, email, phone);
    }

    // ---------- units: owners and occupants ----------

    public async Task<UnitDetailDto> GetUnitAsync(Guid unitId, CancellationToken ct = default)
    {
        var unit = (await _units.ListAsync(ct)).FirstOrDefault(u => u.Id == unitId) ?? throw new KeyNotFoundException("Unit not found.");
        var owners = await _people.OwnershipsOfUnitAsync(unitId, ct);
        var occupants = await _people.OccupanciesOfUnitAsync(unitId, ct);
        var people = new Dictionary<Guid, PersonDto>();
        foreach (var id in owners.Select(o => o.PersonId).Concat(occupants.Select(o => o.PersonId)).Distinct())
            if (await _people.GetPersonAsync(id, ct) is { } p)
                people[id] = ToDto(p);

        var ownerDtos = owners
            .OrderByDescending(o => IsCurrent(o.FromDate, o.ToDate)).ThenByDescending(o => o.FromDate)
            .Select(o => new OwnershipDto(o.Id, people[o.PersonId], o.SharePercent, o.FromDate, o.ToDate, IsCurrent(o.FromDate, o.ToDate)))
            .ToList();
        var occupantDtos = occupants
            .OrderByDescending(o => IsCurrent(o.FromDate, o.ToDate)).ThenByDescending(o => o.FromDate)
            .Select(o => new OccupancyDto(o.Id, people[o.PersonId], o.Kind, o.FromDate, o.ToDate, IsCurrent(o.FromDate, o.ToDate)))
            .ToList();
        return new UnitDetailDto(unit, ownerDtos, occupantDtos, ownerDtos.Where(o => o.IsCurrent).Sum(o => o.SharePercent));
    }

    public async Task<UnitDetailDto> AddOwnerAsync(Guid unitId, PersonRef person, decimal sharePercent, DateOnly? from, CancellationToken ct = default)
    {
        await RequireUnitAsync(unitId, ct);
        if (sharePercent is <= 0 or > 100 || decimal.Round(sharePercent, 2) != sharePercent)
            throw new ArgumentException("Share must be a percentage above 0 and at most 100, with up to 2 decimals.");

        var owner = await ResolveAsync(person, ct);
        var open = (await _people.OwnershipsOfUnitAsync(unitId, ct)).Where(o => o.ToDate is null).ToList();
        if (open.Any(o => o.PersonId == owner.Id))
            throw new ArgumentException($"{owner.Name} already owns this unit.");
        if (open.Sum(o => o.SharePercent) + sharePercent > 100)
            throw new ArgumentException($"The owners' shares would add up to more than 100% (already {open.Sum(o => o.SharePercent):0.##}%). End a previous ownership first.");

        var ownership = new Ownership { UnitId = unitId, PersonId = owner.Id, SharePercent = sharePercent, FromDate = from ?? Today };
        _people.Add(ownership);
        await _people.SaveChangesAsync(ct);
        await AuditAsync("Ownership", ownership.Id, "Added", $"unit={unitId} person={owner.Id} share={sharePercent} from={ownership.FromDate:yyyy-MM-dd}", ct);
        return await GetUnitAsync(unitId, ct);
    }

    public async Task<UnitDetailDto> EndOwnershipAsync(Guid ownershipId, DateOnly to, CancellationToken ct = default)
    {
        var ownership = await _people.GetOwnershipAsync(ownershipId, ct) ?? throw new KeyNotFoundException("Ownership not found.");
        if (to < ownership.FromDate)
            throw new ArgumentException("The end date can't be before the start date.");

        ownership.ToDate = to;
        // An owner who no longer owns the unit doesn't live in it as owner-occupier either.
        foreach (var occupancy in (await _people.OccupanciesOfUnitAsync(ownership.UnitId, ct))
                     .Where(o => o.PersonId == ownership.PersonId && o.Kind == OccupancyKinds.OwnerOccupier && o.ToDate is null))
            occupancy.ToDate = to;
        await _people.SaveChangesAsync(ct);
        await AuditAsync("Ownership", ownership.Id, "Ended", $"to={to:yyyy-MM-dd}", ct);
        return await GetUnitAsync(ownership.UnitId, ct);
    }

    public async Task<UnitDetailDto> RemoveOwnershipAsync(Guid ownershipId, CancellationToken ct = default)
    {
        var ownership = await _people.GetOwnershipAsync(ownershipId, ct) ?? throw new KeyNotFoundException("Ownership not found.");
        _people.Remove(ownership);
        await _people.SaveChangesAsync(ct);
        await AuditAsync("Ownership", ownership.Id, "Removed", $"unit={ownership.UnitId} person={ownership.PersonId} (data-entry correction)", ct);
        return await GetUnitAsync(ownership.UnitId, ct);
    }

    public async Task<UnitDetailDto> AddOccupantAsync(Guid unitId, PersonRef person, string kind, DateOnly? from, CancellationToken ct = default)
    {
        await RequireUnitAsync(unitId, ct);
        kind = (kind ?? string.Empty).Trim();
        if (!OccupancyKinds.All.Contains(kind))
            throw new ArgumentException("Choose owner-occupier, tenant or other.");

        var occupant = await ResolveAsync(person, ct);
        var start = from ?? Today;
        // One home per person in the estate: an owner may own several units but lives in one.
        var home = (await _people.OccupanciesOfPeopleAsync([occupant.Id], ct)).FirstOrDefault(o => o.ToDate is null || o.ToDate >= start);
        if (home is not null)
        {
            if (home.UnitId == unitId)
                throw new ArgumentException($"{occupant.Name} already lives in this unit.");
            var where = await _units.GetAsync(home.UnitId, ct);
            throw new ArgumentException(
                $"{occupant.Name} already lives in Section {where?.SectionNumber}. End that first; a person can live in only one unit at a time.");
        }
        if (kind == OccupancyKinds.OwnerOccupier
            && !(await _people.OwnershipsOfUnitAsync(unitId, ct)).Any(o => o.PersonId == occupant.Id && o.ToDate is null))
            throw new ArgumentException($"{occupant.Name} isn't an owner of this unit. Add them as owner first, or choose tenant.");

        var occupancy = new Occupancy { UnitId = unitId, PersonId = occupant.Id, Kind = kind, FromDate = start };
        _people.Add(occupancy);
        await _people.SaveChangesAsync(ct);
        await AuditAsync("Occupancy", occupancy.Id, "Added", $"unit={unitId} person={occupant.Id} kind={kind} from={occupancy.FromDate:yyyy-MM-dd}", ct);
        return await GetUnitAsync(unitId, ct);
    }

    public async Task<UnitDetailDto> EndOccupancyAsync(Guid occupancyId, DateOnly to, CancellationToken ct = default)
    {
        var occupancy = await _people.GetOccupancyAsync(occupancyId, ct) ?? throw new KeyNotFoundException("Occupancy not found.");
        if (to < occupancy.FromDate)
            throw new ArgumentException("The end date can't be before the start date.");
        occupancy.ToDate = to;
        await _people.SaveChangesAsync(ct);
        await AuditAsync("Occupancy", occupancy.Id, "Ended", $"to={to:yyyy-MM-dd}", ct);
        return await GetUnitAsync(occupancy.UnitId, ct);
    }

    public async Task<UnitDetailDto> RemoveOccupancyAsync(Guid occupancyId, CancellationToken ct = default)
    {
        var occupancy = await _people.GetOccupancyAsync(occupancyId, ct) ?? throw new KeyNotFoundException("Occupancy not found.");
        _people.Remove(occupancy);
        await _people.SaveChangesAsync(ct);
        await AuditAsync("Occupancy", occupancy.Id, "Removed", $"unit={occupancy.UnitId} person={occupancy.PersonId} (data-entry correction)", ct);
        return await GetUnitAsync(occupancy.UnitId, ct);
    }

    // ---------- portal access ----------

    public async Task<InvitePersonResult> InviteAsync(Guid personId, CancellationToken ct = default)
    {
        var person = await _people.GetPersonAsync(personId, ct) ?? throw new KeyNotFoundException("Person not found.");
        if (person.Email is null && person.Phone is null)
            throw new ArgumentException($"Add an email address or mobile number for {person.Name} first.");

        var owns = (await _people.OwnershipsOfPeopleAsync([person.Id], ct)).Any(o => IsCurrent(o.FromDate, o.ToDate) || o.FromDate > Today);
        var lives = (await _people.OccupanciesOfPeopleAsync([person.Id], ct)).Any(o => IsCurrent(o.FromDate, o.ToDate) || o.FromDate > Today);
        if (!owns && !lives)
            throw new ArgumentException($"Add {person.Name} as owner or occupant of a unit first.");
        var role = owns ? Roles.Owner : Roles.Tenant;

        // Already an account with a membership here: link the record, no invitation needed.
        var user = person.Email is not null ? await _users.FindUserByEmailAsync(person.Email, ct) : await _users.FindUserByPhoneAsync(person.Phone!, ct);
        if (user is not null && await _members.FindMembershipAsync(user.Id, ct) is { IsActive: true })
        {
            person.UserId = user.Id;
            await _people.SaveChangesAsync(ct);
            await AuditAsync("Person", person.Id, "LinkedToAccount", $"user={user.Id}", ct);
            return new InvitePersonResult(Linked: true, Invited: false, To: null);
        }

        var invitation = await _invitations.InviteAsync(new InviteRequest(person.Name, person.Email, person.Phone, role, person.Id), ct);
        return new InvitePersonResult(Linked: false, Invited: true, To: ContactPoints.Mask(invitation.Email ?? invitation.Phone!));
    }

    public async Task<IReadOnlyList<MyUnitDto>> MyUnitsAsync(CancellationToken ct = default)
    {
        var me = await _people.PeopleOfUserAsync(Guid.Parse(_user.UserId!), ct);
        var ids = me.Select(p => p.Id).ToList();
        if (ids.Count == 0)
            return [];

        var units = (await _units.ListAsync(ct)).ToDictionary(u => u.Id);
        var result = new List<MyUnitDto>();
        foreach (var o in (await _people.OwnershipsOfPeopleAsync(ids, ct)).Where(o => IsCurrent(o.FromDate, o.ToDate)))
            if (units.TryGetValue(o.UnitId, out var u))
                result.Add(new MyUnitDto(u.Id, u.SectionNumber, u.DoorNumber, u.BlockName, "owner", o.FromDate));
        foreach (var o in (await _people.OccupanciesOfPeopleAsync(ids, ct)).Where(o => IsCurrent(o.FromDate, o.ToDate) && o.Kind != OccupancyKinds.OwnerOccupier))
            if (units.TryGetValue(o.UnitId, out var u) && result.All(r => r.UnitId != u.Id))
                result.Add(new MyUnitDto(u.Id, u.SectionNumber, u.DoorNumber, u.BlockName, o.Kind, o.FromDate));
        return result.OrderBy(r => r.SectionNumber).ToList();
    }

    public async Task<MyUnitDto> GetMyUnitAsync(Guid unitId, CancellationToken ct = default) =>
        (await MyUnitsAsync(ct)).FirstOrDefault(u => u.UnitId == unitId) ?? throw new KeyNotFoundException("That unit was not found.");

    // ---------- helpers ----------

    private async Task RequireUnitAsync(Guid unitId, CancellationToken ct)
    {
        if (await _units.GetAsync(unitId, ct) is null)
            throw new KeyNotFoundException("Unit not found.");
    }

    private async Task<Person> ResolveAsync(PersonRef person, CancellationToken ct)
    {
        if (person.PersonId is { } id)
            return await _people.GetPersonAsync(id, ct) ?? throw new ArgumentException("That person doesn't exist in this estate.");
        if (person.NewPerson is { } input)
            return await NewPersonAsync(input, ct);
        throw new ArgumentException("Choose a person, or enter a new one.");
    }

    private Task AuditAsync(string entityType, Guid id, string action, string? details, CancellationToken ct) =>
        _audit.CreateAsync(new CreateAuditLogDto(entityType, id.ToString(), action, _user.UserId!, details), ct);

    private static PersonDto ToDto(Person p) => new(p.Id, p.Name, p.Email, p.Phone, p.UserId is not null);
}
