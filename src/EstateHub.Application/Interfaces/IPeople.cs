using EstateHub.Application.DTOs;
using EstateHub.Domain.Entities;

namespace EstateHub.Application.Interfaces;

/// <summary>
/// The estate's people and who owns or occupies which unit (slice 3.2). Staff only (PeopleRead/Write);
/// residents get <see cref="MyUnitsAsync"/> and <see cref="GetMyUnitAsync"/>, limited to their own units.
/// </summary>
public interface IPeopleService
{
    Task<IReadOnlyList<PersonSummaryDto>> ListAsync(string? search, CancellationToken ct = default);
    Task<PersonDto> CreateAsync(PersonInput input, CancellationToken ct = default);
    Task<PersonDto> UpdateAsync(Guid personId, PersonInput input, CancellationToken ct = default);

    /// <summary>A unit with its owners and occupants (current and past).</summary>
    Task<UnitDetailDto> GetUnitAsync(Guid unitId, CancellationToken ct = default);

    Task<UnitDetailDto> AddOwnerAsync(Guid unitId, PersonRef person, decimal sharePercent, DateOnly? from, CancellationToken ct = default);
    Task<UnitDetailDto> EndOwnershipAsync(Guid ownershipId, DateOnly to, CancellationToken ct = default);
    Task<UnitDetailDto> RemoveOwnershipAsync(Guid ownershipId, CancellationToken ct = default);

    Task<UnitDetailDto> AddOccupantAsync(Guid unitId, PersonRef person, string kind, DateOnly? from, CancellationToken ct = default);
    Task<UnitDetailDto> EndOccupancyAsync(Guid occupancyId, DateOnly to, CancellationToken ct = default);
    Task<UnitDetailDto> RemoveOccupancyAsync(Guid occupancyId, CancellationToken ct = default);

    /// <summary>
    /// Gives a person portal access: owner or tenant, from their current relationship to a unit. Someone
    /// who already has an account in this estate is linked at once; everyone else gets an invitation.
    /// </summary>
    Task<InvitePersonResult> InviteAsync(Guid personId, CancellationToken ct = default);

    /// <summary>The caller's own units: owned or lived in today.</summary>
    Task<IReadOnlyList<MyUnitDto>> MyUnitsAsync(CancellationToken ct = default);

    /// <summary>One of the caller's own units (the ownership check runs before this: ResourceKind.Unit).</summary>
    Task<MyUnitDto> GetMyUnitAsync(Guid unitId, CancellationToken ct = default);
}

public record PersonInput(string Name, string? Email, string? Phone);

/// <summary>An existing person, or a new one to create on the spot.</summary>
public record PersonRef(Guid? PersonId, PersonInput? NewPerson);

public record PersonDto(Guid Id, string Name, string? Email, string? Phone, bool HasAccount);

public record PersonSummaryDto(Guid Id, string Name, string? Email, string? Phone, bool HasAccount, IReadOnlyList<string> Units);

public record OwnershipDto(Guid Id, PersonDto Person, decimal SharePercent, DateOnly From, DateOnly? To, bool IsCurrent);

public record OccupancyDto(Guid Id, PersonDto Person, string Kind, DateOnly From, DateOnly? To, bool IsCurrent);

public record UnitDetailDto(UnitDto Unit, IReadOnlyList<OwnershipDto> Owners, IReadOnlyList<OccupancyDto> Occupants, decimal CurrentOwnerShare);

public record MyUnitDto(Guid UnitId, int SectionNumber, string? DoorNumber, string? BlockName, string Relationship, DateOnly Since);

public record InvitePersonResult(bool Linked, bool Invited, string? To);

public interface IPeopleRepository
{
    Task<IReadOnlyList<Person>> ListPeopleAsync(string? search, CancellationToken ct = default);
    Task<Person?> GetPersonAsync(Guid id, CancellationToken ct = default);
    Task<bool> EmailTakenAsync(string email, Guid? exceptPersonId, CancellationToken ct = default);
    Task<bool> PhoneTakenAsync(string phone, Guid? exceptPersonId, CancellationToken ct = default);
    Task<IReadOnlyList<Ownership>> OwnershipsOfUnitAsync(Guid unitId, CancellationToken ct = default);
    Task<IReadOnlyList<Occupancy>> OccupanciesOfUnitAsync(Guid unitId, CancellationToken ct = default);
    Task<IReadOnlyList<Ownership>> OwnershipsOfPeopleAsync(IReadOnlyCollection<Guid> personIds, CancellationToken ct = default);
    Task<IReadOnlyList<Occupancy>> OccupanciesOfPeopleAsync(IReadOnlyCollection<Guid> personIds, CancellationToken ct = default);
    Task<Ownership?> GetOwnershipAsync(Guid id, CancellationToken ct = default);
    Task<Occupancy?> GetOccupancyAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Person>> PeopleOfUserAsync(Guid userId, CancellationToken ct = default);
    void Add(object entity);
    void Remove(object entity);
    Task SaveChangesAsync(CancellationToken ct = default);
}
