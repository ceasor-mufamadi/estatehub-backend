#!/bin/bash
# Runs once, on the first start of an empty Postgres volume (docker-entrypoint-initdb.d).
# Creates the two EstateHub roles and the dev + test databases (SECURITY.md layer 5):
#   estatehub_migrator  owns the schema, runs migrations. Not a superuser.
#   estatehub_app       what the API connects as: row read/write only, no DDL, can't disable RLS.
# Superusers bypass row-level security, so neither role may ever be one.
# The same script (minus the dev/test databases) is used to prepare the production server: see README.
set -euo pipefail

: "${ESTATEHUB_MIGRATOR_PASSWORD:?set ESTATEHUB_MIGRATOR_PASSWORD}"
: "${ESTATEHUB_APP_PASSWORD:?set ESTATEHUB_APP_PASSWORD}"
DATABASES="${ESTATEHUB_DATABASES:-estatehub_dev estatehub_test}"

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres <<SQL
CREATE ROLE estatehub_migrator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS PASSWORD '${ESTATEHUB_MIGRATOR_PASSWORD}';
CREATE ROLE estatehub_app      LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS PASSWORD '${ESTATEHUB_APP_PASSWORD}';
SQL

for db in $DATABASES; do
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres <<SQL
CREATE DATABASE ${db} OWNER estatehub_migrator;
-- Everyone looking at this database sees South African time (timestamptz shown as +02).
ALTER DATABASE ${db} SET timezone TO 'Africa/Johannesburg';
REVOKE ALL ON DATABASE ${db} FROM PUBLIC;
GRANT CONNECT ON DATABASE ${db} TO estatehub_migrator, estatehub_app;
SQL

  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$db" <<SQL
ALTER SCHEMA public OWNER TO estatehub_migrator;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO estatehub_app;
-- Every table/sequence the migrator creates is usable (rows only) by the app.
-- Tables that must be stricter (audit_logs: INSERT/SELECT only) revoke in their migration.
ALTER DEFAULT PRIVILEGES FOR ROLE estatehub_migrator IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO estatehub_app;
ALTER DEFAULT PRIVILEGES FOR ROLE estatehub_migrator IN SCHEMA public
  GRANT USAGE, SELECT ON SEQUENCES TO estatehub_app;
SQL
done

echo "EstateHub roles and databases ready: ${DATABASES}"
