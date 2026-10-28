using EstateHub.Application.Interfaces;
using EstateHub.Domain.Entities;
using EstateHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EstateHub.Infrastructure.Repositories;

// People, ownership and occupancy are tenant-scoped: every query here is confined to the active estate.
public class PeopleRepository : IPeopleRepository
{
    private readonly EstateHubDbContext _db;

    public PeopleRepository(EstateHubDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<Person>> ListPeopleAsync(string? search, CancellationToken ct = default)
    {
        var query = _db.People.AsQueryable();
        if (search is not null)
        {
            var term = search.ToLower();
            query = query.Where(p => p.Name.ToLower().Contains(term) || (p.Email != null && p.Email.Contains(term)) || (p.Phone != null && p.Phone.Contains(term)));
        }

        return await query.OrderBy(p => p.Name).Take(500).ToListAsync(ct);
    }

    public Task<Person?> GetPersonAsync(Guid id, CancellationToken ct = default) => _db.People.FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<bool> EmailTakenAsync(string email, Guid? exceptPersonId, CancellationToken ct = default) =>
        _db.People.AnyAsync(p => p.Email == email && p.Id != exceptPersonId, ct);

    public Task<bool> PhoneTakenAsync(string phone, Guid? exceptPersonId, CancellationToken ct = default) =>
        _db.People.AnyAsync(p => p.Phone == phone && p.Id != exceptPersonId, ct);

    public async Task<IReadOnlyList<Ownership>> OwnershipsOfUnitAsync(Guid unitId, CancellationToken ct = default) =>
        await _db.Ownerships.Where(o => o.UnitId == unitId).ToListAsync(ct);

    public async Task<IReadOnlyList<Occupancy>> OccupanciesOfUnitAsync(Guid unitId, CancellationToken ct = default) =>
        await _db.Occupancies.Where(o => o.UnitId == unitId).ToListAsync(ct);

    public async Task<IReadOnlyList<Ownership>> OwnershipsOfPeopleAsync(IReadOnlyCollection<Guid> personIds, CancellationToken ct = default) =>
        await _db.Ownerships.Where(o => personIds.Contains(o.PersonId)).ToListAsync(ct);

    public async Task<IReadOnlyList<Occupancy>> OccupanciesOfPeopleAsync(IReadOnlyCollection<Guid> personIds, CancellationToken ct = default) =>
        await _db.Occupancies.Where(o => personIds.Contains(o.PersonId)).ToListAsync(ct);

    public Task<Ownership?> GetOwnershipAsync(Guid id, CancellationToken ct = default) => _db.Ownerships.FirstOrDefaultAsync(o => o.Id == id, ct);

    public Task<Occupancy?> GetOccupancyAsync(Guid id, CancellationToken ct = default) => _db.Occupancies.FirstOrDefaultAsync(o => o.Id == id, ct);

    public async Task<IReadOnlyList<Person>> PeopleOfUserAsync(Guid userId, CancellationToken ct = default) =>
        await _db.People.Where(p => p.UserId == userId).ToListAsync(ct);

    public void Add(object entity) => _db.Add(entity);

    public void Remove(object entity) => _db.Remove(entity);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
