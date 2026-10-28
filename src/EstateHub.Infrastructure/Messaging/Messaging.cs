using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text;
using EstateHub.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EstateHub.Infrastructure.Messaging;

/// <summary>
/// Bound from "Messaging". Choosing a provider is configuration only (docs/messaging.md):
/// email is plain SMTP (Mailpit, a free tier, or any paid service); SMS picks an adapter by name.
/// </summary>
public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    /// <summary>Public address of the web app, for links in messages (e.g. https://app.example.co.za).</summary>
    public string WebBaseUrl { get; set; } = "http://localhost:3000";

    public string SmtpHost { get; set; } = "localhost";
    public int SmtpPort { get; set; } = 1025;
    public bool SmtpUseTls { get; set; }
    public string? SmtpUser { get; set; }
    public string? SmtpPassword { get; set; }
    public string From { get; set; } = "EstateHub <no-reply@estatehub.local>";

    /// <summary>"mailpit" (development), "android-gateway" (pilot), "bulksms" (paid). Anything else refuses to send.</summary>
    public string SmsProvider { get; set; } = "none";

    public AndroidGatewayOptions AndroidGateway { get; set; } = new();
    public BulkSmsOptions BulkSms { get; set; } = new();
}

/// <summary>"SMS Gateway for Android" (sms-gate.app): a free app on a phone with a SIM, or its cloud relay.</summary>
public sealed class AndroidGatewayOptions
{
    /// <summary>Full send endpoint, e.g. http://192.168.1.50:8080/message (phone on the LAN) or the cloud relay's.</summary>
    public string Url { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

/// <summary>BulkSMS (bulksms.com) REST API, basic auth with an API token.</summary>
public sealed class BulkSmsOptions
{
    public string Url { get; set; } = "https://api.bulksms.com/v1/messages";
    public string TokenId { get; set; } = string.Empty;
    public string TokenSecret { get; set; } = string.Empty;
}

/// <summary>Plain-text email over SMTP. Message bodies (codes, links) are never logged.</summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly MessagingOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<MessagingOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendAsync(string to, string subject, string text, CancellationToken ct = default)
    {
        using var message = new MailMessage { From = new MailAddress(_options.From), Subject = subject, Body = text };
        message.To.Add(to);

        using var client = new SmtpClient(_options.SmtpHost, _options.SmtpPort) { EnableSsl = _options.SmtpUseTls };
        if (_options.SmtpUser is { Length: > 0 } user)
            client.Credentials = new System.Net.NetworkCredential(user, _options.SmtpPassword);

        await client.SendMailAsync(message, ct);
        // Code emails carry the code in the subject, so the subject isn't logged either.
        _logger.LogInformation("Email sent ({Kind})", subject.Length > 0 && char.IsAsciiDigit(subject[0]) ? "code" : "message");
    }
}

/// <summary>Sends through the adapter named by Messaging:SmsProvider. Only SA numbers (AUTH-06).</summary>
public sealed class ConfiguredSmsSender : ISmsSender
{
    public const string HttpClientName = "sms";
    public const string DevDomain = "sms.estatehub.local";

    private readonly MessagingOptions _options;
    private readonly IEmailSender _email;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<ConfiguredSmsSender> _logger;

    public ConfiguredSmsSender(IOptions<MessagingOptions> options, IEmailSender email, IHttpClientFactory http, ILogger<ConfiguredSmsSender> logger)
    {
        _options = options.Value;
        _email = email;
        _http = http;
        _logger = logger;
    }

    public async Task SendAsync(string toE164, string text, CancellationToken ct = default)
    {
        if (!toE164.StartsWith("+27", StringComparison.Ordinal))
            throw new ArgumentException("SMS only goes to South African numbers.");

        switch (_options.SmsProvider)
        {
            case "mailpit":
                await _email.SendAsync($"sms-{toE164.TrimStart('+')}@{DevDomain}", $"SMS to {toE164}", text, ct);
                break;

            case "android-gateway":
                var gateway = _options.AndroidGateway;
                await PostAsync(gateway.Url, Basic(gateway.Username, gateway.Password),
                    new { message = text, phoneNumbers = new[] { toE164 } }, ct);
                break;

            case "bulksms":
                var bulk = _options.BulkSms;
                await PostAsync(bulk.Url, Basic(bulk.TokenId, bulk.TokenSecret), new { to = toE164, body = text }, ct);
                break;

            default:
                _logger.LogError("SMS not sent: no SMS provider configured (Messaging:SmsProvider = {Provider})", _options.SmsProvider);
                throw new InvalidOperationException("SMS isn't set up on this server yet.");
        }

        _logger.LogInformation("SMS sent via {Provider}", _options.SmsProvider);
    }

    private async Task PostAsync(string url, AuthenticationHeaderValue auth, object body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException($"Messaging settings for '{_options.SmsProvider}' are incomplete (no URL).");

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = auth;
        using var response = await _http.CreateClient(HttpClientName).SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            // Status only: the response may echo the message, which carries a code or link.
            _logger.LogError("SMS provider {Provider} refused the message: HTTP {Status}", _options.SmsProvider, (int)response.StatusCode);
            throw new InvalidOperationException("The SMS couldn't be sent. Please try again in a moment.");
        }
    }

    private static AuthenticationHeaderValue Basic(string user, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
}

public sealed class AppLinks : IAppLinks
{
    private readonly MessagingOptions _options;

    public AppLinks(IOptions<MessagingOptions> options)
    {
        _options = options.Value;
    }

    public string Invitation(string token) => $"{_options.WebBaseUrl.TrimEnd('/')}/invite#{token}";

    public string SecureAccount(string token) => $"{_options.WebBaseUrl.TrimEnd('/')}/secure-account#{token}";
}
