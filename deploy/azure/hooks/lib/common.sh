#!/usr/bin/env bash
# shellcheck shell=bash
# shellcheck disable=SC2034  # constants and exit codes below are the sourced interface.
#
# Shared helpers for the Vistara hosted Azure bootstrap. Sourced by
# `deploy/azure/up.sh`, `deploy/azure/down.sh`, and every hook `azd` runs.
#
# Two rules govern everything in this file:
#
#   1. No secret ever reaches a command line, an environment variable, an
#      `azd` environment value, or standard output. Secrets travel as 0600
#      files that are removed on exit.
#   2. Every step is safe to run again. The wrapper reruns hooks on its second
#      provisioning pass and an operator reruns `up.sh` after any failure, so a
#      step that is already done must detect that and return without acting.

set -euo pipefail

# Exit codes, frozen by the hosted bootstrap specification. `up.sh` maps a
# generic `azd` failure back onto these through the recorded failure code.
VISTARA_EXIT_USAGE=64
VISTARA_EXIT_MISSING_TOOL=69
VISTARA_EXIT_PROVISION=70
VISTARA_EXIT_MIGRATION=71
VISTARA_EXIT_HEALTH=75
VISTARA_EXIT_PERMISSION=77

# Minimum tool versions. The Bicep pin is the compiler CI installs and the one
# the committed templates were written against; an older compiler above the
# floor still builds them but has not been validated by the repository gates.
VISTARA_MINIMUM_AZ_VERSION=${VISTARA_MINIMUM_AZ_VERSION:-2.89.1}
VISTARA_MINIMUM_AZD_VERSION=${VISTARA_MINIMUM_AZD_VERSION:-1.32.0}
VISTARA_MINIMUM_BICEP_VERSION=${VISTARA_MINIMUM_BICEP_VERSION:-0.36.1}
VISTARA_PINNED_BICEP_VERSION=${VISTARA_PINNED_BICEP_VERSION:-0.46.1}

# Names the templates fix. Changing one here without changing the Bicep breaks
# the byte comparisons the verification hooks make.
VISTARA_OIDC_CALLBACK_PATH='/api/v1/auth/oidc/entra/callback'
VISTARA_OIDC_SIGNED_OUT_PATH='/api/v1/auth/oidc/entra/signed-out'
VISTARA_FEDERATED_CREDENTIAL_NAME='api-managed-identity'
VISTARA_FEDERATED_CREDENTIAL_AUDIENCE='api://AzureADTokenExchange'
VISTARA_API_KEY_PEPPER_SECRET_NAME='api-key-pepper'
VISTARA_FIREWALL_RULE_NAME='vistara-bootstrap-operator'
VISTARA_KEY_VAULT_OPERATOR_ROLE='Key Vault Secrets Officer'

VISTARA_AZURE_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)

# ---------------------------------------------------------------------------
# Output. Everything diagnostic goes to stderr so a function can still return a
# value on stdout.
# ---------------------------------------------------------------------------

vistara_log() {
  printf '%s\n' "$*" >&2
}

vistara_step() {
  printf '\n==> %s\n' "$*" >&2
}

vistara_warn() {
  printf 'warning: %s\n' "$*" >&2
}

vistara_error() {
  printf 'error: %s\n' "$*" >&2
}

# Records the exit code for `up.sh`. `azd` collapses every hook failure into
# its own generic exit status, so the specified taxonomy would be lost without
# this file.
vistara_record_failure() {
  local directory
  directory=$(vistara_run_dir) || return 0
  printf '%s\n' "$1" >"${directory}/last-error-code" 2>/dev/null || true
}

vistara_clear_failure() {
  local directory
  directory=$(vistara_run_dir) || return 0
  rm -f "${directory}/last-error-code" 2>/dev/null || true
}

# vistara_die <exit code> <message...>
vistara_die() {
  local code=$1
  shift
  vistara_error "$*"
  vistara_record_failure "$code"
  exit "$code"
}

# ---------------------------------------------------------------------------
# Tooling
# ---------------------------------------------------------------------------

vistara_have_command() {
  command -v "$1" >/dev/null 2>&1
}

