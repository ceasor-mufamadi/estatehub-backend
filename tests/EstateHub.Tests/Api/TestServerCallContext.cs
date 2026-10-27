using Grpc.Core;

namespace EstateHub.Tests.Api;

/// <summary>Minimal ServerCallContext fake so gRPC service methods can be unit tested without a live server.</summary>
public sealed class TestServerCallContext : ServerCallContext
{
    public static TestServerCallContext Create(CancellationToken cancellationToken = default) => new(cancellationToken);

    private TestServerCallContext(CancellationToken cancellationToken)
    {
        CancellationTokenOverride = cancellationToken;
    }

    private CancellationToken CancellationTokenOverride { get; }

    protected override string MethodCore => "TestMethod";
    protected override string HostCore => "localhost";
    protected override string PeerCore => "peer";
    protected override DateTime DeadlineCore => DateTime.MaxValue;
    protected override Metadata RequestHeadersCore => new();
    protected override CancellationToken CancellationTokenCore => CancellationTokenOverride;
    protected override Metadata ResponseTrailersCore => new();
    protected override Status StatusCore { get; set; }
    protected override WriteOptions? WriteOptionsCore { get; set; }
    protected override AuthContext AuthContextCore => new("none", new Dictionary<string, List<AuthProperty>>());

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
        throw new NotImplementedException();

    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
}
