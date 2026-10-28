using EstateHub.Application.Privacy;
using Microsoft.AspNetCore.DataProtection;

namespace EstateHub.Infrastructure.Auth;

/// <summary>ID numbers under their own Data Protection purpose: keys outside the database (SECURITY.md layer 7).</summary>
public sealed class IdNumberProtector(IDataProtectionProvider provider) : IIdNumberProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("EstateHub.VisitorIdNumber.v1");

    public string Protect(string idNumber) => _protector.Protect(idNumber);

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}
