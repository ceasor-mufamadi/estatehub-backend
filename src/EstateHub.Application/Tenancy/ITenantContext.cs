namespace EstateHub.Application.Tenancy;

/// <summary>
/// The estate the current scope works in. Set once per request by the tenant interceptor (from the
/// token's estate_id) or per estate by cron jobs. Everything tenant-scoped reads it: EF query filters,
/// insert stamping and the Postgres RLS session setting.
/// </summary>
public interface ITenantContext
{
    /// <summary>Null on platform-level calls; tenant-scoped data is then invisible (secure default).</summary>
    Guid? EstateId { get; }

    /// <summary>Sets the estate for this scope. Can only be set once, so a scope never switches estates halfway.</summary>
    void Use(Guid estateId);
}
