namespace EstateHub.Application.Privacy;

/// <summary>
/// South African ID numbers (YYMMDD SSSS C A Z), for visitor records when an estate turns them on
/// (Sprint 6). They reveal birth date, gender and citizenship, so they are stored encrypted
/// (<see cref="IIdNumberProtector"/>) and only ever shown or logged masked.
/// </summary>
public static class SaIdNumber
{
    /// <summary>13 digits, a real date, and a valid Luhn check digit.</summary>
    public static bool IsValid(string? value)
    {
        var digits = Normalise(value);
        if (digits is null)
            return false;
        if (!DateOnly.TryParseExact(digits[..6], "yyMMdd", out _))
            return false;

        var sum = 0;
        for (var i = 0; i < 13; i++)
        {
            var d = digits[12 - i] - '0';
            if (i % 2 == 1 && (d *= 2) > 9)
                d -= 9;
            sum += d;
        }
        return sum % 10 == 0;
    }

    /// <summary>Digits only, or null unless exactly 13.</summary>
    public static string? Normalise(string? value)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        return digits.Length == 13 ? digits : null;
    }

    /// <summary>"8001015009087" → "*********9087": enough to compare at the gate, not enough to misuse.</summary>
    public static string Mask(string? value)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        return digits.Length <= 4 ? new string('*', digits.Length) : new string('*', digits.Length - 4) + digits[^4..];
    }
}

/// <summary>Encrypts ID numbers at rest (Data Protection, a purpose of their own; keys outside the database).</summary>
public interface IIdNumberProtector
{
    string Protect(string idNumber);
    string Unprotect(string protectedValue);
}
