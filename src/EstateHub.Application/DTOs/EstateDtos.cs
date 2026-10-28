namespace EstateHub.Application.DTOs;

public record EstateDto(Guid Id, string Name, string SchemeType, IReadOnlyCollection<string> EnabledFeatures);

public record BlockDto(Guid Id, string Name);

public record UnitDto(Guid Id, int SectionNumber, string? DoorNumber, Guid? BlockId, string? BlockName, decimal ParticipationQuota, decimal? SizeSquareMetres);

/// <summary>A new or changed unit, as typed. PQ is a percentage.</summary>
public record UnitInput(int SectionNumber, string? DoorNumber, Guid? BlockId, decimal ParticipationQuota, decimal? SizeSquareMetres);

/// <summary>Units plus the PQ check: billing splits levies by PQ, so the total must be 100%.</summary>
public record UnitsView(IReadOnlyList<UnitDto> Units, decimal QuotaTotal, bool QuotaComplete);

public record EstateDetailsDto(Guid Id, string Name, string SchemeType, string? SchemeNumber, string? CsosRegistrationNumber, string? Address);
