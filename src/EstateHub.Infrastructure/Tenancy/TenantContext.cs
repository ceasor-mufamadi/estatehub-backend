using EstateHub.Application.Tenancy;

namespace EstateHub.Infrastructure.Tenancy;

/// <summary>Scoped holder of the active estate. See <see cref="ITenantContext"/>.</summary>
public sealed class TenantContext : ITenantContext
{
    public Guid? EstateId { get; private set; }

    public void Use(Guid estateId)
    {
        if (estateId == Guid.Empty)
            throw new ArgumentException("Estate id is required.", nameof(estateId));

        if (EstateId is { } current && current != estateId)
            throw new InvalidOperationException("This scope is already working in another estate.");

        EstateId = estateId;
    }
}
