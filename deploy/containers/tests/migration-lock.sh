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
replay_migration=""

# PostgreSQL reports every duplicate schema object with one of these SQLSTATEs.
# They are stable across releases and locales, unlike the message text.
duplicate_object_pattern='(^|[^0-9A-Za-z])(42701|42710|42723|42P04|42P06|42P07)([^0-9A-Za-z]|$)'
duplicate_object_sqlstates='42701, 42710, 42723, 42P04, 42P06, 42P07'

while [ "$#" -gt 0 ]; do
  case "$1" in
    --log-directory) log_directory="${2:-}"; shift 2 ;;
    --concurrency) concurrency="${2:-}"; shift 2 ;;
    --replay-migration) replay_migration="${2:-}"; shift 2 ;;
    --help|-h)
      echo "Usage: migration-lock.sh [--log-directory DIR] [--concurrency N] [--replay-migration ID]"
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
  "$postgres_image" \
  -c log_error_verbosity=verbose >/dev/null

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

# Serialization must not be bought by swallowing errors. The newest migration
# may be data-only, so pin the oldest applied migration instead: it is the one
# that creates the schema, and replaying it over the live objects is guaranteed
# to collide. Every later ledger row goes with it so the replay starts there.
if [ -z "$replay_migration" ]; then
  replay_migration="$(psql_query 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" ASC LIMIT 1;')"
fi
[ -n "$replay_migration" ] || fail "The oldest applied migration could not be read."

replay_ledger_rows="$(psql_query "SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" >= '$replay_migration';")"
[ "$replay_ledger_rows" != "0" ] ||
  fail "The replay probe found no ledger row at or after $replay_migration."

psql_query "DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" >= '$replay_migration';" >/dev/null

"$docker_cli" logs "$postgres_container" \
  > "$log_directory/postgres-before-replay.log" 2>&1 || true
server_log_offset="$(wc -l < "$log_directory/postgres-before-replay.log" | tr -d '[:space:]')"

replay_status=0
run_bundle replay || replay_status=$?
[ "$replay_status" -ne 0 ] ||
  fail "Replaying $replay_migration over existing objects must fail the migration bundle."

# The bundle normally prints the SQLSTATE itself. When it does not, fall back to
# the server log written since the replay started; PostgreSQL runs with verbose
# error output so each entry carries its SQLSTATE.
if ! grep --extended-regexp --quiet "$duplicate_object_pattern" "$log_directory/replay.log"; then
  "$docker_cli" logs "$postgres_container" \
    > "$log_directory/postgres-after-replay.log" 2>&1 || true
  tail -n "+$((server_log_offset + 1))" "$log_directory/postgres-after-replay.log" \
    > "$log_directory/postgres-replay.log"
  grep --extended-regexp --quiet "$duplicate_object_pattern" "$log_directory/postgres-replay.log" ||
    fail "Replaying $replay_migration must report a duplicate object SQLSTATE ($duplicate_object_sqlstates); neither the bundle log nor the server log did."
fi

echo "Migration lock gate passed: $ledger_count migrations applied exactly once across $concurrency concurrent bundles."
