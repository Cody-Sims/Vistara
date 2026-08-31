#!/usr/bin/env bash
# Proves that concurrently started migration bundles serialize safely against
# one PostgreSQL database, apply every migration exactly once, stay idempotent
# on a repeat run, load every native library the bundle needs, and still fail
# loudly on a genuine schema error. Only containers and networks created here
# are removed; no operator data is touched.
set -euo pipefail

docker_cli="${DOCKER:-docker}"
migration_image="${MIGRATION_IMAGE:-vistara-migrations:ci}"
postgres_image="${POSTGRES_IMAGE:-postgres:18.0-bookworm@sha256:3f55f8895c4ed50603e2fbdfc72fffeeaba3173321fee5cb825bbbeb30d9d854}"
log_directory="artifacts/migration-lock"
concurrency="${MIGRATION_LOCK_CONCURRENCY:-3}"

while [ "$#" -gt 0 ]; do
  case "$1" in
    --log-directory) log_directory="${2:-}"; shift 2 ;;
    --concurrency) concurrency="${2:-}"; shift 2 ;;
    --help|-h)
      echo "Usage: migration-lock.sh [--log-directory DIR] [--concurrency N]"
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 64
      ;;
  esac
done

case "$concurrency" in
  ''|*[!0-9]*)
    echo "--concurrency must be a positive integer." >&2
    exit 64
    ;;
esac
if [ "$concurrency" -lt 2 ]; then
  echo "--concurrency must be at least 2 to exercise the migration lock." >&2
  exit 64
fi

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

# The migration image must resolve every native library the bundle loads at
# start-up; a missing one silently degrades authentication support.
assert_no_missing_libraries() {
  local name="$1"
  local offenders
  offenders="$(grep --extended-regexp \
    'cannot open shared object file|Cannot load library' \
    "$log_directory/$name.log" || true)"
  [ -z "$offenders" ] ||
    fail "The $name migration bundle log reports a missing native library: $offenders"
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

concurrent_names=()
concurrent_pids=()
for index in $(seq 1 "$concurrency"); do
  name="concurrent-$index"
  run_bundle "$name" &
  concurrent_names+=("$name")
  concurrent_pids+=("$!")
done

concurrent_failures=""
for position in "${!concurrent_pids[@]}"; do
  status=0
  wait "${concurrent_pids[$position]}" || status=$?
  if [ "$status" -ne 0 ]; then
    concurrent_failures="$concurrent_failures ${concurrent_names[$position]}=$status"
  fi
done

if [ -n "$concurrent_failures" ]; then
  for name in "${concurrent_names[@]}"; do
    cat "$log_directory/$name.log" >&2 || true
  done
  fail "Concurrent migration bundles must all succeed; failures were:$concurrent_failures."
fi

for name in "${concurrent_names[@]}"; do
  assert_no_missing_libraries "$name"
done

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
assert_no_missing_libraries repeat

repeat_ledger_count="$(psql_query 'SELECT count(*) FROM "__EFMigrationsHistory";')"
[ "$repeat_ledger_count" = "$ledger_count" ] ||
  fail "The migration bundle is not idempotent: ledger moved from $ledger_count to $repeat_ledger_count."

# Serialization must not be bought by swallowing errors: forcing the newest
# migration to be replayed over its own objects has to fail the bundle.
replayed_migration="$(psql_query 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1;')"
[ -n "$replayed_migration" ] || fail "The newest applied migration could not be read."
psql_query "DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '$replayed_migration';" >/dev/null

replay_status=0
run_bundle replay || replay_status=$?
[ "$replay_status" -ne 0 ] ||
  fail "Replaying $replayed_migration over existing objects must fail the migration bundle."
grep --quiet "already exists" "$log_directory/replay.log" ||
  fail "Replaying $replayed_migration must surface the underlying PostgreSQL error."

echo "Migration lock gate passed: $ledger_count migrations applied exactly once across $concurrency concurrent bundles."
