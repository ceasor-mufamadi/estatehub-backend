using System.Globalization;
using EstateHub.Application.Auth;
using EstateHub.Domain.Entities;

namespace EstateHub.Application.Imports;

/// <summary>A data row as stored on the batch: its spreadsheet row number and its cells by column key.</summary>
public record ImportRawRow(int Row, Dictionary<string, string> Values);

/// <summary>What an import is checked against: the estate as it is now.</summary>
public record ImportEstateState(
    IReadOnlySet<int> Sections,
    IReadOnlyDictionary<string, Guid> BlocksByName,
    IReadOnlyList<ImportExistingPerson> People,
    IReadOnlyDictionary<Guid, int> OpenHomes,
    decimal QuotaTotal);

public record ImportExistingPerson(Guid Id, string Name, string? Email, string? Phone, bool HasAccount);

public record PlannedPerson(string Key, Guid? ExistingId, string Name, string? Email, string? Phone, bool HasAccount);

public record PlannedOwner(string PersonKey, decimal Share, DateOnly Since, bool LivesHere);

public record PlannedTenant(string PersonKey, DateOnly Since);

public record PlannedUnit(int Section, string? Door, string? Block, decimal Quota, decimal? Size, List<PlannedOwner> Owners, List<PlannedTenant> Tenants);

public record ImportPlan(
    IReadOnlyList<PlannedUnit> Units,
    IReadOnlyDictionary<string, PlannedPerson> People,
    IReadOnlyList<string> NewBlocks,
    IReadOnlyList<ImportProblemDto> Problems,
    ImportSummaryDto Summary)
{
    public bool IsValid => Problems.Count == 0;

    /// <summary>People to invite after the commit: owners and tenants with contact details and no account yet.</summary>
    public IEnumerable<PlannedPerson> Invitees =>
        Units.SelectMany(u => u.Owners.Select(o => o.PersonKey).Concat(u.Tenants.Select(t => t.PersonKey)))
            .Distinct().Select(k => People[k]).Where(p => !p.HasAccount && (p.Email is not null || p.Phone is not null));
}

/// <summary>
/// Turns spreadsheet rows into a plan for the estate, or problems by row and column. Pure: no database,
/// so the preview and the commit (re-checked against fresh data) agree, and every rule is unit-tested.
/// The rules are the same as entering by hand (UnitService, PeopleService) plus the file's own consistency.
/// </summary>
public static class ImportPlanner
{
    public const int MaxRows = 5_000;
    public const int MaxStoredProblems = 500;

