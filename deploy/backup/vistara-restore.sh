#!/usr/bin/env bash
# Restores a Vistara backup archive into operator-provided targets. Existing
# data is never overwritten unless --force is passed explicitly.
set -euo pipefail

script_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=deploy/backup/common.sh
source "$script_directory/common.sh"

archive=""
target_database=""
target_media=""
force=false
skip_checksums=false

usage() {
  cat <<'EOF'
Usage:
  vistara-restore.sh --archive DIR --target-database PATH|NAME
                     [--target-media DIR] [--force] [--skip-archive-checksums]

The starter profile copies the SQLite database file and expands the media
archive. The PostgreSQL profile restores into an empty target database using
pg_restore and the standard PG* environment variables.

--force permits writing over existing targets and must only be used during a
declared recovery.
EOF
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --archive) archive="${2:-}"; shift 2 ;;
    --target-database) target_database="${2:-}"; shift 2 ;;
    --target-media) target_media="${2:-}"; shift 2 ;;
    --force) force=true; shift ;;
    --skip-archive-checksums) skip_checksums=true; shift ;;
    --help|-h) usage; exit 0 ;;
    *) usage >&2; fail "Unknown argument: $1" ;;
  esac
done

[ -n "$archive" ] || fail "--archive is required."
[ -n "$target_database" ] || fail "--target-database is required."
[ -f "$archive/manifest.env" ] || fail "$archive is not a Vistara backup archive."
[ -f "$archive/SHA256SUMS" ] || fail "$archive is missing SHA256SUMS."

if [ "$skip_checksums" = false ]; then
  verify_recorded_checksums "$archive"
fi

profile="$(manifest_value "$archive" profile)"
database_file="$(manifest_value "$archive" database_file)"
media_file="$(manifest_value "$archive" media_file)"

if [ "$profile" = starter ]; then
  for sidecar in "$target_database-wal" "$target_database-shm"; do
    [ ! -e "$sidecar" ] ||
      fail "Stale SQLite sidecar $sidecar exists; resolve it before restoring."
  done
  if [ -e "$target_database" ] && [ "$force" = false ]; then
    fail "Target database $target_database already exists; pass --force to replace it."
  fi
  mkdir -p "$(dirname "$target_database")"
  cp "$archive/$database_file" "$target_database"
  chmod 0600 "$target_database"
else
  require_command pg_restore
  require_command psql
  existing="$(psql --dbname "$target_database" --no-align --tuples-only --quiet \
    --command "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public';")"
  if [ "$existing" != "0" ] && [ "$force" = false ]; then
    fail "Target database $target_database already contains tables; pass --force to replace it."
  fi
  restore_arguments=(--no-owner --no-privileges --dbname "$target_database")
  if [ "$force" = true ]; then
    restore_arguments+=(--clean --if-exists)
  fi
  pg_restore "${restore_arguments[@]}" "$archive/$database_file"
fi

if [ -n "$target_media" ] && [ "$media_file" != external ]; then
  require_command tar
  if [ "$force" = false ]; then
    require_empty_directory "$target_media" "Target media directory"
  fi
  mkdir -p "$target_media"
  tar --extract --gzip --file "$archive/$media_file" --directory "$target_media"
fi

echo "Restored archive $archive (profile $profile) into $target_database."
