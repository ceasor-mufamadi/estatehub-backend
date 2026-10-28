using EstateHub.Application.Auth;
using EstateHub.Application.Interfaces;
using EstateHub.Application.Tenancy;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.AspNetCore.Authorization;

namespace EstateHub.Api.Authorization;

/// <summary>
/// Base for checks that must pass before unary and server-streaming RPCs run. Authentication and
/// [RequiresPermission] already ran in the ASP.NET pipeline; these add the EstateHub-specific
/// decisions read from the endpoint metadata (method + service class attributes).
/// Registered in Program.cs in this order: Tenant → Feature → RecentMfa → Ownership.
/// </summary>
public abstract class PreCallCheckInterceptor : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        await CheckAsync(request!, context);
        return await continuation(request, context);
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await CheckAsync(request!, context);
        await continuation(request, responseStream, context);
    }

    protected abstract Task CheckAsync(object request, ServerCallContext context);

    protected static EndpointMetadataCollection? Metadata(ServerCallContext context) =>
        context.GetHttpContext().GetEndpoint()?.Metadata;

    protected static T Service<T>(ServerCallContext context) where T : notnull =>
        context.GetHttpContext().RequestServices.GetRequiredService<T>();
}

/// <summary>
/// Runs first on every call with a token, two checks:
/// 1. The token's session is still alive and still allows the token's estate and role (signed out,
///    revoked, membership removed or role changed → UNAUTHENTICATED within ~30 s; SECURITY.md layer 4).
/// 2. Tenant isolation, layer 1 of 3: the active estate comes only from the signed token's estate_id,
///    never from the request. Sets ITenantContext once for the call. Without an estate, only
///    [PlatformLevel] RPCs may run.
/// </summary>
public sealed class TenantInterceptor : PreCallCheckInterceptor
{
    public const string SessionEndedMessage = Application.Services.AuthService.SessionEnded;

    protected override async Task CheckAsync(object request, ServerCallContext context)
    {
        // [AllowAnonymous] RPCs (sign-in, refresh, logout) ignore any bearer token, so a stale or
        // revoked one can't block signing in or out, and they never act inside an estate.
        if (Metadata(context)?.GetMetadata<IAllowAnonymous>() is not null)
            return;

        var user = Service<ICurrentUser>(context);
        if (user.IsAuthenticated)
            await CheckSessionAsync(user, context);

        if (user.EstateId is { } estateId)
        {
            Service<ITenantContext>(context).Use(estateId);
            return;
        }

        if (Metadata(context)?.GetMetadata<PlatformLevelAttribute>() is not null)
            return;

        throw new RpcException(new Status(StatusCode.FailedPrecondition, "Select an estate first."));
    }

    private static async Task CheckSessionAsync(ICurrentUser user, ServerCallContext context)
    {
        var valid = user.SessionId is { } sessionId
                    && Guid.TryParse(user.UserId, out var userId)
                    && await Service<ISessionValidator>(context).IsValidAsync(sessionId, userId, user.EstateId, user.Role, context.CancellationToken);

        if (!valid)
            throw new RpcException(new Status(StatusCode.Unauthenticated, SessionEndedMessage));
    }
}

/// <summary>[RequiresFeature]: the active estate must have the feature switched on.</summary>
public sealed class FeatureInterceptor : PreCallCheckInterceptor
{
    protected override async Task CheckAsync(object request, ServerCallContext context)
    {
        var required = Metadata(context)?.GetOrderedMetadata<RequiresFeatureAttribute>();
        if (required is not { Count: > 0 })
            return;

        var estateId = Service<ITenantContext>(context).EstateId
            ?? throw new RpcException(new Status(StatusCode.FailedPrecondition, "Select an estate first."));

        var enabled = await Service<IFeatureService>(context).GetEnabledAsync(estateId, context.CancellationToken);
        var missing = required.FirstOrDefault(f => !enabled.Contains(f.FeatureKey));
        if (missing is not null)
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"The '{missing.FeatureKey}' feature is not enabled for this estate."));
    }
}

/// <summary>[RequiresRecentMfa] (AUTH-12): MFA completed within the last 15 minutes.</summary>
public sealed class RecentMfaInterceptor : PreCallCheckInterceptor
{
    /// <summary>Trailer the web app looks for to prompt for MFA and retry.</summary>
    public const string StepUpTrailer = "estatehub-step-up";

    protected override Task CheckAsync(object request, ServerCallContext context)
    {
        if (Metadata(context)?.GetMetadata<RequiresRecentMfaAttribute>() is null)
            return Task.CompletedTask;

        var user = Service<ICurrentUser>(context);
        var now = Service<TimeProvider>(context).GetUtcNow();
        var recent = user.AuthMethods.Contains(TokenClaims.MfaMethod)
                     && user.AuthTime is { } at
                     && now - at <= RequiresRecentMfaAttribute.MaxAge;

        if (!recent)
            throw new RpcException(
                new Status(StatusCode.PermissionDenied, "Confirm it's you: complete MFA and try again."),
                new Metadata { { StepUpTrailer, "required" } });

        return Task.CompletedTask;
    }
}

/// <summary>
/// [RequiresAccess] checks, reading the id from the named proto field of the request.
/// A failed check is NOT_FOUND, not PERMISSION_DENIED: it never confirms the record exists (SECURITY.md layer 5).
/// </summary>
public sealed class OwnershipInterceptor : PreCallCheckInterceptor
{
    private readonly ILogger<OwnershipInterceptor> _logger;

    public OwnershipInterceptor(ILogger<OwnershipInterceptor> logger)
    {
        _logger = logger;
    }

    protected override async Task CheckAsync(object request, ServerCallContext context)
    {
        var checks = Metadata(context)?.GetOrderedMetadata<RequiresAccessAttribute>();
        if (checks is not { Count: > 0 })
            return;

        var access = Service<IResourceAccess>(context);
        foreach (var check in checks)
        {
            var id = ReadField(request, check.Field);
            if (string.IsNullOrWhiteSpace(id))
            {
                if (check.Optional || access.IsUnrestricted)
                    continue;
                throw Hidden(check);
            }

            if (!await access.CanAccessAsync(check.Kind, id, context.CancellationToken))
            {
                _logger.LogWarning("Ownership check failed: {Method} {Kind} {Id} by {User}",
                    context.Method, check.Kind, id, Service<ICurrentUser>(context).UserId);
                throw Hidden(check);
            }
        }
    }

    private static string? ReadField(object request, string fieldName)
    {
        if (request is not IMessage message)
            throw new InvalidOperationException($"RequiresAccess needs a protobuf request, got {request.GetType().Name}.");

        var field = message.Descriptor.FindFieldByName(fieldName)
            ?? throw new InvalidOperationException($"RequiresAccess: field '{fieldName}' not found on {message.Descriptor.FullName}.");
        return field.Accessor.GetValue(message)?.ToString();
    }

    private static RpcException Hidden(RequiresAccessAttribute check) =>
        new(new Status(StatusCode.NotFound, $"That {check.Kind.ToString().ToLowerInvariant()} was not found."));
}
