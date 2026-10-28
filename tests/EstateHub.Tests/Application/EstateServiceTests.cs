using EstateHub.Application.DTOs;
using EstateHub.Application.Features;
using EstateHub.Application.Interfaces;
using EstateHub.Application.Services;
using EstateHub.Domain.Entities;
using FluentAssertions;
using Moq;

namespace EstateHub.Tests.Application;

public class EstateServiceTests
{
    private readonly Mock<IEstateRepository> _estates = new();
    private readonly Mock<IFeatureRepository> _features = new();
    private readonly Mock<IAuditLogService> _audit = new();
    private readonly Dictionary<string, bool> _overrides = new();
    private readonly Estate _estate = new() { Name = "Sunset Ridge" };
    private readonly EstateService _sut;

    public EstateServiceTests()
    {
        _estates.Setup(r => r.GetAsync(_estate.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_estate);
        _features.Setup(r => r.GetOverridesAsync(_estate.Id, It.IsAny<CancellationToken>())).ReturnsAsync(() => _overrides);
        _features.Setup(r => r.UpsertAsync(_estate.Id, It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, bool, string, CancellationToken>((_, key, enabled, _, _) => _overrides[key] = enabled)
            .Returns(Task.CompletedTask);
        _audit.Setup(a => a.CreateAsync(It.IsAny<CreateAuditLogDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateAuditLogDto d, CancellationToken _) => new AuditLogDto(Guid.NewGuid(), d.EstateId, d.EntityType, d.EntityId, d.Action, d.PerformedBy, DateTime.UtcNow, null, null));

        _sut = new EstateService(_estates.Object, _features.Object, new FeatureService(_features.Object), _audit.Object);
    }

    [Fact]
    public async Task Enabling_A_Feature_Whose_Dependency_Is_Off_Is_Rejected()
    {
        var act = () => _sut.SetFeatureAsync(_estate.Id, FeatureCatalog.ShortStays, true, "admin");

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*online_payments*");
    }

    [Fact]
    public async Task Enabling_A_Feature_Is_Saved_And_Audited_In_That_Estate()
    {
        var result = await _sut.SetFeatureAsync(_estate.Id, FeatureCatalog.Parking, true, "admin");

        result.EnabledFeatures.Should().Contain(FeatureCatalog.Parking);
        _audit.Verify(a => a.CreateAsync(
            It.Is<CreateAuditLogDto>(d => d.EstateId == _estate.Id && d.Action == "Enabled"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task An_Estate_Name_Can_Only_Be_Used_Once()
    {
        _estates.Setup(r => r.NameExistsAsync("Sunset Ridge", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var act = () => _sut.CreateAsync("  Sunset Ridge ", "sectional_title", "admin");

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("An estate named 'Sunset Ridge' already exists*");
        _estates.Verify(r => r.AddAsync(It.IsAny<Estate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Unknown_Feature_Is_Rejected()
    {
        var act = () => _sut.SetFeatureAsync(_estate.Id, "teleporter", true, "admin");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Unknown_Estate_Is_Not_Found()
    {
        var act = () => _sut.SetFeatureAsync(Guid.NewGuid(), FeatureCatalog.Parking, true, "admin");

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Create_Rejects_Unknown_Scheme_Types()
    {
        var act = () => _sut.CreateAsync("X", "timeshare", "admin");

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
