namespace EstateHub.Application.DTOs;

public record EstateDto(Guid Id, string Name, string SchemeType, IReadOnlyCollection<string> EnabledFeatures);

public record BlockDto(Guid Id, string Name);