    public static ImportPlan Plan(IReadOnlyList<ImportRawRow> rows, ImportEstateState state, DateOnly today)
    {
        var problems = new List<ImportProblemDto>();
        void Problem(int row, string column, string message) => problems.Add(new ImportProblemDto(row, column is "" ? "" : ImportColumns.Label(column), message));

        if (rows.Count == 0)
            Problem(0, "", "The file has no rows to import under the header.");
        if (rows.Count > MaxRows)
        {
            Problem(0, "", string.Create(CultureInfo.InvariantCulture, $"The file has {rows.Count:N0} rows; the limit is {MaxRows:N0}. Split it into smaller files."));
            return Empty(problems, state);
        }

        // ----- units: grouped by section; unit columns may repeat (same value) or be left blank on later rows
        var units = new Dictionary<int, UnitDraft>();
        foreach (var row in rows)
        {
            var sectionText = Cell(row, ImportColumns.Section);
            if (sectionText is null)
            {
                Problem(row.Row, ImportColumns.Section, "Section number is required.");
                continue;
            }

            if (!int.TryParse(sectionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var section)
                && !(decimal.TryParse(sectionText, NumberStyles.Number, CultureInfo.InvariantCulture, out var whole) && whole == decimal.Truncate(whole) && int.TryParse(((int)whole).ToString(CultureInfo.InvariantCulture), out section)))
            {
                Problem(row.Row, ImportColumns.Section, $"'{sectionText}' isn't a whole number.");
                continue;
            }
            if (section is < 1 or > 99_999)
            {
                Problem(row.Row, ImportColumns.Section, "Section number must be between 1 and 99999.");
                continue;
            }
            if (state.Sections.Contains(section))
            {
                Problem(row.Row, ImportColumns.Section, $"Section {section} already exists in this estate. The import only adds new units: leave it out, or remove it first.");
                continue;
            }

            if (!units.TryGetValue(section, out var unit))
                units[section] = unit = new UnitDraft(section, row.Row);
            unit.Rows.Add(row);

            foreach (var key in new[] { ImportColumns.Door, ImportColumns.Block, ImportColumns.Quota, ImportColumns.Size })
            {
                if (Cell(row, key) is not { } value)
                    continue;
                if (unit.Fields.TryGetValue(key, out var first) && !string.Equals(first.Value, value, StringComparison.OrdinalIgnoreCase))
                    Problem(row.Row, key, $"Section {section} has '{first.Value}' on row {first.Row}. Give the unit's details once, or the same on every row.");
                else
                    unit.Fields.TryAdd(key, (value, row.Row));
            }
        }

        // PQs as fractions (all units total 1) are converted to percentages.
        var quotas = new Dictionary<int, decimal>();
        foreach (var unit in units.Values)
        {
            if (!unit.Fields.TryGetValue(ImportColumns.Quota, out var q))
                Problem(unit.FirstRow, ImportColumns.Quota, $"Section {unit.Section} needs a participation quota.");
            else if (Number(q.Value) is not { } quota)
                Problem(q.Row, ImportColumns.Quota, $"'{q.Value}' isn't a number.");
            else
                quotas[unit.Section] = quota;
        }
        var fileTotal = quotas.Values.Sum();
        var fractions = quotas.Count > 1 && state.QuotaTotal == 0 && quotas.Values.All(q => q < 1) && fileTotal is >= 0.9999m and <= 1.0001m;

        var planned = new List<PlannedUnit>();
        var newBlocks = new List<string>();
        foreach (var unit in units.Values.OrderBy(u => u.Section))
        {
            decimal quota = 0;
            if (quotas.TryGetValue(unit.Section, out var raw))
            {
                quota = fractions ? raw * 100 : raw;
                var row = unit.Fields[ImportColumns.Quota].Row;
                if (quota is <= 0 or > 100)
                    Problem(row, ImportColumns.Quota, "Participation quota must be a percentage above 0 and at most 100.");
                else if (decimal.Round(quota, 4) != quota)
                    Problem(row, ImportColumns.Quota, "Participation quota can have at most 4 decimals (e.g. 2.3456).");
            }

            string? door = null;
            if (unit.Fields.TryGetValue(ImportColumns.Door, out var d))
            {
                door = d.Value;
                if (door.Length > 20)
                    Problem(d.Row, ImportColumns.Door, "Door number must be at most 20 characters.");
            }

            decimal? size = null;
            if (unit.Fields.TryGetValue(ImportColumns.Size, out var s))
            {
                size = Number(s.Value);
                if (size is not { } m || m <= 0 || m > 100_000)
                    Problem(s.Row, ImportColumns.Size, "Size must be a number above 0 and at most 100 000 m².");
                else
                    size = decimal.Round(m, 2);
            }

            string? block = null;
            if (unit.Fields.TryGetValue(ImportColumns.Block, out var b))
            {
                block = state.BlocksByName.Keys.FirstOrDefault(k => string.Equals(k, b.Value, StringComparison.OrdinalIgnoreCase))
                        ?? newBlocks.FirstOrDefault(k => string.Equals(k, b.Value, StringComparison.OrdinalIgnoreCase));
                if (block is null)
                {
                    if (b.Value.Length > 100)
                        Problem(b.Row, ImportColumns.Block, "Block name must be at most 100 characters.");
                    else
                        newBlocks.Add(block = b.Value);
                }
            }

            planned.Add(new PlannedUnit(unit.Section, door, block, quota, size, [], []));
        }

        var quotaAfter = state.QuotaTotal + planned.Sum(u => u.Quota);
        if (quotaAfter > 100)
            Problem(0, ImportColumns.Quota, $"Participation quotas would total {quotaAfter:0.0000}%, more than 100% (the estate already has {state.QuotaTotal:0.0000}%).");

        // ----- people: matched by email, then mobile, then (without either) by name within the file
        var people = new Dictionary<string, PlannedPerson>(StringComparer.Ordinal);
        var homes = new Dictionary<string, (int Section, int Row)>();
        var byUnit = planned.ToDictionary(u => u.Section);

        foreach (var unit in units.Values)
        {
            if (!byUnit.TryGetValue(unit.Section, out var target))
                continue;

            foreach (var row in unit.Rows)
            {
                if (Person(row, "owner", ImportColumns.OwnerName, ImportColumns.OwnerEmail, ImportColumns.OwnerMobile) is { } owner)
                {
                    var since = Date(row, ImportColumns.OwnerSince) ?? today;
                    var livesHere = YesNo(row, ImportColumns.OwnerLivesHere);
                    decimal? share = null;
                    if (Cell(row, ImportColumns.OwnerShare) is { } shareText)
                    {
                        share = Number(shareText);
                        if (share is not { } v || v <= 0 || v > 100 || decimal.Round(v, 2) != v)
                        {
                            Problem(row.Row, ImportColumns.OwnerShare, "Share must be a percentage above 0 and at most 100, with up to 2 decimals.");
                            share = null;
                        }
                    }

                    if (target.Owners.Any(o => o.PersonKey == owner.Key))
                        Problem(row.Row, ImportColumns.OwnerName, $"{owner.Name} is already an owner of section {unit.Section} on another row.");
                    else
                    {
                        target.Owners.Add(new PlannedOwner(owner.Key, share ?? -1, since, livesHere));
                        if (livesHere)
                            Home(owner, unit.Section, row.Row, ImportColumns.OwnerLivesHere);
                    }
                }
                else if (Cell(row, ImportColumns.OwnerShare) is not null || Cell(row, ImportColumns.OwnerSince) is not null || YesNo(row, ImportColumns.OwnerLivesHere))
                {
                    Problem(row.Row, ImportColumns.OwnerName, "Owner details without the owner's name.");
                }

                if (Person(row, "tenant", ImportColumns.TenantName, ImportColumns.TenantEmail, ImportColumns.TenantMobile) is { } tenant)
                {
                    if (target.Tenants.Any(t => t.PersonKey == tenant.Key))
                        continue; // the same tenant repeated on a co-owner's row
                    target.Tenants.Add(new PlannedTenant(tenant.Key, Date(row, ImportColumns.TenantSince) ?? today));
                    Home(tenant, unit.Section, row.Row, ImportColumns.TenantName);
                }
                else if (Cell(row, ImportColumns.TenantSince) is not null)
                {
                    Problem(row.Row, ImportColumns.TenantName, "Tenant start date without the tenant's name.");
                }
            }

            // Shares: blank means 100 for a single owner; co-owners must say, and total at most 100.
            if (target.Owners.Count == 1 && target.Owners[0].Share < 0)
                target.Owners[0] = target.Owners[0] with { Share = 100 };
            else if (target.Owners.Any(o => o.Share < 0))
            {
                Problem(unit.FirstRow, ImportColumns.OwnerShare, $"Section {unit.Section} has {target.Owners.Count} owners: give each one's share.");
                target.Owners.RemoveAll(o => o.Share < 0);
            }
            if (target.Owners.Sum(o => o.Share) > 100)
                Problem(unit.FirstRow, ImportColumns.OwnerShare, $"The owners of section {unit.Section} would hold {target.Owners.Sum(o => o.Share):0.##}%, more than 100%.");
        }

        var existing = people.Values.Count(p => p.ExistingId is not null);
        var summary = new ImportSummaryDto(
            planned.Count, newBlocks.Count, people.Count - existing, existing,
            planned.Sum(u => u.Owners.Count), planned.Sum(u => u.Owners.Count(o => o.LivesHere) + u.Tenants.Count),
            0, quotaAfter, quotaAfter == 100);
        var plan = new ImportPlan(planned, people, newBlocks, problems.OrderBy(p => p.Row).ToList(), summary);
        return plan with { Summary = summary with { Invitations = plan.Invitees.Count() } };

        // A person on this row (owner or tenant), resolved against the estate and earlier rows; null if the name is blank.
        PlannedPerson? Person(ImportRawRow row, string what, string nameKey, string emailKey, string mobileKey)
        {
            var name = Cell(row, nameKey);
            var emailText = Cell(row, emailKey);
            var mobileText = Cell(row, mobileKey);
            if (name is null)
            {
                if (emailText is not null || mobileText is not null)
                    Problem(row.Row, nameKey, $"The {what}'s name is required when an email or mobile is given.");
                return null;
            }
            if (name.Length > 200)
            {
                Problem(row.Row, nameKey, "Name must be at most 200 characters.");
                return null;
            }

            string? email = null, phone = null;
            try { email = emailText is null ? null : ContactPoints.Email(emailText); }
            catch (ArgumentException) { Problem(row.Row, emailKey, $"'{emailText}' isn't a valid email address."); return null; }
            // Excel drops the leading 0 of 0821234567 in a number cell: put it back.
            if (mobileText is { Length: 9 } && mobileText.All(char.IsAsciiDigit) && mobileText[0] is '6' or '7' or '8')
                mobileText = "0" + mobileText;
            try { phone = mobileText is null ? null : ContactPoints.SaMobile(mobileText); }
            catch (ArgumentException e) { Problem(row.Row, mobileKey, $"'{mobileText}': {e.Message}"); return null; }

            var byEmail = email is null ? null : state.People.FirstOrDefault(p => p.Email == email);
            var byPhone = phone is null ? null : state.People.FirstOrDefault(p => p.Phone == phone);
            if (byEmail is not null && byPhone is not null && byEmail.Id != byPhone.Id)
            {
                Problem(row.Row, mobileKey, $"The email belongs to {byEmail.Name} and the mobile to {byPhone.Name} in this estate.");
                return null;
            }

            var match = byEmail ?? byPhone;
            var key = match is not null ? $"id:{match.Id}" : email is not null ? $"e:{email}" : phone is not null ? $"p:{phone}" : $"n:{name.ToLowerInvariant()}";

            // The same email or mobile can't name two different people in the file.
            var other = people.Values.FirstOrDefault(p => p.Key != key && ((email is not null && p.Email == email) || (phone is not null && p.Phone == phone)));
            if (other is not null)
            {
                Problem(row.Row, email is not null && other.Email == email ? emailKey : mobileKey,
                    $"Also used for {other.Name} on another row: each email and mobile belongs to one person.");
                return null;
            }

            if (people.TryGetValue(key, out var known))
            {
                if (match is null && !string.Equals(known.Name, name, StringComparison.OrdinalIgnoreCase))
                    Problem(row.Row, nameKey, $"{email ?? phone} is {known.Name} on another row, but '{name}' here.");
                return known;
            }

            return people[key] = match is not null
                ? new PlannedPerson(key, match.Id, match.Name, match.Email, match.Phone, match.HasAccount)
                : new PlannedPerson(key, null, name, email, phone, false);
        }

        // One home per person: in the file, and against where they already live.
        void Home(PlannedPerson person, int section, int row, string column)
        {
            if (homes.TryGetValue(person.Key, out var home) && home.Section != section)
                Problem(row, column, $"{person.Name} already lives in section {home.Section} (row {home.Row}). A person lives in only one unit.");
            else if (person.ExistingId is { } id && state.OpenHomes.TryGetValue(id, out var current))
                Problem(row, column, $"{person.Name} already lives in Section {current}. End that first in EstateHub; a person lives in only one unit.");
            else
                homes[person.Key] = (section, row);
        }

        DateOnly? Date(ImportRawRow row, string key)
        {
            if (Cell(row, key) is not { } text)
                return null;
            if (ParseDate(text) is { } date)
                return date;
            Problem(row.Row, key, $"'{text}' isn't a date. Use e.g. 2020-01-31.");
            return null;
        }

        bool YesNo(ImportRawRow row, string key)
        {
            switch (Cell(row, key)?.ToLowerInvariant())
            {
                case null or "no" or "n" or "false" or "0": return false;
                case "yes" or "y" or "true" or "1": return true;
                default:
                    Problem(row.Row, key, "Use yes or no.");
                    return false;
            }
        }
    }