vistara_require_command() {
  local name=$1
  local hint=${2:-}
  if vistara_have_command "$name"; then
    return 0
  fi
  vistara_die "$VISTARA_EXIT_MISSING_TOOL" "required tool '${name}' was not found on PATH. ${hint}"
}

# vistara_version_at_least <have> <want>; tolerates suffixes such as
# "1.32.0-beta.1" by comparing the leading dotted numbers only.
vistara_version_at_least() {
  awk -v have="$1" -v want="$2" '
    function part(value, index_,   pieces, count) {
      count = split(value, pieces, /\./)
      return (index_ <= count ? pieces[index_] + 0 : 0)
    }
    BEGIN {
      sub(/^[^0-9]*/, "", have)
      sub(/^[^0-9]*/, "", want)
      sub(/[^0-9.].*$/, "", have)
      sub(/[^0-9.].*$/, "", want)
      for (i = 1; i <= 3; i++) {
        h = part(have, i)
        w = part(want, i)
        if (h > w) { exit 0 }
        if (h < w) { exit 1 }
      }
      exit 0
    }'
}

# Extracts the first dotted version number from a tool's banner.
vistara_extract_version() {
  sed -n 's/.*[^0-9]\{0,\}\([0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*\).*/\1/p' | head -n 1
}

# ---------------------------------------------------------------------------
# Values and validation
# ---------------------------------------------------------------------------

# Directory GUIDs are case-insensitive but are written both ways, so every
# comparison of one against another is made on a single canonical form.
vistara_lowercase() {
  printf '%s' "${1:-}" | tr '[:upper:]' '[:lower:]'
}

vistara_is_guid() {
  local pattern='^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
  [[ ${1:-} =~ $pattern ]]
}

vistara_require_guid() {
  local value=${1:-}
  local label=$2
  if ! vistara_is_guid "$value"; then
    vistara_die "$VISTARA_EXIT_USAGE" "${label} must be a GUID, got '${value}'."
  fi
}

vistara_is_digest() {
  local pattern='^sha256:[0-9a-f]{64}$'
  [[ ${1:-} =~ $pattern ]]
}

# An image reference is only acceptable when it pins a digest. A tag can be
# repointed after the template was reviewed, so the templates never see one.
vistara_is_digest_reference() {
  local pattern='^[A-Za-z0-9./_:-]+@sha256:[0-9a-f]{64}$'
  [[ ${1:-} =~ $pattern ]]
}

vistara_require_env() {
  local name=$1
  local hint=${2:-}
  local value
  value=$(vistara_env "$name")
  if [ -z "$value" ]; then
    vistara_die "$VISTARA_EXIT_USAGE" "environment value ${name} is required. ${hint}"
  fi
  printf '%s' "$value"
}

# Reads a value from the process environment, falling back to the `azd`
# environment. `azd` exports its values to hooks, but `up.sh` also runs hooks
# directly, and a value another hook set earlier in the same run is only on
# disk.
vistara_env() {
  local name=$1
  local value=${!name:-}
  if [ -n "$value" ]; then
    printf '%s' "$value"
    return 0
  fi
  if vistara_have_command azd; then
    if [ -n "${VISTARA_AZD_ENVIRONMENT:-}" ]; then
      value=$(azd env get-value "$name" --environment "$VISTARA_AZD_ENVIRONMENT" 2>/dev/null || true)
    else
      value=$(azd env get-value "$name" 2>/dev/null || true)
    fi
    value=$(printf '%s' "$value" | tr -d '\r\n')
    case "$value" in
      ERROR*|*'not found'*) value='' ;;
    esac
    printf '%s' "$value"
    return 0
  fi
  printf ''
}

# Reads the selected environment, or the one named by VISTARA_AZD_ENVIRONMENT.
# Naming it explicitly is how a script reads an environment without selecting
# it: selection is a change to local state, and a run that may still be
# declined should not be making changes of any kind.
vistara_azd_env_values() {
  if [ -n "${VISTARA_AZD_ENVIRONMENT:-}" ]; then
    azd env get-values --environment "$VISTARA_AZD_ENVIRONMENT" 2>/dev/null || true
  else
    azd env get-values 2>/dev/null || true
  fi
}

