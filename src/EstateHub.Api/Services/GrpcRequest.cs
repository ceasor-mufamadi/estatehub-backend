using System.Globalization;
using Grpc.Core;

namespace EstateHub.Api.Services;

/// <summary>
/// Request-field parsing for gRPC services. Invalid input becomes <see cref="StatusCode.InvalidArgument"/>
/// instead of an unhandled FormatException surfacing as INTERNAL.
/// </summary>
internal static class GrpcRequest
{
    public static Guid RequiredGuid(string value, string field) =>
        Guid.TryParse(value, out var result) && result != Guid.Empty
            ? result
            : throw InvalidArgument($"{field} must be a valid id.");

    public static Guid? OptionalGuid(string value, string field) =>
        string.IsNullOrWhiteSpace(value) ? null : RequiredGuid(value, field);

    /// <summary>Rands as typed: "1 234,56", "1234.56", "-150" → decimal. Blank → null.</summary>
    public static decimal? OptionalMoney(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var cleaned = new string(value.Where(c => !char.IsWhiteSpace(c) && c != 'R').ToArray()).Replace(',', '.');
        return decimal.TryParse(cleaned, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount)
            ? amount
            : throw InvalidArgument($"{label} must be an amount in rands, e.g. 1234.56.");
    }

    public static decimal RequiredMoney(string value, string label) =>
        OptionalMoney(value, label) ?? throw InvalidArgument($"{label} is required.");

    public static DateOnly? OptionalDate(string value, string field) =>
        string.IsNullOrWhiteSpace(value) ? null
        : DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date
        : throw InvalidArgument($"{field} must be a date like 2026-10-28.");

    /// <summary>Money on the wire: "1234.56".</summary>
    public static string Money(decimal amount) => amount.ToString("F2", CultureInfo.InvariantCulture);

    public static string Date(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    public static RpcException InvalidArgument(string message) =>
        new(new Status(StatusCode.InvalidArgument, message));

    public static RpcException NotFound(string message) =>
        new(new Status(StatusCode.NotFound, message));
}
