#!/usr/bin/env bash
# Creates or upgrades a Pulaski database (D-028), then exits; re-runnable.
#
#   PGHOST, PGPORT, PGUSER, PGPASSWORD  a server login that can create databases and roles (and owns the schema)
#   PULASKI_DATABASE                    database name (default pulaski)
#   PULASKI_APP_LOGIN                   the API's login (default pulaski_api), made a member of pulaski_app
#   PULASKI_APP_PASSWORD                its password (required); set again on every run
#   PULASKI_SEED                        demo = load the demo site, lines and materials into a new database; none (default) = starter data only
#
# A new database gets db/schema-draft.sql, db/app-role.sql, the demo seeds when asked, then the starter units and reasons. An existing
# one is brought up to date by db/migrate.sh, and the starter units and reasons are loaded again (they only add what is missing).
set -euo pipefail

db="${PULASKI_DATABASE:-pulaski}"
login="${PULASKI_APP_LOGIN:-pulaski_api}"
seed="${PULASKI_SEED:-none}"
: "${PULASKI_APP_PASSWORD:?Set PULASKI_APP_PASSWORD to the password the API connects with.}"
here=/pulaski/db
export PGOPTIONS="${PGOPTIONS:--c client_min_messages=warning}"

case "$seed" in demo | none) ;; *) echo "PULASKI_SEED must be demo or none, not '$seed'." >&2; exit 2 ;; esac

for _ in $(seq 1 60); do
    pg_isready -q -d postgres && break
    sleep 1
done
pg_isready -q -d postgres || { echo "PostgreSQL at ${PGHOST:-localhost}:${PGPORT:-5432} didn't answer within a minute." >&2; exit 1; }

psql_on() { psql -X -q -v ON_ERROR_STOP=1 -d "$@"; }

if [[ -z "$(psql_on postgres -tA -c "SELECT 1 FROM pg_database WHERE datname = '$db'")" ]]; then
    echo "Creating database $db"
    psql_on postgres -c "CREATE DATABASE \"$db\""
fi

if [[ "$(psql_on "$db" -tA -c "SELECT to_regclass('public.schema_migration') IS NULL")" == "t" ]]; then
    echo "Loading the schema into $db"
    psql_on "$db" --single-transaction -f "$here/schema-draft.sql"
    psql_on "$db" -f "$here/app-role.sql"
    if [[ "$seed" == demo ]]; then
        echo "Loading the demo site, lines and materials"
        psql_on "$db" --single-transaction -f "$here/seed-demo.sql"
        psql_on "$db" --single-transaction -f "$here/seed-materials.sql"
    fi
else
    echo "Bringing $db up to date"
    bash "$here/migrate.sh" "$db"
fi

psql_on "$db" --single-transaction -f "$here/seed-units.sql"
psql_on "$db" --single-transaction -f "$here/seed-reasons.sql"
# The reporting role (D-129): reads the report views only; logins for BI tools are the DBA's (db/report-role.sql).
psql_on "$db" -f "$here/report-role.sql"

# The API's login: created once, its password set on every run, a member of pulaski_app only (D-012).
psql_on "$db" -v login="$login" -v password="$PULASKI_APP_PASSWORD" -v db="$db" <<'SQL'
SELECT format('CREATE ROLE %I LOGIN', :'login') WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'login') \gexec
SELECT format('ALTER ROLE %I LOGIN PASSWORD %L', :'login', :'password') \gexec
SELECT format('GRANT pulaski_app TO %I', :'login') \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO %I', :'db', :'login') \gexec
SQL

echo "$db is ready; the API connects as $login."
