# Tenancy & feature toggles

## Tenant isolation (three layers)

| Layer | Where | What it does |
|---|---|---|
| 1. Token | `TenantInterceptor` | Active estate = the signed token's `estate_id`. Requests never carry it. Set once per call (`ITenantContext.Use` refuses a second estate) |
| 2. EF Core | `EstateHubDbContext` | Query filter on every `ITenantScoped` entity; inserts stamped with the estate; writing or moving another estate's rows throws; no estate → no tenant data |
| 3. Postgres | `RowLevelSecurity` + `TenantConnectionInterceptor` | Every connection sets `app.estate_id`; RLS policies (FORCE) only show/accept that estate's rows. The app role is not the owner and not a superuser |

Adding a tenant table:
1. Entity implements `ITenantScoped` (and `IHasTimestamps`, as every entity does).
2. In its migration, after `CreateTable`: `RowLevelSecurity.Enable(migrationBuilder, "<table>")` (and `Disable` in `Down`).
3. Repositories never mention `EstateId`: the context handles it.

Outside the database: stream filters check the estate first; cache and object-storage keys start
with the estate id; cron jobs go through `EstateJobRunner` (one DI scope per estate, tenant set).

⚠️ RLS has been checked by hand in psql against the dev Postgres (InitialCreate, 2026-10-26) but has
no automated test yet (SECURITY.md §6). The EF layer is covered by `TenantDbContextTests` and the
API by `TenantIsolationTests`.

Note for Sprint 2: sign-in must list a user's memberships across estates, which RLS on
`memberships` blocks by design. Do it through a narrow `SECURITY DEFINER` function (owned by the
migrator, returning only that user's rows), not by loosening the policy.

## Feature toggles

`Application/Features/FeatureCatalog.cs` lists every feature with its default and dependencies.
Estates override defaults in `estate_features` (super admin only, `PlatformService/SetEstateFeature`,
needs recent MFA). A feature whose dependency is off is effectively off.

- Backend: a toggleable module's RPCs carry `[RequiresFeature("key")]`; core RPCs `[AlwaysOn("why")]`.
- Jobs: `EstateJobRunner.ForEachEstateAsync(requiredFeature: "key", …)` skips estates without it.
- Frontend: `FeaturesService/GetMyContext` returns the enabled features; the nav renders from them.