    private static ImportPlan Empty(List<ImportProblemDto> problems, ImportEstateState state) =>
        new([], new Dictionary<string, PlannedPerson>(), [], problems, new ImportSummaryDto(0, 0, 0, 0, 0, 0, 0, state.QuotaTotal, state.QuotaTotal == 100));

    private static string? Cell(ImportRawRow row, string key) =>
        row.Values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    /// <summary>"2,3456", "2.3456", "50 %", "1 234.5" → decimal.</summary>
    public static decimal? Number(string text)
    {
        var cleaned = new string(text.Where(c => !char.IsWhiteSpace(c) && c != '%').ToArray()).Replace(',', '.');
        return decimal.TryParse(cleaned, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static readonly string[] DateFormats =
        ["yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "d MMM yyyy", "d MMMM yyyy", "yyyy-MM-ddTHH:mm:ss"];

    /// <summary>ISO and South African day-first dates, or an Excel date number (days since 1899-12-30).</summary>
    public static DateOnly? ParseDate(string text)
    {
        if (DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date))
            return date;
        if (DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dateTime))
            return DateOnly.FromDateTime(dateTime);
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is >= 1 and < 2_958_466)
            return DateOnly.FromDateTime(DateTime.FromOADate(Math.Floor(serial)));
        return null;
    }

    private sealed class UnitDraft(int section, int firstRow)
    {
        public int Section { get; } = section;
        public int FirstRow { get; } = firstRow;
        public List<ImportRawRow> Rows { get; } = [];
        public Dictionary<string, (string Value, int Row)> Fields { get; } = new();
    }
}
