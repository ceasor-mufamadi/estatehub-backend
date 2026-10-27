# EstateHub gRPC Backend

The backend for **EstateHub**, multi-tenant estate management for South African sectional title
schemes (plan: `../PHASE1.md`, security: `../SECURITY.md`). Built with **.NET 10**, **gRPC +
gRPC-Web**, **EF Core** and **PostgreSQL**, in the same layered structure as `chieta-etqa-backend`.

## Architecture

```
src/
  EstateHub.Domain          Entities (ITenantScoped, IHasTimestamps), no external dependencies
  EstateHub.Application     DTOs, service + repository interfaces, business logic, FeatureCatalog, RolePermissions
  EstateHub.Infrastructure  EF Core DbContext (tenant filters, write guard, timestamps), migrations,
                            repositories, token service (ES256), cron framework + jobs
  EstateHub.Api             gRPC services, Program.cs (RUN_MODE api|jobs), authorization attributes + interceptors,
                            streaming, rate limits

EstateHubProtos/            .proto contracts (server + client stubs); estatehub-web generates its TS client from these
tests/EstateHub.Tests       Domain/ Application/ Infrastructure/ Api/ (+ Api/Integration, Contracts/)
docker/postgres/init        Creates the estatehub_migrator + estatehub_app roles and the dev/test databases
```

Same image, two roles: `RUN_MODE=api` (default) serves gRPC; `RUN_MODE=jobs` runs the cron jobs
(framework from `chieta-disbursement-service`).

## Development with Docker (no local .NET SDK needed)

Docker Desktop must be open. All commands from this folder.

```bash
cp .env.example .env                                   # once: local-only passwords
docker compose -f docker-compose.dev.yml up -d         # Postgres (dev + test DBs) + Mailpit; leave running
```

| Task | Command |
|---|---|
| Build | `docker compose -f docker-compose.dev.yml run --rm sdk dotnet build` |
| Test | `docker compose -f docker-compose.dev.yml run --rm sdk dotnet test` |
| Add a migration | `docker compose -f docker-compose.dev.yml run --rm sdk sh -c "dotnet tool restore && dotnet ef migrations add <Name> --project src/EstateHub.Infrastructure --startup-project src/EstateHub.Infrastructure --output-dir Data/Migrations"` |
| Apply migrations (as `estatehub_migrator`) | `docker compose -f docker-compose.dev.yml run --rm sdk sh -c "dotnet tool restore && dotnet ef database update --project src/EstateHub.Infrastructure --startup-project src/EstateHub.Infrastructure"` |
| Run the API (watch mode) | `docker compose -f docker-compose.dev.yml --profile app up api` → native gRPC `localhost:5269`, gRPC-Web `localhost:5268` |
| Mailpit (caught emails) | http://localhost:8025 |
| psql | `docker compose -f docker-compose.dev.yml exec postgres psql -U estatehub_migrator estatehub_dev` |
| Stop (data kept) | `docker compose -f docker-compose.dev.yml down` |
| Wipe the databases | `docker compose -f docker-compose.dev.yml down -v` |

With PowerShell (`brew install powershell`), `pwsh ./Make.ps1 <target>` wraps the same commands.

**Trying the API:** in Development, `AuthService/DevSignIn` issues a token for any role (it is
disabled everywhere else). Use the token as `authorization: Bearer <token>` metadata, e.g. with
grpcurl or Postman (gRPC reflection is on in Development):

```bash
grpcurl -plaintext -d '{"user_id":"me","role":"super_admin","mfa":true}' localhost:5269 estatehub.auth.v1.AuthService/DevSignIn
```

**Git hooks:** `git config core.hooksPath .githooks` enables the pre-commit hook (gitleaks secret
scan + tests, both through Docker). Emergency bypass: `git commit --no-verify`.

## Time zones

Columns are `timestamptz`; the database time zone is **Africa/Johannesburg**, so every tool shows
SAST (`2026-10-26 14:30:00+02`). C# holds UTC-kind values only because the Npgsql driver requires
it. Names never carry `_utc`.

## House rules (enforced by tests)

- Every table implements `IHasTimestamps` (`created_at`, `updated_at`), set by the DbContext.
- Every entity with an `EstateId` is `ITenantScoped` (or explicitly listed with a reason).
- Every RPC declares permission, ownership and feature decisions; no "pending" escape hatch.
- Estate-scoped requests never carry `estate_id`; the estate comes from the signed token.
- Protos follow Google style: `estatehub.<module>.v1`, `csharp_namespace = "EstateHub.Api"`.
- Every state change writes an `AuditLog` entry.
- Any `.proto` change bumps `EstateHubProtos/version`; every PR bumps `version`.

See `docs/authorization.md` and `docs/tenancy-and-features.md`.

## Production

`docker-compose.prod.yml` runs the `api` and `jobs` containers, hardened (read-only, no
capabilities, non-root chiseled image, **no published ports**) on the server's existing edge
network. Postgres runs separately: prepare it with `docker/postgres/init/01-roles-and-databases.sh`
(set `ESTATEHUB_DATABASES=estatehub`). Copy `.env.prod.example` to `.env.prod`, fill it in,
`chmod 600`. Deploying is done by hand.

## CI

`.github/workflows/ci.yml`: build + tests + 70% diff coverage, version bumps, gitleaks, Semgrep CE,
vulnerable NuGet packages, Trivy image scan. Dependabot opens weekly grouped update PRs.
