using EstateHub.Domain.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace EstateHub.Tests.Infrastructure;

// Tenant isolation layer 2 (SECURITY.md layer 5): EF query filters + write guard.
public class TenantDbContextTests
{
    private readonly TestDb _db = new();
    private readonly Guid _estateA = Guid.NewGuid();
    private readonly Guid _estateB = Guid.NewGuid();

    private async Task SeedBlockAsync(Guid estateId, string name)
    {
        await using var db = _db.For(estateId);
        db.Blocks.Add(new Block { Name = name });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Insert_Is_Stamped_With_The_Active_Estate()
    {
        await SeedBlockAsync(_estateA, "Block A");

        await using var db = _db.For(_estateA);
        (await db.Blocks.SingleAsync()).EstateId.Should().Be(_estateA);
    }

    [Fact]
    public async Task Reads_Only_See_The_Active_Estate()
    {
        await SeedBlockAsync(_estateA, "Block A");
        await SeedBlockAsync(_estateB, "Block B");

        await using var db = _db.For(_estateB);
        (await db.Blocks.Select(b => b.Name).ToListAsync()).Should().Equal("Block B");
    }

    [Fact]
    public async Task Another_Estates_Row_Looks_Like_It_Does_Not_Exist()
    {
        await SeedBlockAsync(_estateA, "Block A");
        Guid id;
        await using (var a = _db.For(_estateA))
            id = (await a.Blocks.SingleAsync()).Id;

        await using var b = _db.For(_estateB);
        (await b.Blocks.FindAsync(id)).Should().BeNull();
        (await b.Blocks.AnyAsync(x => x.Id == id)).Should().BeFalse();
    }

    [Fact]
    public async Task Without_An_Estate_Tenant_Data_Is_Invisible()
    {
        await SeedBlockAsync(_estateA, "Block A");

        await using var db = _db.Platform();
        (await db.Blocks.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Without_An_Estate_Tenant_Data_Cannot_Be_Written()
    {
        await using var db = _db.Platform();
        db.Blocks.Add(new Block { Name = "Orphan" });

        var save = () => db.SaveChangesAsync();
        await save.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no active estate*");
    }

    [Fact]
    public async Task Writing_A_Row_For_Another_Estate_Throws()
    {
        await using var db = _db.For(_estateA);
        db.Blocks.Add(new Block { Name = "Sneaky", EstateId = _estateB });

        var save = () => db.SaveChangesAsync();
        await save.Should().ThrowAsync<InvalidOperationException>().WithMessage("*another estate*");
    }

    [Fact]
    public async Task Moving_A_Row_To_Another_Estate_Throws()
    {
        await SeedBlockAsync(_estateA, "Block A");

        await using var db = _db.For(_estateA);
        var block = await db.Blocks.SingleAsync();
        block.EstateId = _estateB;

        var save = () => db.SaveChangesAsync();
        await save.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Timestamps_Are_Set_On_Insert_And_Update_Keeps_CreatedAt()
    {
        var created = _db.Clock.Now;
        await SeedBlockAsync(_estateA, "Block A");

        _db.Clock.Now = created.AddHours(3);
        await using (var db = _db.For(_estateA))
        {
            var block = await db.Blocks.SingleAsync();
            block.Name = "Block A (renamed)";
            block.CreatedAt = DateTime.MinValue; // must be ignored
            await db.SaveChangesAsync();
        }

        await using var check = _db.For(_estateA);
        var saved = await check.Blocks.SingleAsync();
        saved.CreatedAt.Should().Be(created.UtcDateTime);
        saved.UpdatedAt.Should().Be(created.AddHours(3).UtcDateTime);
    }

    [Fact]
    public async Task Non_Tenant_Tables_Also_Get_Timestamps()
    {
        await using var db = _db.Platform();
        var estate = new Estate { Name = "Sunset Ridge" };
        db.Estates.Add(estate);
        await db.SaveChangesAsync();

        estate.CreatedAt.Should().Be(_db.Clock.Now.UtcDateTime);
        estate.UpdatedAt.Should().Be(_db.Clock.Now.UtcDateTime);
    }
}
