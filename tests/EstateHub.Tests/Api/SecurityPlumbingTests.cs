using System.Net;
using EstateHub.Api.Security;
using EstateHub.Api.Streaming;
using EstateHub.Application.DTOs;
using EstateHub.Application.Interfaces;
using EstateHub.Application.Services;
using EstateHub.Application.Tenancy;
using EstateHub.Infrastructure.Streaming;
using FluentAssertions;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Moq;

namespace EstateHub.Tests.Api;

public class StreamConnectionLimiterTests
{
    private static StreamConnectionLimiter Limiter(int max)
    {
        var options = new Mock<IOptionsMonitor<StreamingOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(new StreamingOptions { MaxStreamsPerUser = max });
        return new StreamConnectionLimiter(options.Object);
    }

    [Fact]
    public void Allows_Up_To_The_Limit_Then_ResourceExhausted()
    {
        var limiter = Limiter(2);
        using var one = limiter.Acquire("u1");
        using var two = limiter.Acquire("u1");

        var third = () => limiter.Acquire("u1");

        third.Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.ResourceExhausted);
        limiter.OpenFor("u1").Should().Be(2);
    }

    [Fact]
    public void Closing_A_Stream_Frees_A_Slot_And_Users_Are_Independent()
    {
        var limiter = Limiter(1);
        var lease = limiter.Acquire("u1");
        using var other = limiter.Acquire("u2");

        lease.Dispose();
        lease.Dispose(); // double dispose must not free twice

        using var again = limiter.Acquire("u1");
        limiter.OpenFor("u1").Should().Be(1);
    }
}

public class ClientIpResolverTests
{
    private static ClientIpResolver Resolver(params string[] trusted) =>
        new(Options.Create(new ReverseProxyOptions { TrustedNetworks = trusted }));

    private static HttpContext Request(string peer, string? cfHeader)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        if (cfHeader is not null)
            http.Request.Headers[ClientIpResolver.CloudflareHeader] = cfHeader;
        return http;
    }

    [Fact]
    public void Uses_Cloudflare_Header_From_A_Trusted_Proxy() =>
        Resolver("172.16.0.0/12").Resolve(Request("172.18.0.5", "41.1.2.3")).Should().Be("41.1.2.3");

    [Fact]
    public void Ignores_A_Spoofed_Header_From_An_Untrusted_Peer() =>
        Resolver("172.16.0.0/12").Resolve(Request("8.8.8.8", "1.2.3.4")).Should().Be("8.8.8.8");

    [Fact]
    public void Falls_Back_To_The_Peer_When_The_Header_Is_Missing_Or_Garbage()
    {
        Resolver("172.16.0.0/12").Resolve(Request("172.18.0.5", null)).Should().Be("172.18.0.5");
        Resolver("172.16.0.0/12").Resolve(Request("172.18.0.5", "not-an-ip")).Should().Be("172.18.0.5");
    }
}

// Streams share one broadcaster for every estate: the subscription filter must keep them apart.
public class AuditStreamIsolationTests
{
    [Fact]
    public async Task A_Subscriber_Only_Receives_Its_Own_Estates_Entries()
    {
        var broadcaster = new InMemoryEventBroadcaster<AuditLogDto>();
        var estateA = Guid.NewGuid();
        var estateB = Guid.NewGuid();

        AuditLogService ServiceFor(Guid estate)
        {
            var tenant = new Mock<ITenantContext>();
            tenant.SetupGet(t => t.EstateId).Returns(estate);
            return new AuditLogService(Mock.Of<IAuditLogRepository>(), tenant.Object, broadcaster);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<AuditLogDto>();
        var subscription = Task.Run(async () =>
        {
            await foreach (var dto in ServiceFor(estateB).SubscribeAsync(null, cts.Token))
            {
                received.Add(dto);
                if (dto.Action == "done") break;
            }
        });
        while (broadcaster.SubscriberCount == 0) await Task.Delay(10);

        await ServiceFor(estateA).CreateAsync(new CreateAuditLogDto("Block", "1", "secret-of-A", "a"));
        await ServiceFor(estateB).CreateAsync(new CreateAuditLogDto("Block", "2", "done", "b"));
        await subscription;

        received.Select(r => r.Action).Should().Equal("done");
    }
}
