using System.Net;
using Microsoft.Extensions.Options;

namespace EstateHub.Api.Security;

/// <summary>Bound from the "ReverseProxy" configuration section.</summary>
public sealed class ReverseProxyOptions
{
    public const string SectionName = "ReverseProxy";

    /// <summary>
    /// Networks the proxies (cloudflared/Caddy on the Docker network) connect from, in CIDR form.
    /// Only requests arriving from these may set the client IP via CF-Connecting-IP.
    /// </summary>
    public string[] TrustedNetworks { get; set; } = [];
}

/// <summary>
/// The real client IP for rate limits and audit entries. Behind Cloudflare → cloudflared → Caddy
/// every request comes from the proxy, so the client IP is taken from CF-Connecting-IP, but only
/// when the direct peer is a trusted proxy. Otherwise anyone could spoof the header to dodge limits.
/// </summary>
public sealed class ClientIpResolver
{
    public const string CloudflareHeader = "CF-Connecting-IP";

    private readonly IPNetwork[] _trusted;

    public ClientIpResolver(IOptions<ReverseProxyOptions> options)
    {
        _trusted = options.Value.TrustedNetworks.Select(IPNetwork.Parse).ToArray();
    }

    public string Resolve(HttpContext http)
    {
        var peer = http.Connection.RemoteIpAddress;
        if (peer is null)
            return "unknown";

        if (peer.IsIPv4MappedToIPv6)
            peer = peer.MapToIPv4();

        if (_trusted.Any(n => n.Contains(peer))
            && IPAddress.TryParse(http.Request.Headers[CloudflareHeader].ToString(), out var client))
            return client.ToString();

        return peer.ToString();
    }
}
