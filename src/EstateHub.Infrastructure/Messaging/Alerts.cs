using EstateHub.Application.Auth;
using EstateHub.Application.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EstateHub.Infrastructure.Messaging;

/// <summary>Bound from "Alerts". Either, both or neither (then alerts are only logged).</summary>
public sealed class AlertOptions
{
    public const string SectionName = "Alerts";

    /// <summary>Full ntfy topic URL, e.g. https://ntfy.example.co.za/estatehub-alerts (self-hosted, SECURITY.md layer 12).</summary>
    public string? NtfyUrl { get; set; }

    /// <summary>Access token for a protected topic (recommended).</summary>
    public string? NtfyToken { get; set; }

    /// <summary>Operator's email for alerts (Mailpit in development).</summary>
    public string? Email { get; set; }
}

/// <summary>
/// Sends security alerts to ntfy and/or email. The same alert is held back for 10 minutes so an
/// attack becomes one notification, not hundreds. Never throws: alerting must not break the action.
/// </summary>
public sealed class AlertSender : IAlerts
{
    public const string HttpClientName = "alerts";
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(10);

    private readonly AlertOptions _options;
    private readonly IHttpClientFactory _http;
    private readonly IEmailSender _email;
    private readonly IMemoryCache _recent;
    private readonly ILogger<AlertSender> _logger;

    public AlertSender(IOptions<AlertOptions> options, IHttpClientFactory http, IEmailSender email, IMemoryCache recent, ILogger<AlertSender> logger)
    {
        _options = options.Value;
        _http = http;
        _email = email;
        _recent = recent;
        _logger = logger;
    }

    public async Task RaiseAsync(string title, string details, CancellationToken ct = default)
    {
        var key = $"alert:{title}:{details}";
        if (_recent.TryGetValue(key, out _))
            return;
        _recent.Set(key, true, Quiet);

        _logger.LogWarning("Security alert: {Title}: {Details}", title, details);

        if (_options.NtfyUrl is { Length: > 0 } url)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(details) };
                request.Headers.Add("Title", $"EstateHub: {title}");
                request.Headers.Add("Priority", "high");
                request.Headers.Add("Tags", "warning");
                if (_options.NtfyToken is { Length: > 0 } token)
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                using var response = await _http.CreateClient(HttpClientName).SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                    _logger.LogError("ntfy refused the alert: HTTP {Status}", (int)response.StatusCode);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Alert not sent to ntfy");
            }
        }

        if (_options.Email is { Length: > 0 } email)
        {
            try
            {
                await _email.SendAsync(email, $"EstateHub alert: {title}", details, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Alert not sent by email");
            }
        }
    }
}
