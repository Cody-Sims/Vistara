#!/usr/bin/env bash
# Shared helpers for the Vistara backup, restore, and drill scripts.
# shellcheck shell=bash

CORE_TABLES=(
  __EFMigrationsHistory
  api_keys
  assets
  audit_events
  blobs
  deletion_tombstones
  resource_grants
  shares
  tenant_memberships
  tenants
  users
)

AUTHORIZATION_TABLES=(api_keys resource_grants tenant_memberships)

COUNTED_TABLES=(
  tenants
  users
  assets
  blobs
  audit_events
  deletion_tombstones
  shares
)

fail() {
  echo "$*" >&2
  exit 1
}

require_command() {
  local command_name="$1"
  command -v "$command_name" >/dev/null 2>&1 ||
    fail "$command_name is required but was not found on PATH."
}

checksum_file() {
  local path="$1"
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$path" | cut -d' ' -f1
  else
    shasum -a 256 "$path" | cut -d' ' -f1
  fi
}

checksum_stdin() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum | cut -d' ' -f1
  else
    shasum -a 256 | cut -d' ' -f1
  fi
}

verify_recorded_checksums() {
  local directory="$1"
  (
    cd "$directory"
    if command -v sha256sum >/dev/null 2>&1; then
      sha256sum --check --quiet SHA256SUMS
    else
      shasum -a 256 --check --status SHA256SUMS
    fi
  ) || fail "Archive checksum verification failed for $directory."
}

# Fails when the path exists and contains entries. Never deletes anything.
require_empty_directory() {
  local path="$1"
  local label="$2"
  if [ -e "$path" ] && [ ! -d "$path" ]; then
    fail "$label $path already exists and is not a directory."
  fi
  if [ -d "$path" ] && [ -n "$(ls -A "$path" 2>/dev/null)" ]; then
    fail "$label $path is not empty; refusing to overwrite existing data."
  fi
}

manifest_value() {
  local archive="$1"
  local key="$2"
  local line
  line="$(grep -m1 -E "^${key}=" "$archive/manifest.env" || true)"
  [ -n "$line" ] || fail "manifest.env is missing $key."
  printf '%s' "${line#*=}"
}

utc_timestamp() {
  date -u +%Y-%m-%dT%H:%M:%SZ
}

json_escape() {
  printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g'
}

sqlite_query() {
  local database="$1"
  local sql="$2"
  sqlite3 -batch -noheader "$database" "$sql"
}

sqlite_table_exists() {
  local database="$1"
  local table="$2"
  local found
  found="$(sqlite_query "$database" \
    "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='${table}';")"
  [ "$found" = "1" ]
}
