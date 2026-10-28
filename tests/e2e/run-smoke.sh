#!/usr/bin/env bash
# Smoke test against a real Postgres (see smoke.py). Docker Desktop must be open and the dev stack up
# (docker compose -f docker-compose.dev.yml up -d). Uses the separate estatehub_test database and a
# temporary API on 127.0.0.1:5368, so the dev database, your API and your browser sessions are untouched.
# The script makes many anonymous calls from one IP, so the temporary API's per-IP limit is raised.
#   tests/e2e/run-smoke.sh
set -euo pipefail
cd "$(dirname "$0")/../.."
# shellcheck disable=SC1091
source .env

compose=(docker compose -f docker-compose.dev.yml)
db="Host=postgres;Database=estatehub_test;Timezone=Africa/Johannesburg"
name=estatehub-smoke

# Every run starts from an empty schema: all migrations down (which also proves each Down works),
# then up again. Otherwise rows from earlier runs pile up (e.g. the hourly code caps per IP).
echo "== build, then reset estatehub_test (migrations down to 0 and back up)"
ef="dotnet ef database update --project src/EstateHub.Infrastructure --startup-project src/EstateHub.Infrastructure"
"${compose[@]}" run --rm -e "ConnectionStrings__Migrator=$db;Username=estatehub_migrator;Password=$ESTATEHUB_MIGRATOR_PASSWORD" sdk \
  sh -c "dotnet tool restore >/dev/null && dotnet build -v q -nologo src/EstateHub.Api >/dev/null && $ef 0 >/dev/null && $ef | grep -E 'Applying|Done'"

echo "== start temporary API on :5368"
"${compose[@]}" run -d --rm --name "$name" -p 127.0.0.1:5368:5268 \
  -e ASPNETCORE_ENVIRONMENT=Development \
  -e Kestrel__Endpoints__Http__Url=http://0.0.0.0:5268 -e Kestrel__Endpoints__Grpc__Url=http://0.0.0.0:5269 \
  -e "ConnectionStrings__Postgres=$db;Username=estatehub_app;Password=$ESTATEHUB_APP_PASSWORD" \
  -e Messaging__SmtpHost=mailpit -e Messaging__SmtpPort=1025 -e Messaging__SmsProvider=mailpit \
  -e Auth__RefreshCookieSecure=false -e Logging__LogLevel__Microsoft.EntityFrameworkCore=Warning \
  -e RateLimits__AnonymousPerMinute=1000 \
  sdk dotnet run --no-build --project src/EstateHub.Api --no-launch-profile >/dev/null
trap 'docker stop "$name" >/dev/null 2>&1 || true' EXIT

for _ in $(seq 1 60); do
  curl -s http://127.0.0.1:5368/ | grep -q running && break
  sleep 1
done

echo "== smoke test"
status=0
python3 -I tests/e2e/smoke.py || status=$?
[ "$status" = "0" ] || echo "FAIL smoke.py exited with $status"

# The ledger is append-only for the app's database role: corrections are reversals, never edits (slice 4.1).
denied=$("${compose[@]}" exec -T -e PGPASSWORD="$ESTATEHUB_APP_PASSWORD" postgres \
  psql -h localhost -U estatehub_app -d estatehub_test -c "UPDATE ledger_entries SET debit = debit; DELETE FROM ledger_entries;" 2>&1 || true)
if echo "$denied" | grep -q "permission denied for table ledger_entries"; then
  echo "PASS the app's database role can't edit or delete ledger entries"
else
  echo "FAIL ledger_entries is editable by estatehub_app: $denied"
  status=1
fi

# Logs must never contain codes, links or tokens (SECURITY.md layer 7).
leaks=$(docker logs "$name" 2>&1 | grep -ciE 'is your EstateHub code|/invite#|estatehub_refresh=|eyJ[A-Za-z0-9_-]{20,}' || true)
if [ "$leaks" != "0" ]; then
  echo "FAIL logs contain $leaks code/link/token mention(s)"
  status=1
else
  echo "PASS logs contain no codes, links or tokens"
fi
exit $status
