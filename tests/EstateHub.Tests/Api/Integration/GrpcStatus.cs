using Grpc.Core;

namespace EstateHub.Tests.Api.Integration;

internal static class GrpcStatus
{
    public static async Task<StatusCode> Of(Func<Task> call)
    {
        try { await call(); return StatusCode.OK; }
        catch (RpcException ex) { return ex.StatusCode; }
    }

    public static async Task<RpcException?> ErrorOf(Func<Task> call)
    {
        try { await call(); return null; }
        catch (RpcException ex) { return ex; }
    }
}
