using EstateHub.Application.Billing;
using FluentAssertions;

namespace EstateHub.Tests.Application;

// The levy split to the cent (PHASE1.md §4): parts always add up to the budget.
public class QuotaSplitTests
{
    private static (Guid, decimal)[] Units(params decimal[] quotas) => quotas.Select(q => (Guid.NewGuid(), q)).ToArray();

    [Fact]
    public void Even_Quotas_Split_Exactly()
    {
        var units = Units(25, 25, 50);

        var parts = QuotaSplit.Split(10_000m, units);

        parts.Values.Should().Equal(2500m, 2500m, 5000m);
    }

    [Fact]
    public void Leftover_Cents_Go_To_The_Largest_Quota_And_The_Total_Always_Matches()
    {
        var units = Units(33.3333m, 33.3333m, 33.3334m);

        var parts = QuotaSplit.Split(100m, units);

        parts.Values.Sum().Should().Be(100m);
        parts[units[2].Item1].Should().Be(33.34m, "the largest quota absorbs the rounding");
        parts[units[0].Item1].Should().Be(33.33m);
    }

    [Fact]
    public void Rounding_Up_Too_Far_Is_Taken_Back_From_The_Largest_Quota()
    {
        // 3 × round(0.335) = 3 × 0.34 = 1.02 > 1.00: the largest unit gives back 2 cents.
        var units = Units(33.5m, 33.5m, 33m);

        var parts = QuotaSplit.Split(1m, units);

        parts.Values.Sum().Should().Be(1m);
        parts.Values.Should().OnlyContain(v => v >= 0.31m && v <= 0.34m);
    }

    [Fact]
    public void Many_Units_With_Four_Decimal_Quotas_Still_Add_Up()
    {
        var random = new Random(42);
        var raw = Enumerable.Range(0, 150).Select(_ => (decimal)random.Next(1, 10_000)).ToArray();
        var quotas = raw.Select(r => decimal.Round(r / raw.Sum() * 100m, 4)).ToArray();
        quotas[^1] += 100m - quotas.Sum();

        var parts = QuotaSplit.Split(123_456.78m, Units(quotas));

        parts.Values.Sum().Should().Be(123_456.78m);
    }

    [Fact]
    public void Quotas_That_Dont_Add_Up_To_100_Are_Refused()
    {
        var act = () => QuotaSplit.Split(1000m, Units(50, 49.5m));

        act.Should().Throw<ArgumentException>().WithMessage("*99.5000%, not 100%*");
    }
}
