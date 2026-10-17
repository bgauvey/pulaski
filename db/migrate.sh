#!/usr/bin/env bash
# Brings an existing Pulaski database up to date (D-028).
#
#   db/migrate.sh [database]        default: pulaski
#
# Runs each db/migrations/NNNN_*.sql not yet recorded in public.schema_migration, in name order,
# each in its own transaction together with the row that records it, then re-runs
# db/app-role.sql for the grants. A fresh database loaded from db/schema-draft.sql already
# records every migration, so this does nothing there. psql connects with the usual PG*
# environment variables.
set -euo pipefail

db="${1:-pulaski}"
here="$(cd "$(dirname "$0")" && pwd)"
psql_db=(psql -X -q -d "$db" -v ON_ERROR_STOP=1)

"${psql_db[@]}" -c "CREATE TABLE IF NOT EXISTS public.schema_migration (
    version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())"

applied=0
for file in "$here"/migrations/[0-9][0-9][0-9][0-9]_*.sql; do
    version="$(basename "$file" .sql)"
    done_already="$("${psql_db[@]}" -tA -c "SELECT 1 FROM public.schema_migration WHERE version = '$version'")"
    if [[ -n "$done_already" ]]; then
        continue
    fi
    echo "Applying $version"
    "${psql_db[@]}" --single-transaction -f "$file" \
        -c "INSERT INTO public.schema_migration (version) VALUES ('$version')"
    applied=$((applied + 1))
done

"${psql_db[@]}" -f "$here/app-role.sql"
echo "$db is up to date ($applied applied)."