# Loads every value from the selected `azd` environment into this process.
# Values already exported win, so a hook that `azd` invoked keeps the exact
# values `azd` handed it.
vistara_load_env() {
  local line key value
  vistara_have_command azd || return 0
  while IFS= read -r line; do
    case "$line" in
      *=*) ;;
      *) continue ;;
    esac
    key=${line%%=*}
    value=${line#*=}
    case "$key" in
      ''|*[!A-Za-z0-9_]*) continue ;;
    esac
    case "$value" in
      '"'*'"')
        value=${value#\"}
        value=${value%\"}
        ;;
    esac
    if [ -z "${!key:-}" ]; then
      export "${key}=${value}"
    fi
  done <<EOF
$(vistara_azd_env_values)
EOF
}

vistara_azd_env_set() {
  local name=$1
  local value=$2
  if [ -n "${VISTARA_AZD_ENVIRONMENT:-}" ]; then
    azd env set "$name" "$value" --environment "$VISTARA_AZD_ENVIRONMENT" >/dev/null
  else
    azd env set "$name" "$value" >/dev/null
  fi
  export "${name}=${value}"
}

# Clears a value without failing the run: some `azd` versions refuse an empty
# assignment, and a stale value is a warning rather than a reason to abandon a
# teardown that has already deleted the resources.
vistara_azd_env_clear() {
  local name=$1
  if ! vistara_azd_env_set "$name" '' >/dev/null 2>&1; then
    vistara_warn "could not clear ${name} in the azd environment; clear it with: azd env set ${name} \"\""
    return 0
  fi
  export "${name}="
}

# ---------------------------------------------------------------------------
# Scratch state
#
# Secret material and generated request bodies live here. The directory is
# 0700 under the `azd` environment folder, which `.gitignore` excludes, so
# nothing lands in a shared temporary directory another local account can read.
# ---------------------------------------------------------------------------

vistara_run_dir() {
  local base
  if [ -n "${VISTARA_STATE_DIR:-}" ]; then
    base=$VISTARA_STATE_DIR
  else
    base="${VISTARA_AZURE_DIR}/.azure/${AZURE_ENV_NAME:-default}/.vistara"
  fi
  mkdir -p "$base" 2>/dev/null || return 1
  chmod 700 "$base" 2>/dev/null || true
  printf '%s' "$base"
}

# Creates a 0600 file inside the run directory and prints its path.
vistara_private_file() {
  local directory name path
  directory=$(vistara_run_dir) || vistara_die "$VISTARA_EXIT_PROVISION" 'could not create the bootstrap scratch directory.'
  name=$1
  path="${directory}/${name}"
  rm -f "$path"
  (umask 077 && : >"$path")
  chmod 600 "$path"
  printf '%s' "$path"
}

# Overwrites and removes a private file. Best effort: the point is that the
# value stops existing as soon as the step that needed it is finished.
vistara_shred() {
  local path
  for path in "$@"; do
    [ -f "$path" ] || continue
    : >"$path" 2>/dev/null || true
    rm -f "$path" 2>/dev/null || true
  done
}

# ---------------------------------------------------------------------------
# Redaction
#
# Diagnostics from Azure can echo an access token or a connection string back.
# Everything printed from a failure path is filtered through this.
# ---------------------------------------------------------------------------

vistara_redact() {
  sed \
    -e 's/eyJ[A-Za-z0-9_=-]\{10,\}\.[A-Za-z0-9_=.-]\{10,\}/[redacted-token]/g' \
    -e 's/[Pp][Aa][Ss][Ss][Ww][Oo][Rr][Dd]=[^ ;"]*/password=[redacted]/g' \
    -e 's/[Aa][Cc][Cc][Ee][Ss][Ss][Tt][Oo][Kk][Ee][Nn]"*[: ]*"*[A-Za-z0-9._-]\{16,\}/accessToken=[redacted]/g' \
    -e 's/[Ss][Ee][Cc][Rr][Ee][Tt]=[^ ;"]*/secret=[redacted]/g' \
    -e 's/sig=[A-Za-z0-9%_+\/=-]\{8,\}/sig=[redacted]/g'
}

# ---------------------------------------------------------------------------
# Prompting
# ---------------------------------------------------------------------------

vistara_assume_yes() {
  [ "${VISTARA_ASSUME_YES:-0}" = '1' ]
}

# vistara_confirm <prompt>; true when the operator agreed. Never assumes an
# answer: without a terminal and without --yes it fails the run instead.
vistara_confirm() {
  local prompt=$1
  local reply=''
  if vistara_assume_yes; then
    return 0
  fi
  if [ ! -t 0 ]; then
    vistara_die "$VISTARA_EXIT_USAGE" "${prompt} — no terminal is attached; rerun with --yes to accept non-interactively."
  fi
  printf '%s [y/N]: ' "$prompt" >&2
  IFS= read -r reply || reply=''
  case "$reply" in
    y|Y|yes|YES|Yes) return 0 ;;
    *) return 1 ;;
  esac
}

