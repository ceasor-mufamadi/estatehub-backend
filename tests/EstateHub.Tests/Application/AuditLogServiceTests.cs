using EstateHub.Application.DTOs;
using EstateHub.Application.Interfaces;
using EstateHub.Application.Services;
using EstateHub.Application.Streaming;
using EstateHub.Application.Tenancy;
using EstateHub.Domain.Entities;
using FluentAssertions;
using Moq;

namespace EstateHub.Tests.Application;

public class AuditLogServiceTests
{
    private readonly Mock<IAuditLogRepository> _repository = new();
    private readonly Mock<ITenantContext> _tenant = new();
    private readonly Mock<IEventBroadcaster<AuditLogDto>> _broadcaster = new();
    private readonly Guid _estateId = Guid.NewGuid();
    private readonly AuditLogService _sut;

    public AuditLogServiceTests()
    {
        _tenant.SetupGet(t => t.EstateId).Returns(_estateId);
        _sut = new AuditLogService(_repository.Object, _tenant.Object, _broadcaster.Object);
    }

    [Fact]
    public async Task CreateAsync_Persists_A_Trimmed_Entry_In_The_Active_Estate_And_Publishes_It()
    {
        AuditLog? captured = null;
        _repository
            .Setup(r => r.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()))
            .Callback<AuditLog, CancellationToken>((log, _) => captured = log)
            .Returns(Task.CompletedTask);

        var result = await _sut.CreateAsync(new CreateAuditLogDto(" Block ", " 123 ", " Created ", " user-1 ", "note"));

        captured.Should().NotBeNull();
        captured!.EstateId.Should().Be(_estateId);
        captured.EntityType.Should().Be("Block");
        captured.PerformedBy.Should().Be("user-1");
        result.Id.Should().Be(captured.Id);
        _broadcaster.Verify(b => b.PublishAsync(It.Is<AuditLogDto>(d => d.Id == captured.Id), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_Uses_An_Explicit_Estate_When_Given()
    {
        var other = Guid.NewGuid();
        AuditLog? captured = null;
        _repository
            .Setup(r => r.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()))
            .Callback<AuditLog, CancellationToken>((log, _) => captured = log)
            .Returns(Task.CompletedTask);

        await _sut.CreateAsync(new CreateAuditLogDto("Estate", "1", "Created", "admin", EstateId: other));

        captured!.EstateId.Should().Be(other);
    }

    [Theory]
    [InlineData("", "1", "Created", "admin")]
    [InlineData("Block", "", "Created", "admin")]
    [InlineData("Block", "1", "", "admin")]
    [InlineData("Block", "1", "Created", "")]
    public async Task CreateAsync_Throws_When_Required_Field_Missing(string entityType, string entityId, string action, string performedBy)
    {
        var act = () => _sut.CreateAsync(new CreateAuditLogDto(entityType, entityId, action, performedBy));

        await act.Should().ThrowAsync<ArgumentException>();
        _repository.Verify(r => r.AddAsync(It.IsAny<AuditLog>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(0, 0, 1, 20)]
    [InlineData(-5, 20, 1, 20)]
    [InlineData(1, 500, 1, 200)]
    [InlineData(3, 10, 3, 10)]
    public async Task ListAsync_Normalizes_Page_And_PageSize_And_Scopes_To_The_Estate(
        int page, int pageSize, int expectedPage, int expectedPageSize)
    {
        _repository
            .Setup(r => r.ListAsync(_estateId, null, null, expectedPage, expectedPageSize, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<AuditLog>(), 0));

        var result = await _sut.ListAsync(" ", null, page, pageSize);

        result.Page.Should().Be(expectedPage);
        result.PageSize.Should().Be(expectedPageSize);
    }

    [Fact]
    public async Task Reads_Without_An_Active_Estate_Throw()
    {
        _tenant.SetupGet(t => t.EstateId).Returns((Guid?)null);

        var act = () => _sut.ListAsync(null, null, 1, 20);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
