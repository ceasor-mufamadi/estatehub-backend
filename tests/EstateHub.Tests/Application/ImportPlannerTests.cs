using EstateHub.Application.Imports;
using FluentAssertions;
using C = EstateHub.Application.Imports.ImportColumns;

namespace EstateHub.Tests.Application;

// The import's rules (slice 3.3), without a database: each test is one rule staff will meet.
public class ImportPlannerTests
{
    private static readonly DateOnly Today = new(2026, 10, 28);

    private static ImportEstateState Empty(decimal quotaTotal = 0) => new(
        new HashSet<int>(), new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase), [], new Dictionary<Guid, int>(), quotaTotal);

    private static int _row = 1;

    private static ImportRawRow Row(params (string Key, string Value)[] cells) =>
        new(++_row, cells.ToDictionary(c => c.Key, c => c.Value));

    private static ImportPlan Plan(ImportEstateState state, params ImportRawRow[] rows) => ImportPlanner.Plan(rows, state, Today);

    private static ImportPlan Plan(params ImportRawRow[] rows) => Plan(Empty(), rows);

    [Fact]
    public void Co_Owners_Share_A_Unit_And_An_Owner_Can_Own_Several_Units()
    {
        var fixedPlan = Plan(
            Row((C.Section, "1"), (C.Quota, "50"), (C.Block, "Block A"), (C.OwnerName, "Thandi"), (C.OwnerEmail, "thandi@example.co.za"), (C.OwnerLivesHere, "yes")),
            Row((C.Section, "2"), (C.Quota, "50"), (C.Block, "block a"), (C.OwnerName, "Pieter"), (C.OwnerEmail, "pieter@example.co.za"), (C.OwnerShare, "50")),
            Row((C.Section, "2"), (C.OwnerName, "Anna"), (C.OwnerShare, "50"), (C.OwnerSince, "2015-07-15")));

        fixedPlan.IsValid.Should().BeTrue();
        fixedPlan.NewBlocks.Should().Equal("Block A"); // matched case-insensitively
        fixedPlan.Units.Should().HaveCount(2);
        fixedPlan.Units[1].Owners.Select(o => (fixedPlan.People[o.PersonKey].Name, o.Share)).Should().Equal(("Pieter", 50m), ("Anna", 50m));
        fixedPlan.Units[1].Owners[1].Since.Should().Be(new DateOnly(2015, 7, 15));
        fixedPlan.Units[0].Owners[0].Since.Should().Be(Today, "blank = today");
        fixedPlan.Summary.Should().BeEquivalentTo(new { Units = 2, NewBlocks = 1, NewPeople = 3, Ownerships = 3, Occupancies = 1, Invitations = 2, QuotaComplete = true });
    }

    [Fact]
    public void The_Same_Owner_On_Two_Units_Is_One_Person()
    {
        var plan = Plan(
            Row((C.Section, "1"), (C.Quota, "50"), (C.OwnerName, "Thandi"), (C.OwnerEmail, "thandi@example.co.za")),
            Row((C.Section, "2"), (C.Quota, "50"), (C.OwnerName, "Thandi M"), (C.OwnerEmail, "thandi@example.co.za")));

        plan.People.Should().HaveCount(1);
        plan.Problems.Should().ContainSingle().Which.Message.Should().Contain("is Thandi on another row");
    }

    [Fact]
    public void Quotas_As_Fractions_Totalling_1_Become_Percentages()
    {
        var plan = Plan(Row((C.Section, "1"), (C.Quota, "0.25")), Row((C.Section, "2"), (C.Quota, "0,75")));

        plan.IsValid.Should().BeTrue();
        plan.Units.Select(u => u.Quota).Should().Equal(25m, 75m);
    }

    [Fact]
    public void A_Single_Unit_Of_1_Percent_Is_Not_Mistaken_For_A_Fraction()
    {
        Plan(Row((C.Section, "1"), (C.Quota, "1"))).Units.Single().Quota.Should().Be(1m);
        Plan(Row((C.Section, "1"), (C.Quota, "0.5")), Row((C.Section, "2"), (C.Quota, "0.5"), (C.Door, "x"))).Units.Select(u => u.Quota).Should().Equal(50m, 50m);
    }

    [Fact]
    public void Quotas_Over_100_Percent_With_The_Estates_Existing_Units_Are_Refused()
    {
        var plan = Plan(Empty(quotaTotal: 60), Row((C.Section, "5"), (C.Quota, "45")));

        plan.Problems.Should().ContainSingle().Which.Message.Should().Contain("105.0000%");
    }

    [Fact]
    public void Existing_Sections_Are_Refused_Because_The_Import_Only_Adds()
    {
        var state = Empty() with { Sections = new HashSet<int> { 7 } };

        Plan(state, Row((C.Section, "7"), (C.Quota, "1"))).Problems.Should().ContainSingle()
            .Which.Message.Should().Contain("Section 7 already exists");
    }

    [Theory]
    [InlineData("abc", "isn't a whole number")]
    [InlineData("0", "between 1 and 99999")]
    [InlineData("", "required")]
    public void Section_Numbers_Are_Checked(string section, string message)
    {
        Plan(Row((C.Section, section), (C.Quota, "100"), (C.Door, "x"))).Problems.Should().Contain(p => p.Message.Contains(message) && p.Column == "Section");
    }

    [Fact]
    public void A_Units_Details_Must_Agree_Across_Its_Rows()
    {
        var plan = Plan(Row((C.Section, "1"), (C.Quota, "50"), (C.Door, "A1")), Row((C.Section, "1"), (C.Door, "A2")));

        plan.Problems.Should().ContainSingle().Which.Message.Should().Contain("has 'A1' on row");
    }

    [Fact]
    public void Co_Owners_Must_Give_Shares_Totalling_At_Most_100()
    {
        Plan(Row((C.Section, "1"), (C.Quota, "100"), (C.OwnerName, "A")), Row((C.Section, "1"), (C.OwnerName, "B")))
            .Problems.Should().ContainSingle().Which.Message.Should().Contain("give each one's share");
        Plan(Row((C.Section, "1"), (C.Quota, "100"), (C.OwnerName, "A"), (C.OwnerShare, "60")), Row((C.Section, "1"), (C.OwnerName, "B"), (C.OwnerShare, "50")))
            .Problems.Should().ContainSingle().Which.Message.Should().Contain("110%");
    }

    [Fact]
    public void A_Person_Lives_In_Only_One_Unit_In_The_File_Or_Already()
    {
        Plan(
                Row((C.Section, "1"), (C.Quota, "50"), (C.TenantName, "Sipho"), (C.TenantMobile, "073 555 0101")),
                Row((C.Section, "2"), (C.Quota, "50"), (C.TenantName, "Sipho"), (C.TenantMobile, "0735550101")))
            .Problems.Should().ContainSingle().Which.Message.Should().Contain("already lives in section 1");

        var sipho = Guid.NewGuid();
        var state = Empty() with
        {
            People = [new ImportExistingPerson(sipho, "Sipho", null, "+27735550101", false)],
            OpenHomes = new Dictionary<Guid, int> { [sipho] = 9 },
        };
        var plan = Plan(state, Row((C.Section, "1"), (C.Quota, "50"), (C.TenantName, "S. Dlamini"), (C.TenantMobile, "073 555 0101")));
        plan.Problems.Should().ContainSingle().Which.Message.Should().Contain("already lives in Section 9");
    }

    [Fact]
    public void Existing_People_Are_Reused_By_Email_Or_Mobile_And_Account_Holders_Are_Not_Invited()
    {
        var existing = Guid.NewGuid();
        var state = Empty() with { People = [new ImportExistingPerson(existing, "Pieter van Wyk", "pieter@example.co.za", null, true)] };

        var plan = Plan(state, Row((C.Section, "1"), (C.Quota, "100"), (C.OwnerName, "P van Wyk"), (C.OwnerEmail, "Pieter@Example.co.za")));

        plan.IsValid.Should().BeTrue();
        plan.People.Values.Should().ContainSingle(p => p.ExistingId == existing && p.Name == "Pieter van Wyk");
        plan.Summary.Should().BeEquivalentTo(new { NewPeople = 0, ExistingPeople = 1, Invitations = 0 });
    }

    [Fact]
    public void An_Email_And_Mobile_Of_Two_Different_People_Are_Refused()
    {
        var state = Empty() with
        {
            People = [new ImportExistingPerson(Guid.NewGuid(), "A", "a@example.co.za", null, false), new ImportExistingPerson(Guid.NewGuid(), "B", null, "+27821234567", false)],
        };

        Plan(state, Row((C.Section, "1"), (C.Quota, "100"), (C.OwnerName, "X"), (C.OwnerEmail, "a@example.co.za"), (C.OwnerMobile, "0821234567")))
            .Problems.Should().ContainSingle().Which.Message.Should().Contain("belongs to A and the mobile to B");
    }

    [Theory]
    [InlineData(C.OwnerEmail, "not-an-email", "isn't a valid email")]
    [InlineData(C.OwnerMobile, "+44 7700 900123", "South African")]
    [InlineData(C.OwnerSince, "next week", "isn't a date")]
    [InlineData(C.OwnerLivesHere, "maybe", "yes or no")]
    public void Bad_Contact_Details_Dates_And_Answers_Are_Reported_By_Column(string column, string value, string message)
    {
        var plan = Plan(Row((C.Section, "1"), (C.Quota, "100"), (C.OwnerName, "A"), (column, value)));

        plan.Problems.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Column = ImportColumns.Label(column) }, o => o.ExcludingMissingMembers());
        plan.Problems[0].Message.Should().Contain(message);
    }

    [Theory]
    [InlineData("821234567", "+27821234567")] // Excel dropped the leading 0
    [InlineData("082 123 4567", "+27821234567")]
    public void Mobiles_Are_Normalised(string mobile, string expected)
    {
        Plan(Row((C.Section, "1"), (C.Quota, "100"), (C.OwnerName, "A"), (C.OwnerMobile, mobile)))
            .People.Values.Single().Phone.Should().Be(expected);
    }

    [Theory]
    [InlineData("2020-01-31")]
    [InlineData("31/01/2020")]
    [InlineData("31 Jan 2020")]
    [InlineData("43861")] // Excel's date number
    public void Dates_In_Common_Forms_Are_Read(string text)
    {
        ImportPlanner.ParseDate(text).Should().Be(new DateOnly(2020, 1, 31));
    }

    [Fact]
    public void Owner_Details_Without_A_Name_Are_Reported()
    {
        Plan(Row((C.Section, "1"), (C.Quota, "100"), (C.OwnerEmail, "a@example.co.za")))
            .Problems.Should().ContainSingle().Which.Message.Should().Contain("name is required");
    }

    [Fact]
    public void Files_Over_The_Row_Limit_Are_Refused()
    {
        var rows = Enumerable.Range(1, ImportPlanner.MaxRows + 1).Select(i => Row((C.Section, i.ToString()), (C.Quota, "0.01"))).ToArray();

        Plan(rows).Problems.Should().ContainSingle().Which.Message.Should().Contain("limit is 5,000");
    }

    [Fact]
    public void Header_Names_Are_Matched_Loosely()
    {
        ImportColumns.Match(" PQ % ").Should().Be(C.Quota);
        ImportColumns.Match("Owner Mobile").Should().Be(C.OwnerMobile);
        ImportColumns.Match("Size (m²)").Should().Be(C.Size);
        ImportColumns.Match("Notes").Should().BeNull();
    }
}