# Requires the operator to type an exact phrase. Used before anything
# destructive so a stray keystroke cannot delete data.
vistara_confirm_phrase() {
  local prompt=$1
  local expected=$2
  local reply=''
  if vistara_assume_yes; then
    return 0
  fi
  if [ ! -t 0 ]; then
    vistara_die "$VISTARA_EXIT_USAGE" "${prompt} — no terminal is attached; rerun with --yes to accept non-interactively."
  fi
  printf '%s\nType %s to continue: ' "$prompt" "$expected" >&2
  IFS= read -r reply || reply=''
  [ "$reply" = "$expected" ]
}

# The three managed-identity PostgreSQL roles are created by connecting to the
# server and calling a function on it. Azure exposes no other route to that —
# no ARM resource, no CLI command — so a machine with neither a PostgreSQL
# client nor a container runtime cannot finish a deployment, however much of it
# has already been created.
#
# Which is why this is asked before anything is created rather than at the step
# that needs it: finding out after a provisioning pass costs the operator the
# wait, and leaves them with half a deployment and a tool to go and install.
vistara_have_usable_psql() {
  vistara_have_command psql && psql --version >/dev/null 2>&1
}

vistara_have_usable_docker() {
  vistara_have_command docker && docker version >/dev/null 2>&1
}

vistara_have_postgres_client() {
  vistara_have_usable_psql || vistara_have_usable_docker
}

vistara_require_postgres_client() {
  local context=$1
  if vistara_have_postgres_client; then
    return 0
  fi
  vistara_error 'neither psql nor docker is available.'
  vistara_error "${context} has to connect to PostgreSQL to create the three managed-identity roles, and Azure offers no server-side way to do it instead."
  vistara_error 'Install one of these and rerun:'
  vistara_error '  macOS   brew install libpq && brew link --force libpq'
  vistara_error '  Debian  sudo apt-get install postgresql-client'
  vistara_error '  Docker  any container runtime; the bootstrap runs psql in a container instead'
  vistara_die "$VISTARA_EXIT_MISSING_TOOL" 'a PostgreSQL client is required.'
}

vistara_seconds() {
  date +%s
}

