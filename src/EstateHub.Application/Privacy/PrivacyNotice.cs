using EstateHub.Domain.Entities;

namespace EstateHub.Application.Privacy;

/// <summary>
/// The residents' privacy notice (POPIA s18). Text lives in code, so every change is reviewed; bump
/// <see cref="Version"/> whenever it changes and residents are asked to read it again.
/// </summary>
public static class PrivacyNotice
{
    public const string Version = "2026-10-28";

    public record Section(string Heading, string Text);

    public static IReadOnlyList<Section> For(Estate estate)
    {
        var officer = estate.InformationOfficerName is { } name
            ? $"{name}{(estate.InformationOfficerEmail is { } email ? $" ({email})" : "")}"
            : "the estate's managing agent (the Information Officer hasn't been named yet)";

        return
        [
            new("Who holds your information",
                $"{estate.Name} (the body corporate) is responsible for your personal information. EstateHub runs this system for the estate as its operator and uses your information only on the estate's instructions."),
            new("What we hold and why",
                "Your name, email, mobile number and the units you own or live in, with dates. The estate uses them to run the scheme: levies and statements, notices, and access to the estate. You don't need to consent to this: the estate needs it to manage the scheme and meet its legal duties."),
            new("Who sees it",
                "The estate's staff (managing agent, managers and trustees). Your neighbours never see your details, and guards see unit numbers only. Nothing is sold or shared for marketing."),
            new("How long",
                "While you own or live in a unit. After you leave, financial records are kept as long as the law requires; your contact details can then be removed."),
            new("Your rights",
                "You can see what is held about you (download it under My details), correct your email and mobile there, ask the estate to correct anything else, object to processing, and complain to the Information Regulator (inforegulator.org.za)."),
            new("Questions",
                $"Contact {officer}."),
        ];
    }
}
