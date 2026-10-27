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

    public static RpcException InvalidArgument(string message) =>
        new(new Status(StatusCode.InvalidArgument, message));

    public static RpcException NotFound(string message) =>
        new(new Status(StatusCode.NotFound, message));
}
