using EstateHub.Application.Auth;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace EstateHub.Api.Errors;

/// <summary>
/// Outermost interceptor. Turns exceptions into gRPC statuses so services don't need try/catch:
///   ArgumentException      → INVALID_ARGUMENT (message is meant for the user)
///   AuthFailedException    → UNAUTHENTICATED (message is meant for the user, never says which part failed)
///   RateLimitedException   → RESOURCE_EXHAUSTED (message is meant for the user)
///   HumanCheckRequiredException → FAILED_PRECONDITION + "estatehub-challenge: turnstile" trailer
///   ActionNotAllowedException → PERMISSION_DENIED (message is meant for the user)
///   KeyNotFoundException   → NOT_FOUND
///   RpcException           → passed through
///   anything else          → INTERNAL "Something went wrong (ref XXXXXXXX)"; details only in the log
/// (SECURITY.md layer 6: production errors never leak internals.)
/// </summary>
public sealed class ErrorInterceptor : Interceptor
{
    private readonly ILogger<ErrorInterceptor> _logger;

    public ErrorInterceptor(ILogger<ErrorInterceptor> logger)
    {
        _logger = logger;
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(request, context);
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw Translate(ex, context);
        }
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            await continuation(request, responseStream, context);
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw Translate(ex, context);
        }
    }

    private RpcException Translate(Exception ex, ServerCallContext context)
    {
        switch (ex)
        {
            case AuthFailedException:
                return new RpcException(new Status(StatusCode.Unauthenticated, ex.Message));
            case HumanCheckRequiredException:
                return new RpcException(
                    new Status(StatusCode.FailedPrecondition, ex.Message),
                    new Metadata { { Security.SignInResponses.ChallengeTrailer, "turnstile" } });
            case RateLimitedException:
                return new RpcException(new Status(StatusCode.ResourceExhausted, ex.Message));
            case ActionNotAllowedException:
                return new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
            case ArgumentException argument:
                return new RpcException(new Status(StatusCode.InvalidArgument, UserMessage(argument)));
            case KeyNotFoundException:
                return new RpcException(new Status(StatusCode.NotFound, ex.Message));
            case OperationCanceledException when context.CancellationToken.IsCancellationRequested:
                return new RpcException(new Status(StatusCode.Cancelled, "The call was cancelled."));
            default:
                var reference = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
                _logger.LogError(ex, "Unhandled error in {Method} (ref {Reference})", context.Method, reference);
                return new RpcException(new Status(StatusCode.Internal, $"Something went wrong (ref {reference})."));
        }
    }

    /// <summary>
    /// ArgumentException.Message appends " (Parameter 'name')" when a parameter name is given: a
    /// developer detail users shouldn't see. The suffix is built the same way (same culture) and cut off.
    /// </summary>
    private static string UserMessage(ArgumentException ex)
    {
        if (string.IsNullOrEmpty(ex.ParamName))
            return ex.Message;

        var suffix = new ArgumentException(string.Empty, ex.ParamName).Message;
        return ex.Message.EndsWith(suffix, StringComparison.Ordinal)
            ? ex.Message[..^suffix.Length].TrimEnd()
            : ex.Message;
    }
}
