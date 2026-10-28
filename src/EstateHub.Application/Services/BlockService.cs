using EstateHub.Application.DTOs;
using EstateHub.Application.Interfaces;
using EstateHub.Domain.Entities;

namespace EstateHub.Application.Services;

// Tenant scoping is not done here: the repository's DbContext filters reads by the active estate
// and stamps EstateId on insert. This service never sees or chooses an estate id.
public class BlockService : IBlockService
{
    private const int MaxNameLength = 100;

    private readonly IBlockRepository _repository;
    private readonly IUnitRepository _units;
    private readonly IAuditLogService _audit;

    public BlockService(IBlockRepository repository, IUnitRepository units, IAuditLogService audit)
    {
        _repository = repository;
        _units = units;
        _audit = audit;
    }

    public async Task<BlockDto> CreateAsync(string name, string performedBy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));

        name = name.Trim();
        if (name.Length > MaxNameLength)
            throw new ArgumentException($"Name must be at most {MaxNameLength} characters.", nameof(name));

        if (await _repository.NameExistsAsync(name, ct))
            throw new ArgumentException($"A block named '{name}' already exists in this estate.", nameof(name));

        var block = new Block { Name = name };
        await _repository.AddAsync(block, ct);

        await _audit.CreateAsync(new CreateAuditLogDto("Block", block.Id.ToString(), "Created", performedBy, $"name={name}"), ct);

        return new BlockDto(block.Id, block.Name);
    }

    public async Task<IReadOnlyList<BlockDto>> ListAsync(CancellationToken ct = default) =>
        (await _repository.ListAsync(ct)).Select(b => new BlockDto(b.Id, b.Name)).ToList();

    public async Task<BlockDto> RenameAsync(Guid blockId, string name, string performedBy, CancellationToken ct = default)
    {
        var block = await _repository.GetAsync(blockId, ct) ?? throw new KeyNotFoundException("Block not found.");
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));

        name = name.Trim();
        if (name.Length > MaxNameLength)
            throw new ArgumentException($"Name must be at most {MaxNameLength} characters.", nameof(name));
        if (await _repository.NameExistsAsync(name, blockId, ct))
            throw new ArgumentException($"A block named '{name}' already exists in this estate.", nameof(name));

        var previous = block.Name;
        block.Name = name;
        await _repository.SaveChangesAsync(ct);
        await _audit.CreateAsync(new CreateAuditLogDto("Block", block.Id.ToString(), "Renamed", performedBy, $"from={previous} to={name}"), ct);
        return new BlockDto(block.Id, block.Name);
    }

    public async Task DeleteAsync(Guid blockId, string performedBy, CancellationToken ct = default)
    {
        var block = await _repository.GetAsync(blockId, ct) ?? throw new KeyNotFoundException("Block not found.");
        if (await _units.AnyInBlockAsync(blockId, ct))
            throw new ArgumentException($"Block '{block.Name}' still has units. Move or remove them first.");

        _repository.Remove(block);
        await _repository.SaveChangesAsync(ct);
        await _audit.CreateAsync(new CreateAuditLogDto("Block", block.Id.ToString(), "Removed", performedBy, $"name={block.Name}"), ct);
    }
}
