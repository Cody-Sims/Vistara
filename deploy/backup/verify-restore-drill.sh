#!/usr/bin/env bash
# Runs a non-destructive restore drill: the archive is restored into an
# isolated working directory and verified for schema, tenant counts, blob
# references, checksums, and authorization scoping. Nothing outside the
# working directory is written, and nothing is ever deleted.
set -euo pipefail

script_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=deploy/backup/common.sh
source "$script_directory/common.sh"

archive=""
workdir=""
scratch_database=""
rto_minutes=240
skip_checksums=false

usage() {
  cat <<'EOF'
Usage:
  verify-restore-drill.sh --archive DIR --workdir DIR
                          [--rto-minutes N] [--scratch-database NAME]
                          [--skip-archive-checksums]

The working directory must be absent or empty. A PostgreSQL archive requires
--scratch-database naming an empty, disposable database.
EOF
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --archive) archive="${2:-}"; shift 2 ;;
    --workdir) workdir="${2:-}"; shift 2 ;;
    --scratch-database) scratch_database="${2:-}"; shift 2 ;;
    --rto-minutes) rto_minutes="${2:-}"; shift 2 ;;
    --skip-archive-checksums) skip_checksums=true; shift ;;
    --help|-h) usage; exit 0 ;;
    *) usage >&2; fail "Unknown argument: $1" ;;
  esac
done

[ -n "$archive" ] || fail "--archive is required."
[ -n "$workdir" ] || fail "--workdir is required."
[ -f "$archive/manifest.env" ] || fail "$archive is not a Vistara backup archive."
[[ "$rto_minutes" =~ ^[0-9]+$ ]] || fail "--rto-minutes must be a whole number."

require_empty_directory "$workdir" "Drill working directory"
mkdir -p "$workdir"

started_at="$(utc_timestamp)"
start_seconds="$SECONDS"

profile="$(manifest_value "$archive" profile)"
media_file="$(manifest_value "$archive" media_file)"
restored_media="$workdir/media"
restored_database="$workdir/database/vistara.db"

restore_arguments=(--archive "$archive" --target-media "$restored_media")
if [ "$profile" = starter ]; then
  require_command sqlite3
  restore_arguments+=(--target-database "$restored_database")
else
  [ -n "$scratch_database" ] ||
    fail "--scratch-database is required for a PostgreSQL archive."
  require_command psql
  restore_arguments+=(--target-database "$scratch_database")
fi
if [ "$skip_checksums" = true ]; then
  restore_arguments+=(--skip-archive-checksums)
fi

"$script_directory/vistara-restore.sh" "${restore_arguments[@]}" >/dev/null

query() {
  if [ "$profile" = starter ]; then
    sqlite_query "$restored_database" "$1"
  else
    psql --dbname "$scratch_database" --no-align --tuples-only --quiet --command "$1"
  fi
}

table_exists() {
  if [ "$profile" = starter ]; then
    sqlite_table_exists "$restored_database" "$1"
  else
    [ "$(query "SELECT count(*) FROM information_schema.tables
      WHERE table_schema = 'public' AND table_name = '$1';")" = "1" ]
  fi
}

if [ "$profile" = starter ]; then
  integrity="$(query "PRAGMA integrity_check;")"
  [ "$integrity" = "ok" ] ||
    fail "Restored database failed integrity_check: $integrity"
fi

for table in "${CORE_TABLES[@]}"; do
  table_exists "$table" ||
    fail "Restored schema is missing the required table $table."
done

expected_head="$(manifest_value "$archive" migration_head)"
actual_head="$(query "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\"
  ORDER BY \"MigrationId\" DESC LIMIT 1;" | tr -d '[:space:]')"
[ "$actual_head" = "$expected_head" ] ||
  fail "Restored migration head $actual_head does not match the recorded $expected_head."

expected_migrations="$(manifest_value "$archive" migration_count)"
actual_migrations="$(query "SELECT count(*) FROM \"__EFMigrationsHistory\";" | tr -d '[:space:]')"
[ "$actual_migrations" = "$expected_migrations" ] ||
  fail "Restored migration ledger has $actual_migrations rows, expected $expected_migrations."

