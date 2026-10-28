namespace EstateHub.Api.Security;

/// <summary>
/// The refresh-token cookie (AUTH-10): HttpOnly (scripts can't read it), SameSite=Strict (other sites
/// can't send it), Secure, and only sent to AuthService RPCs. Bound from the "Auth" section.
/// </summary>
public sealed class AuthCookieOptions
{
    public const string SectionName = "Auth";
    public const string Name = "estatehub_refresh";

    /// <summary>gRPC-Web paths are /package.Service/Method: only Refresh, Logout and friends receive the cookie.</summary>
    public const string Path = "/estatehub.auth.v1.AuthService/";

    /// <summary>
    /// Only turned off in Development (plain http://localhost, where Safari drops Secure cookies).
    /// Program.cs refuses to start with it off anywhere else.
    /// </summary>
    public bool RefreshCookieSecure { get; set; } = true;
}
