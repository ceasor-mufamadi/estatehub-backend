namespace EstateHub.Application.Billing;

/// <summary>
/// Splits a monthly total by participation quota, in cents (PHASE1.md §4). Each unit gets
/// total × quota ÷ 100, rounded to the cent (half away from zero); whatever the rounding leaves over
/// (a few cents either way) goes to the unit with the largest quota, so the parts always add up to the total.
/// Quotas must add up to exactly 100% (the run refuses otherwise).
/// </summary>
public static class QuotaSplit
{
    public static IReadOnlyDictionary<Guid, decimal> Split(decimal total, IReadOnlyList<(Guid UnitId, decimal Quota)> units)
    {
        if (total < 0 || decimal.Round(total, 2) != total)
            throw new ArgumentException("The total must be in rands and cents.");
        if (units.Count == 0)
            return new Dictionary<Guid, decimal>();
        if (units.Sum(u => u.Quota) != 100m)
            throw new ArgumentException($"Participation quotas add up to {units.Sum(u => u.Quota):0.0000}%, not 100%.");

        var parts = units.ToDictionary(u => u.UnitId, u => decimal.Round(total * u.Quota / 100m, 2, MidpointRounding.AwayFromZero));
        var leftover = total - parts.Values.Sum();
        if (leftover != 0)
        {
            // Largest quota takes the difference; ties go to the lowest id so the result never varies.
            var largest = units.OrderByDescending(u => u.Quota).ThenBy(u => u.UnitId).First().UnitId;
            parts[largest] += leftover;
        }
        return parts;
    }
}
