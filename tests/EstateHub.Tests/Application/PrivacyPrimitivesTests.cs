using EstateHub.Application.Privacy;
using EstateHub.Domain.Entities;
using FluentAssertions;

namespace EstateHub.Tests.Application;

// Slice 3.4 building blocks: SA ID numbers (for Sprint 6's visitor register) and the privacy notice.
public class PrivacyPrimitivesTests
{
    [Theory]
    [InlineData("8001015009087", true)]   // the well-known valid example
    [InlineData("800101 5009 087", true)] // spaces are ignored
    [InlineData("8001015009088", false)]  // wrong check digit
    [InlineData("8013015009081", false)]  // month 13
    [InlineData("800101500908", false)]   // 12 digits
    [InlineData("", false)]
    public void Sa_Id_Numbers_Are_Checked(string value, bool valid) => SaIdNumber.IsValid(value).Should().Be(valid);

    [Theory]
    [InlineData("8001015009087", "*********9087")]
    [InlineData("800101 5009 087", "*********9087")]
    [InlineData("123", "***")]
    public void Id_Numbers_Are_Only_Ever_Shown_Masked(string value, string masked) => SaIdNumber.Mask(value).Should().Be(masked);

    [Fact]
    public void The_Notice_Names_The_Estate_And_Its_Information_Officer()
    {
        var withOfficer = PrivacyNotice.For(new Estate { Name = "Sunset Villas", InformationOfficerName = "Naledi Khumalo", InformationOfficerEmail = "io@sunset.co.za" });
        var without = PrivacyNotice.For(new Estate { Name = "Sunset Villas" });

        withOfficer[0].Text.Should().Contain("Sunset Villas");
        withOfficer[^1].Text.Should().Contain("Naledi Khumalo (io@sunset.co.za)");
        without[^1].Text.Should().Contain("hasn't been named yet");
        withOfficer.Should().Contain(s => s.Text.Contains("Information Regulator"));
    }
}