# Asserts that the tenant this deployment is configured for is the tenant Azure
# reports for the subscription it is deployed into.
#
# Everything the identity path is built from — the federated credential issuer,
# the OIDC authority, the directory tenant the first-owner allowlist is keyed
# on — is derived from one value, AZURE_TENANT_ID. A wrong value is therefore
# consistent with itself: the issuer built from it matches the issuer
# registered from it, and a verification that compares the two agrees. The only
# thing that can contradict it is Azure, so each step that is about to depend
# on the tenant asks Azure directly rather than trusting the value it was
# handed, or another step's conclusion about it.
vistara_require_tenant_matches_subscription() {
  local context=$1
  local declared subscription_id observed

  declared=$(vistara_env AZURE_TENANT_ID)
  subscription_id=$(vistara_env AZURE_SUBSCRIPTION_ID)

  if [ -z "$declared" ]; then
    vistara_die "$VISTARA_EXIT_USAGE" \
      "AZURE_TENANT_ID is not set, so ${context} cannot be pinned to a directory."
  fi
  if [ -z "$subscription_id" ]; then
    vistara_die "$VISTARA_EXIT_USAGE" \
      "AZURE_SUBSCRIPTION_ID is not set, so AZURE_TENANT_ID cannot be checked against the subscription for ${context}."
  fi

  observed=$(az account show --subscription "$subscription_id" --query tenantId --output tsv 2>/dev/null | tr -d '\r\n' || true)
  if [ -z "$observed" ]; then
    vistara_die "$VISTARA_EXIT_PERMISSION" \
      "could not read the tenantId of subscription ${subscription_id}, so AZURE_TENANT_ID cannot be checked before ${context}. Run: az login"
  fi

  if [ "$(vistara_lowercase "$declared")" != "$(vistara_lowercase "$observed")" ]; then
    vistara_error "AZURE_TENANT_ID is ${declared}."
    vistara_error "The tenantId Azure reports for AZURE_SUBSCRIPTION_ID ${subscription_id} is ${observed}."
    vistara_error "Every value ${context} depends on is derived from AZURE_TENANT_ID, so a wrong one agrees with itself and only Azure can contradict it."
    vistara_die "$VISTARA_EXIT_PROVISION" \
      'AZURE_TENANT_ID does not match the tenant of AZURE_SUBSCRIPTION_ID.'
  fi
}

# Resolves the tag of a repository's latest published release by following the
# redirect GitHub answers `/releases/latest` with.
#
# Everything about that sentence has to be enforced rather than assumed. Curl
# does not follow a redirect unless it is told to, so without -L the "final"
# URL is the one that was asked for and the tag would be parsed out of nothing.
# A redirect chain is attacker-influenced input, so it is bounded, capped in
# time, and confined to HTTPS on the first hop and on every hop after it — a
# redirect to http:// or to a file:// URL is refused rather than followed. And
# the answer is only believed when the final response was a 200 whose URL is a
# release tag of this exact repository: `/releases/latest` on a repository with
# no releases answers 200 at `/releases`, which is a successful request that
# resolves nothing.
#
# Prints the tag, or nothing when it cannot be established.
vistara_resolve_latest_release_tag() {
  local owner=$1
  local repository=$2
  local expected_prefix="https://github.com/${owner}/${repository}/releases/tag/"
  local response status_code final_url tag
  local tag_pattern='^[A-Za-z0-9][A-Za-z0-9._+-]*$'

  response=$(curl -fsS \
    --location \
    --proto '=https' \
    --proto-redir '=https' \
    --max-redirs "${VISTARA_MAX_REDIRECTS:-5}" \
    --max-time "${VISTARA_HTTP_TIMEOUT_SECONDS:-30}" \
    --output /dev/null \
    --write-out '%{http_code} %{url_effective}' \
    "https://github.com/${owner}/${repository}/releases/latest" 2>/dev/null) || return 1
  response=$(printf '%s' "$response" | tr -d '\r\n')

  status_code=${response%% *}
  final_url=${response#* }

  if [ "$status_code" != '200' ]; then
    return 1
  fi

  case "$final_url" in
    "${expected_prefix}"?*) ;;
    *) return 1 ;;
  esac

  tag=${final_url#"$expected_prefix"}
  case "$tag" in
    */*) return 1 ;;
  esac
  if ! [[ $tag =~ $tag_pattern ]]; then
    return 1
  fi

  printf '%s' "$tag"
}

# Opens a URL in the operator's browser. Never fails the run: the URL is always
# printed as well, so a headless machine loses nothing.
vistara_open_url() {
  local url=$1
  if [ "${VISTARA_NO_OPEN:-0}" = '1' ]; then
    return 0
  fi
  if vistara_have_command open; then
    open "$url" >/dev/null 2>&1 || true
  elif vistara_have_command xdg-open; then
    xdg-open "$url" >/dev/null 2>&1 || true
  else
    vistara_warn 'no browser opener found; open the URL above manually.'
  fi
}

# The Key Vault name carried by the AZURE_KEY_VAULT_ENDPOINT output.
vistara_vault_name_from_endpoint() {
  local endpoint=${1:-}
  endpoint=${endpoint#https://}
  endpoint=${endpoint%%/*}
  printf '%s' "${endpoint%%.*}"
}
