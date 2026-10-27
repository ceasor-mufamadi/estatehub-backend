using EstateHub.Api.Authorization;
using EstateHub.Application.Auth;
using EstateHub.Application.Interfaces;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;

namespace EstateHub.Api.Services;

// Sprint 1 only: keys + a development sign-in. Real sign-in flows (AUTH-01…18) arrive in Sprint 2.
[AllowAnonymous]
[PlatformLevel]
[NoOwnershipRequired("Sign-in and public keys: no caller-owned data.")]
[AlwaysOn("Authentication can't be switched off.")]
public class AuthGrpcService : AuthService.AuthServiceBase
{
    private readonly ITokenService _tokens;
    private readonly IEstateRepository _estates;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<AuthGrpcService> _logger;

    public AuthGrpcService(ITokenService tokens, IEstateRepository estates, IHostEnvironment environment, ILogger<AuthGrpcService> logger)
    {
        _tokens = tokens;
        _estates = estates;
        _environment = environment;
        _logger = logger;
    }

    public override async Task<TokenResponse> DevSignIn(DevSignInRequest request, ServerCallContext context)
    {
        if (!_environment.IsDevelopment() && !_environment.IsEnvironment("Testing"))
            throw new RpcException(new Status(StatusCode.Unimplemented, "Not available."));

        if (string.IsNullOrWhiteSpace(request.UserId))
            throw GrpcRequest.InvalidArgument("user_id is required.");

        if (!RolePermissions.IsKnownRole(request.Role))
            throw GrpcRequest.InvalidArgument($"Unknown role '{request.Role}'. Known: {string.Join(", ", RolePermissions.AllRoles)}.");

        var estateId = GrpcRequest.OptionalGuid(request.EstateId, "estate_id");
        if (estateId is null && request.Role != Roles.SuperAdmin)
            throw GrpcRequest.InvalidArgument("estate_id is required for every role except super_admin.");

        if (estateId is { } id && await _estates.GetAsync(id, context.CancellationToken) is null)
            throw GrpcRequest.NotFound("Estate not found.");

        var permissions = RolePermissions.For(request.Role);
        string[] methods = request.Mfa ? ["otp", TokenClaims.MfaMethod] : ["otp"];
        var issued = _tokens.Issue(new TokenRequest(
            request.UserId.Trim(), estateId, request.Role, permissions, methods, DateTimeOffset.UtcNow));

        _logger.LogWarning("DevSignIn issued a {Role} token for {UserId} (development only)", request.Role, request.UserId);

        var response = new TokenResponse
        {
            AccessToken = issued.AccessToken,
            ExpiresAt = Timestamp.FromDateTimeOffset(issued.ExpiresAt),
            Role = request.Role,
            EstateId = estateId?.ToString() ?? string.Empty,
        };
        response.Permissions.AddRange(permissions);
        return response;
    }

    public override Task<GetJwksResponse> GetJwks(GetJwksRequest request, ServerCallContext context) =>
        Task.FromResult(new GetJwksResponse { JwksJson = _tokens.GetJwksJson() });
}
