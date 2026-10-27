using FluentAssertions;
using Grpc.AspNetCore.Server;
using Grpc.Core;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace EstateHub.Tests.Api.Integration;

/// <summary>Guards the composition root: every gRPC service is mapped and constructible, and nothing else is exposed.</summary>
public class HostWiringTests : IClassFixture<EstateHubApiFactory>
{
    private readonly EstateHubApiFactory _factory;

    public HostWiringTests(EstateHubApiFactory factory)
    {
        _factory = factory;
    }

    private static IReadOnlyList<Type> GrpcServiceImplementations() =>
        typeof(Program).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                     && t.BaseType?.GetCustomAttributes(typeof(BindServiceMethodAttribute), inherit: false).Length > 0)
            .ToList();

    private IReadOnlyList<Type> MappedGrpcServices() =>
        _factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .Select(e => e.Metadata.GetMetadata<GrpcMethodMetadata>()?.ServiceType)
            .OfType<Type>()
            .Distinct()
            .ToList();

    [Fact]
    public async Task Host_Starts_And_Root_Endpoint_Responds()
    {
        using var client = _factory.CreateClient();

        var body = await client.GetStringAsync("/");

        body.Should().Be("EstateHub api running");
    }

    [Fact]
    public void Api_Assembly_Contains_Grpc_Services()
    {
        GrpcServiceImplementations().Should().NotBeEmpty();
    }

    [Fact]
    public void Every_Grpc_Service_Implementation_Is_Mapped()
    {
        GrpcServiceImplementations().Should().BeSubsetOf(MappedGrpcServices(),
            "every *GrpcService in EstateHub.Api must have a matching app.MapGrpcService<T>() in Program.cs");
    }

    [Fact]
    public void Every_Mapped_Grpc_Service_Can_Be_Constructed_From_DI()
    {
        using var scope = _factory.Services.CreateScope();

        foreach (var serviceType in MappedGrpcServices())
        {
            var create = () => ActivatorUtilities.CreateInstance(scope.ServiceProvider, serviceType);

            create.Should().NotThrow($"{serviceType.Name} needs all of its constructor dependencies registered");
        }
    }

    [Fact]
    public void Only_Grpc_And_The_Health_Route_Are_Exposed()
    {
        // gRPC-only, plus "/" for health. Sprint 5 adds /webhooks/{provider} (PHASE1.md §1d): update this list then.
        var httpRoutes = _factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<GrpcMethodMetadata>() is null)
            .Select(e => e.RoutePattern.RawText)
            .Where(r => r is not null && !r.StartsWith("{", StringComparison.Ordinal))
            // gRPC's own per-service catch-all: only ever answers UNIMPLEMENTED.
            .Where(r => !r!.EndsWith("/{unimplementedMethod:grpcunimplemented}", StringComparison.Ordinal))
            .ToList();

        httpRoutes.Should().BeEquivalentTo(["/"]);
    }
}
