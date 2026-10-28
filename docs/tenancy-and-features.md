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

**The one way across the boundary (Sprint 2, migration `AuthCore`).** Sign-in must list a user's
memberships in every estate before any estate is active, which RLS on `memberships` forbids for
`estatehub_app` by design. `estatehub_user_memberships(user_id)` does it instead:
- `SECURITY DEFINER`, owned by `estatehub_migrator`, `search_path` pinned; EXECUTE for `estatehub_app` only.
- A second, SELECT-only policy (`membership_directory`) lets only the migrator role read across
  estates; the tenant policy still binds the app role everywhere else.
- Returns one user's **active** memberships: estate id, estate name, role. Nothing else.
- Used by sign-in, `ListMyEstates`, `SwitchEstate` and the per-request session check
  (`IMembershipDirectory`; tests use an EF InMemory stand-in).

**A second, narrower one (migration `Invitations`).** Accepting an invitation also happens before an
estate is active. `estatehub_invitation_estate(token_hash)` returns only the estate id of the
invitation with that exact hash (the hash of a 256-bit link token, so it can't be used to browse);
the app then reads the invitation inside that estate. Same owner, pinned `search_path`, EXECUTE for
`estatehub_app` only, SELECT-only `invitation_directory` policy. Checked in psql 2026-10-27: direct
read with no estate → 0 rows; Estate B can't see Estate A's invitation; wrong hash → nothing.

**A third, for guard tablets (migration `GuardTabletsAndSupport`).** A tablet is recognised (and
paired) before any estate is active. `estatehub_device_estate(kind, hash)` returns only the estate
id for a pairing-code hash (`'pairing'`) or a tablet-credential hash (`'device'`). Same rules as the
others. Covered by the real-Postgres smoke test (`tests/e2e/run-smoke.sh`).

Membership function checked by hand in psql on 2026-10-27 as `estatehub_app`: direct read of memberships with no estate
set → 0 rows; the function → only the active membership; dropping the policy or replacing the
function → refused.

## Feature toggles

`Application/Features/FeatureCatalog.cs` lists every feature with its default and dependencies.
Estates override defaults in `estate_features` (super admin only, `PlatformService/SetEstateFeature`,
needs recent MFA). A feature whose dependency is off is effectively off.

- Backend: a toggleable module's RPCs carry `[RequiresFeature("key")]`; core RPCs `[AlwaysOn("why")]`.
- Jobs: `EstateJobRunner.ForEachEstateAsync(requiredFeature: "key", …)` skips estates without it.
- Frontend: `FeaturesService/GetMyContext` returns the enabled features; the nav renders from them.
