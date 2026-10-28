using System.Net.Http.Json;
using System.Text.Json.Serialization;
using EstateHub.Application.Auth;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EstateHub.Infrastructure.Auth;

/// <summary>Bound from "Turnstile". Empty secret = the human check is off.</summary>
public sealed class TurnstileOptions
{
    public const string SectionName = "Turnstile";

    /// <summary>
    /// From the Cloudflare dashboard (Turnstile). Development uses Cloudflare's published test secret
    /// "1x0000000000000000000000000000000AA", which always passes.
    /// </summary>
    public string? SecretKey { get; set; }
}

/// <summary>
/// Cloudflare Turnstile server-side verification (siteverify). When Cloudflare can't be reached the
/// check fails: it is only asked of IP addresses that already failed repeatedly, so failing closed
/// costs little and doesn't open a gap during an outage.
/// </summary>
public sealed class TurnstileHumanCheck : IHumanCheck
{
    public const string HttpClientName = "turnstile";
    private const string VerifyUrl = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    private readonly TurnstileOptions _options;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<TurnstileHumanCheck> _logger;

    public TurnstileHumanCheck(IOptions<TurnstileOptions> options, IHttpClientFactory http, ILogger<TurnstileHumanCheck> logger)
    {
        _options = options.Value;
        _http = http;
        _logger = logger;
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(_options.SecretKey);

    public async Task<bool> VerifyAsync(string? token, string? ipAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        try
        {
            var form = new Dictionary<string, string> { ["secret"] = _options.SecretKey!, ["response"] = token };
            if (ipAddress is { Length: > 0 } ip && ip != "unknown")
                form["remoteip"] = ip;

            using var response = await _http.CreateClient(HttpClientName).PostAsync(VerifyUrl, new FormUrlEncodedContent(form), ct);
            var result = await response.Content.ReadFromJsonAsync<SiteVerify>(ct);
            return result?.Success == true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "Turnstile verification unavailable; treating the check as failed");
            return false;
        }
    }

    private sealed record SiteVerify([property: JsonPropertyName("success")] bool Success);
}

/// <summary>
/// Failed sign-in steps per IP address over the last 15 minutes (in memory: one API instance,
/// SECURITY.md §6). Three or more makes the address "suspicious" until the window passes.
/// </summary>
public sealed class SignInFailures : ISignInFailures
{
    public const int Threshold = 3;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly IMemoryCache _cache;
    private readonly TimeProvider _clock;

    public SignInFailures(IMemoryCache cache, TimeProvider clock)
    {
        _cache = cache;
        _clock = clock;
    }

    public void Record(string? ipAddress)
    {
        if (string.IsNullOrEmpty(ipAddress))
            return;

        var now = _clock.GetUtcNow();
        var times = _cache.GetOrCreate(Key(ipAddress), entry =>
        {
            entry.SlidingExpiration = Window;
            return new Queue<DateTimeOffset>();
        })!;
        lock (times)
        {
            times.Enqueue(now);
            while (times.Count > Threshold * 4)
                times.Dequeue();
        }
    }

    public bool IsSuspicious(string? ipAddress)
    {
        if (string.IsNullOrEmpty(ipAddress) || !_cache.TryGetValue(Key(ipAddress), out Queue<DateTimeOffset>? times))
            return false;

        var since = _clock.GetUtcNow() - Window;
        lock (times!)
            return times.Count(t => t > since) >= Threshold;
    }

    private static string Key(string ipAddress) => $"signin-failures:{ipAddress}";
}
