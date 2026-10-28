namespace EstateHub.Application.Interfaces;

/// <summary>Outgoing email (SMTP; Mailpit in development).</summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string text, CancellationToken ct = default);
}

/// <summary>
/// Outgoing SMS to SA numbers (+27…). Development delivers them to Mailpit as emails; the pilot uses
/// the Android gateway (PHASE1.md §1f). Never log message bodies: they carry codes and links.
/// </summary>
public interface ISmsSender
{
    Task SendAsync(string toE164, string text, CancellationToken ct = default);
}

/// <summary>Links into the web app (the API doesn't know its public web address otherwise).</summary>
public interface IAppLinks
{
    /// <summary>The token goes in the URL fragment (#…), which browsers never send to servers or logs.</summary>
    string Invitation(string token);

    /// <summary>The "This wasn't me" page for a new sign-in email (token in the fragment).</summary>
    string SecureAccount(string token);
}
