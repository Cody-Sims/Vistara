#!/usr/bin/env bash
# Creates a verifiable Vistara backup archive. The script only reads the live
# instance; it never deletes or rewrites live data.
set -euo pipefail

script_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=deploy/backup/common.sh
source "$script_directory/common.sh"

profile=starter
database=""
media_root=""
output=""
configs=()

usage() {
  cat <<'EOF'
Usage:
  vistara-backup.sh --profile starter --output DIR --database FILE
                    [--media-root DIR] [--config FILE]...
  vistara-backup.sh --profile postgres --output DIR --database NAME
                    [--media-root DIR] [--config FILE]...

The PostgreSQL profile reads connection settings from the standard PGHOST,
PGPORT, PGUSER, and PGPASSWORD environment variables. Never pass a password on
the command line.

Backed-up components: database, original blobs, configuration, audit records,
and deletion tombstones. Derivatives are reproducible and are not archived.
EOF
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --profile) profile="${2:-}"; shift 2 ;;
    --database) database="${2:-}"; shift 2 ;;
    --media-root) media_root="${2:-}"; shift 2 ;;
    --output) output="${2:-}"; shift 2 ;;
    --config) configs+=("${2:-}"); shift 2 ;;
    --help|-h) usage; exit 0 ;;
    *) usage >&2; fail "Unknown argument: $1" ;;
  esac
done

[ -n "$output" ] || fail "--output is required."
[ -n "$database" ] || fail "--database is required."
case "$profile" in
  starter|postgres) ;;
  *) fail "--profile must be starter or postgres." ;;
esac

require_empty_directory "$output" "Backup output directory"
mkdir -p "$output/database"

count_tenants=0
count_users=0
count_assets=0
count_blobs=0
count_audit_events=0
count_deletion_tombstones=0
count_shares=0
count_active_blobs=0
migration_head=""
migration_count=0
database_file=""
database_provider=""

if [ "$profile" = starter ]; then
  require_command sqlite3
  [ -f "$database" ] || fail "SQLite database $database was not found."
  database_provider=sqlite
  database_file="database/vistara.db"
  sqlite3 "$database" ".backup '$output/$database_file'"
  integrity="$(sqlite_query "$output/$database_file" "PRAGMA integrity_check;")"
  [ "$integrity" = "ok" ] || fail "The backed-up SQLite database failed integrity_check."
  for table in "${COUNTED_TABLES[@]}"; do
    if sqlite_table_exists "$output/$database_file" "$table"; then
      printf -v "count_${table}" '%s' \
        "$(sqlite_query "$output/$database_file" "SELECT count(*) FROM \"$table\";")"
    fi
  done
  count_active_blobs="$(sqlite_query "$output/$database_file" \
    "SELECT count(*) FROM blobs WHERE state = 'Active';")"
  migration_head="$(sqlite_query "$output/$database_file" \
    "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\" DESC LIMIT 1;")"
  migration_count="$(sqlite_query "$output/$database_file" \
    "SELECT count(*) FROM \"__EFMigrationsHistory\";")"
else
  require_command pg_dump
  require_command psql
  database_provider=postgresql
  database_file="database/vistara.dump"
  pg_dump \
    --format=custom \
    --no-owner \
    --no-privileges \
    --file "$output/$database_file" \
    --dbname "$database"
  pg_count() {
    psql --dbname "$database" --no-align --tuples-only --quiet --command "$1"
  }
  for table in "${COUNTED_TABLES[@]}"; do
    printf -v "count_${table}" '%s' "$(pg_count "SELECT count(*) FROM \"$table\";")"
  done
  count_active_blobs="$(pg_count "SELECT count(*) FROM blobs WHERE state = 'Active';")"
  migration_head="$(pg_count \
    "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\" DESC LIMIT 1;")"
  migration_count="$(pg_count "SELECT count(*) FROM \"__EFMigrationsHistory\";")"
fi

media_file=external
media_object_count=0
if [ -n "$media_root" ]; then
  require_command tar
  [ -d "$media_root" ] || fail "Media root $media_root was not found."
  mkdir -p "$output/media"
  media_file="media/media.tar.gz"
  tar --create --gzip --file "$output/$media_file" --directory "$media_root" .
  media_object_count="$(find "$media_root/.vistara/objects" -type f -name '*.blob' 2>/dev/null | wc -l | tr -d ' ')"
fi

if [ "${#configs[@]}" -gt 0 ]; then
  mkdir -p "$output/config"
  for config in "${configs[@]}"; do
    [ -f "$config" ] || fail "Configuration file $config was not found."
    target="$output/config/$(basename "$config")"
    [ ! -e "$target" ] || fail "Duplicate configuration file name: $(basename "$config")."
    cp "$config" "$target"
    chmod 0600 "$target"
  done
fi

created_at="$(utc_timestamp)"

{
  echo "manifest_version=1"
  echo "profile=$profile"
  echo "created_at_utc=$created_at"
  echo "database_provider=$database_provider"
  echo "database_file=$database_file"
  echo "media_file=$media_file"
  echo "media_object_count=$media_object_count"
  echo "migration_head=$migration_head"
  echo "migration_count=$migration_count"
  echo "count_tenants=$count_tenants"
  echo "count_users=$count_users"
  echo "count_assets=$count_assets"
  echo "count_blobs=$count_blobs"
  echo "count_active_blobs=$count_active_blobs"
  echo "count_audit_events=$count_audit_events"
  echo "count_deletion_tombstones=$count_deletion_tombstones"
  echo "count_shares=$count_shares"
} > "$output/manifest.env"

{
  echo '{'
  echo '  "manifestVersion": 1,'
  echo "  \"profile\": \"$(json_escape "$profile")\","
  echo "  \"createdAtUtc\": \"$created_at\","
  echo '  "database": {'
  echo "    \"provider\": \"$database_provider\","
  echo "    \"file\": \"$database_file\","
  echo "    \"migrationHead\": \"$(json_escape "$migration_head")\","
  echo "    \"migrationCount\": $migration_count"
  echo '  },'
  echo '  "media": {'
  echo "    \"file\": \"$(json_escape "$media_file")\","
  echo "    \"objectCount\": $media_object_count"
  echo '  },'
  echo '  "counts": {'
  echo "    \"tenants\": $count_tenants,"
  echo "    \"users\": $count_users,"
  echo "    \"assets\": $count_assets,"
  echo "    \"blobs\": $count_blobs,"
  echo "    \"activeBlobs\": $count_active_blobs,"
  echo "    \"auditEvents\": $count_audit_events,"
  echo "    \"deletionTombstones\": $count_deletion_tombstones,"
  echo "    \"shares\": $count_shares"
  echo '  }'
  echo '}'
} > "$output/manifest.json"

(
  cd "$output"
  find . -type f ! -name SHA256SUMS -print0 |
    LC_ALL=C sort -z |
    while IFS= read -r -d '' path; do
      printf '%s  %s\n' "$(checksum_file "$path")" "${path#./}"
    done > SHA256SUMS
)

echo "Backup archive written to $output (profile $profile, migration head ${migration_head:-none})."
