# Authentication & authorization

EstateHub issues and validates its own tokens: there is no external auth service (PHASE1.md §2a,
SECURITY.md layers 3–5). Read this before adding or changing an RPC.

## Tokens

ES256 JWTs, 15 minutes, signed with `Jwt:SigningKeyPem` (EC P-256). Claims: `sub`, `estate_id`
(absent on platform-level tokens), `role`, `perm[]`, `amr[]` (`otp`, `pwd`, `mfa`), `auth_time`.
Public keys: `AuthService/GetJwks`. Sprint 1 has only `DevSignIn` (Development/Testing); real
sign-in flows land in Sprint 2.

Enforcement is **always on**. There is no `Auth:Enforce` switch.

## What runs on every call

1. ASP.NET authentication: validates the token.
2. ASP.NET authorization: `[RequiresPermission]`; RPCs without any attribute still require a signed-in caller.
3. Rate limiter: per user (signed in) or per client IP (anonymous).
4. gRPC interceptors, in order:
   - `ErrorInterceptor`: exceptions → statuses; unknown errors → `INTERNAL "Something went wrong (ref …)"`
   - `TenantInterceptor`: sets the active estate from `estate_id`; none → `FAILED_PRECONDITION` unless `[PlatformLevel]`
   - `FeatureInterceptor`: `[RequiresFeature]` off → `FAILED_PRECONDITION`
   - `RecentMfaInterceptor`: `[RequiresRecentMfa]` without MFA in 15 min → `PERMISSION_DENIED` + trailer `estatehub-step-up: required`
   - `OwnershipInterceptor`: `[RequiresAccess]` fails → `NOT_FOUND` (never confirms the record exists)

## Rules for every RPC

Each RPC (method or its service class) must declare **all three**. `AuthorizationCoverageTests` fails the build otherwise.

| Decision | Attributes |
|---|---|
| Permission | `[RequiresPermission("estatehub.<module>.read\|write")]`, `[Authorize]` or `[AllowAnonymous]` (anonymous must also be `[PlatformLevel]`) |
| Ownership | `[RequiresAccess(ResourceKind.X, "proto_field")]`, `[AccessScopedInAction("why")]` or `[NoOwnershipRequired("why")]` |
| Feature | `[RequiresFeature("key")]` (key from `FeatureCatalog`) or `[AlwaysOn("why")]` |

Optional: `[RequiresRecentMfa]` for sensitive actions, `[PlatformLevel]` for RPCs that run without an estate.
Estate-scoped request messages must not have an `estate_id` field (tested).

## Permissions and roles

Defined in code: `Application/Auth/Permissions.cs` (`Permissions`, `Roles`, `RolePermissions`).
`platform.access.all` (super admin) passes permission and ownership checks, but **not** tenant
isolation: super admin still sees one estate at a time.
