#!/usr/bin/env bash
# Proves that concurrently started migration bundles serialize safely against
# one PostgreSQL database, apply every migration exactly once, and stay
# idempotent on a repeat run. Only containers and networks created here are
# removed; no operator data is touched.
set -euo pipefail

docker_cli="${DOCKER:-docker}"
migration_image="${MIGRATION_IMAGE:-vistara-migrations:ci}"
postgres_image="${POSTGRES_IMAGE:-postgres:18.0-bookworm@sha256:3f55f8895c4ed50603e2fbdfc72fffeeaba3173321fee5cb825bbbeb30d9d854}"
log_directory="artifacts/migration-lock"

while [ "$#" -gt 0 ]; do
  case "$1" in
    --log-directory) log_directory="${2:-}"; shift 2 ;;
    --help|-h)
      echo "Usage: migration-lock.sh [--log-directory DIR]"
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 64
      ;;
  esac
done

suffix="$$-$(date +%s)"
network="vistara-migration-lock-net-$suffix"
postgres_container="vistara-migration-lock-db-$suffix"
password="$(head -c 24 /dev/urandom | base64 | tr -d '/+=' | cut -c1-24)"
connection="Host=$postgres_container;Port=5432;Database=vistara;Username=vistara_migrator;Password=$password;SSL Mode=Disable;Include Error Detail=false"

mkdir -p "$log_directory"

cleanup() {
  "$docker_cli" rm --force "$postgres_container" >/dev/null 2>&1 || true
  "$docker_cli" network rm "$network" >/dev/null 2>&1 || true
}
trap cleanup EXIT

fail() {
  echo "$*" >&2
  exit 1
}

psql_query() {
  "$docker_cli" exec \
    --env "PGPASSWORD=$password" \
    "$postgres_container" \
    psql \
    --username vistara_migrator \
    --dbname vistara \
    --no-align \
    --tuples-only \
    --quiet \
    --command "$1" | tr -d '[:space:]'
}

run_bundle() {
  local name="$1"
  "$docker_cli" run --rm \
    --network "$network" \
    --env MIGRATION_PROVIDER=PostgreSql \
    --env "ConnectionStrings__Vistara=$connection" \
    "$migration_image" \
    > "$log_directory/$name.log" 2>&1
}

"$docker_cli" network create "$network" >/dev/null

"$docker_cli" run --detach \
  --name "$postgres_container" \
  --network "$network" \
  --env POSTGRES_USER=vistara_migrator \
  --env "POSTGRES_PASSWORD=$password" \
  --env POSTGRES_DB=vistara \
  "$postgres_image" >/dev/null

for attempt in $(seq 1 60); do
  if "$docker_cli" exec "$postgres_container" \
    pg_isready --username vistara_migrator --dbname vistara >/dev/null 2>&1; then
    break
  fi
  if [ "$attempt" = 60 ]; then
    "$docker_cli" logs "$postgres_container" >&2 || true
    fail "PostgreSQL did not become ready for the migration lock gate."
  fi
  sleep 1
done

run_bundle first &
first_pid=$!
run_bundle second &
second_pid=$!

first_status=0
second_status=0
wait "$first_pid" || first_status=$?
wait "$second_pid" || second_status=$?

if [ "$first_status" -ne 0 ] || [ "$second_status" -ne 0 ]; then
  cat "$log_directory/first.log" "$log_directory/second.log" >&2 || true
  fail "Concurrent migration bundles must both succeed; exits were $first_status and $second_status."
fi

ledger_count="$(psql_query 'SELECT count(*) FROM "__EFMigrationsHistory";')"
[ -n "$ledger_count" ] || fail "The migration ledger could not be read."
[ "$ledger_count" != "0" ] || fail "No migrations were applied by the migration bundle."

duplicates="$(psql_query 'SELECT count(*) FROM (SELECT "MigrationId" FROM "__EFMigrationsHistory" GROUP BY "MigrationId" HAVING count(*) > 1) AS repeated;')"
[ "$duplicates" = "0" ] ||
  fail "The migration ledger contains $duplicates duplicate migration identifiers."

repeat_status=0
run_bundle repeat || repeat_status=$?
if [ "$repeat_status" -ne 0 ]; then
  cat "$log_directory/repeat.log" >&2 || true
  fail "The repeat migration bundle run failed with exit $repeat_status."
fi

repeat_ledger_count="$(psql_query 'SELECT count(*) FROM "__EFMigrationsHistory";')"
[ "$repeat_ledger_count" = "$ledger_count" ] ||
  fail "The migration bundle is not idempotent: ledger moved from $ledger_count to $repeat_ledger_count."

echo "Migration lock gate passed: $ledger_count migrations applied exactly once under concurrency."