declare -a count_report=()
for table in "${COUNTED_TABLES[@]}"; do
  expected="$(manifest_value "$archive" "count_${table}")"
  actual="$(query "SELECT count(*) FROM \"$table\";" | tr -d '[:space:]')"
  [ "$actual" = "$expected" ] ||
    fail "Restored $table count $actual does not match the recorded $expected."
  count_report+=("\"$table\": $actual")
done

for table in "${AUTHORIZATION_TABLES[@]}"; do
  orphans="$(query "SELECT count(*) FROM \"$table\" AS scoped
    WHERE scoped.tenant_id IS NULL
      OR NOT EXISTS (SELECT 1 FROM tenants WHERE tenants.id = scoped.tenant_id);" |
    tr -d '[:space:]')"
  [ "$orphans" = "0" ] ||
    fail "Restored authorization table $table has $orphans rows without a restored tenant."
done

verified_blobs=0
if [ "$media_file" = external ]; then
  echo "Object storage is external; blob payloads were not part of this archive."
else
  objects_root="$restored_media/.vistara/objects"
  [ -d "$objects_root" ] || fail "Restored media is missing the object store root."
  while IFS=$'\t' read -r object_key expected_sha size_bytes; do
    [ -n "$object_key" ] || continue
    key_hash="$(printf '%s' "$object_key" | checksum_stdin)"
    blob_path="$objects_root/${key_hash:0:2}/${key_hash:2:2}/${key_hash}.blob"
    [ -f "$blob_path" ] ||
      fail "Restored blob for $object_key is missing from the object store."
    file_bytes="$(wc -c < "$blob_path" | tr -d ' ')"
    [ "$file_bytes" -gt "$((size_bytes + 16))" ] ||
      fail "Restored blob for $object_key is truncated."
    magic="$(tail -c 16 "$blob_path" | head -c 8)"
    [ "$magic" = "VISTAR01" ] ||
      fail "Restored blob for $object_key has an invalid descriptor footer."
    actual_sha="$(head -c "$size_bytes" "$blob_path" | checksum_stdin)"
    [ "$actual_sha" = "$expected_sha" ] ||
      fail "Restored blob for $object_key failed its SHA-256 checksum."
    verified_blobs=$((verified_blobs + 1))
  done < <(
    if [ "$profile" = starter ]; then
      sqlite3 -batch -noheader -separator $'\t' "$restored_database" \
        "SELECT object_key, sha256, size_bytes FROM blobs WHERE state = 'Active' ORDER BY object_key;"
    else
      psql --dbname "$scratch_database" --no-align --tuples-only --quiet \
        --field-separator=$'\t' \
        --command "SELECT object_key, sha256, size_bytes FROM blobs WHERE state = 'Active' ORDER BY object_key;"
    fi
  )

  expected_active="$(manifest_value "$archive" count_active_blobs)"
  [ "$verified_blobs" = "$expected_active" ] ||
    fail "Verified $verified_blobs active blobs, expected $expected_active."
fi

elapsed_seconds=$((SECONDS - start_seconds))
budget_seconds=$((rto_minutes * 60))
if [ "$elapsed_seconds" -gt "$budget_seconds" ]; then
  fail "Restore drill took ${elapsed_seconds}s and exceeded the ${rto_minutes}-minute RTO budget."
fi

{
  echo '{'
  echo '  "result": "passed",'
  echo "  \"profile\": \"$(json_escape "$profile")\","
  echo "  \"archive\": \"$(json_escape "$archive")\","
  echo "  \"startedAtUtc\": \"$started_at\","
  echo "  \"completedAtUtc\": \"$(utc_timestamp)\","
  echo "  \"elapsedSeconds\": $elapsed_seconds,"
  echo "  \"rtoBudgetMinutes\": $rto_minutes,"
  echo '  "checks": {'
  echo "    \"integrity\": true,"
  echo "    \"schemaTables\": ${#CORE_TABLES[@]},"
  echo "    \"migrationHead\": \"$(json_escape "$actual_head")\","
  echo "    \"blobReferences\": $verified_blobs,"
  echo "    \"authorizationScoped\": true"
  echo '  },'
  echo '  "counts": {'
  printf '    %s\n' "$(IFS=,; echo "${count_report[*]}" | sed 's/,/,\n    /g')"
  echo '  }'
  echo '}'
} > "$workdir/drill-report.json"

echo "Restore drill passed in ${elapsed_seconds}s (${verified_blobs} blob payloads verified)."
echo "Report: $workdir/drill-report.json"
