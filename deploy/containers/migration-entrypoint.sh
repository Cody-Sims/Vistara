#!/usr/bin/env sh
set -eu

connection_string="${ConnectionStrings__Vistara:-${Persistence__ConnectionString:-}}"
if [ -z "$connection_string" ]; then
  echo "ConnectionStrings__Vistara is required." >&2
  exit 64
fi

case "${MIGRATION_PROVIDER:-}" in
  Sqlite|sqlite)
    bundle="/app/vistara-migrate-sqlite"
    ;;
  PostgreSql|postgresql|Postgres|postgres)
    bundle="/app/vistara-migrate-postgres"
    ;;
  *)
    echo "MIGRATION_PROVIDER must be Sqlite or PostgreSql." >&2
    exit 64
    ;;
esac

exec "$bundle" --connection "$connection_string"
