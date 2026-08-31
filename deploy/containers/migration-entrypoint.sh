#!/usr/bin/env bash
# Runs one migration bundle against the configured database.
#
# Password mode (no MIGRATION_MANAGED_IDENTITY_CLIENT_ID) is unchanged: the
# operator connection string arrives in ConnectionStrings__Vistara and is handed
# to the bundle as-is.
#
# Managed-identity mode never reads, parses, or forwards an operator connection
# string. Npgsql accepts aliases and repeated keywords, so any guard that
# inspects a supplied string can be walked past with `pwd=`, `psw=`, a duplicate
# `SSL Mode`, or quoting; instead this script builds the whole connection string
# from discrete, individually validated values and ignores everything else. The
# Entra access token is read straight from the Container Apps identity endpoint
# with no Azure CLI, and reaches Npgsql through a private pgpass file, never
# through an argument list, an environment variable, or a log line.
#
# Frozen environment contract (Bicep/azd emit these; see HB-08 and HB-12):
#
#   MIGRATION_PROVIDER                      Sqlite | PostgreSql
#   ConnectionStrings__Vistara              password mode only; ignored otherwise
#   MIGRATION_MANAGED_IDENTITY_CLIENT_ID    UAMI client id GUID; enables Entra mode
#   MIGRATION_POSTGRES_HOST                 required in Entra mode
#   MIGRATION_POSTGRES_PORT                 optional, default 5432, 1-65535
#   MIGRATION_POSTGRES_DATABASE             required in Entra mode
#   MIGRATION_POSTGRES_USERNAME             required in Entra mode
#   MIGRATION_POSTGRES_HOST_SUFFIXES        optional, default .postgres.database.azure.com
#   MIGRATION_POSTGRES_HOST_ALLOWLIST       optional exact hosts; replaces the suffix policy
#   MIGRATION_POSTGRES_ROOT_CERTIFICATE     optional CA bundle path for VerifyFull
#   MIGRATION_ENTRA_TOKEN_SCOPE             optional, default ossrdbms-aad .default scope
#   IDENTITY_ENDPOINT                       injected by Azure Container Apps
#   IDENTITY_HEADER                         injected by Azure Container Apps
#
# SSL Mode=VerifyFull, GSS Encryption Mode=Disable, and the pgpass path are
# fixed by this script and cannot be configured away.
set -euo pipefail
set +o history 2>/dev/null || true

DEFAULT_TOKEN_SCOPE="https://ossrdbms-aad.database.windows.net/.default"
DEFAULT_POSTGRES_PORT="5432"
DEFAULT_POSTGRES_HOST_SUFFIXES=".postgres.database.azure.com"
IDENTITY_API_VERSION="2019-08-01"
IDENTITY_TIMEOUT_SECONDS="${MIGRATION_IDENTITY_TIMEOUT_SECONDS:-10}"
# A bundle run has to outlive the token it started with, so refuse anything that
# expires inside the window a migration can plausibly need.
MINIMUM_TOKEN_LIFETIME_SECONDS=300
# PostgreSQL truncates identifiers at 63 bytes and DNS names at 253.
MAXIMUM_IDENTIFIER_LENGTH=63
MAXIMUM_HOST_LENGTH=253
MAXIMUM_PATH_LENGTH=255

HOST_PATTERN='^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$'
DATABASE_PATTERN='^[A-Za-z0-9_][A-Za-z0-9_.-]*$'
USERNAME_PATTERN='^[A-Za-z0-9_][A-Za-z0-9_.@-]*$'
PATH_PATTERN='^/[A-Za-z0-9_./-]*$'
PORT_PATTERN='^[1-9][0-9]{0,4}$'

token_directory=""

fail() {
  echo "$*" >&2
  exit 78
}

cleanup() {
  if [ -n "$token_directory" ] && [ -d "$token_directory" ]; then
    rm -rf "$token_directory"
  fi
}

bundle_directory="${MIGRATION_BUNDLE_DIRECTORY:-/app}"
case "${MIGRATION_PROVIDER:-}" in
  Sqlite|sqlite)
    provider="Sqlite"
    bundle="$bundle_directory/vistara-migrate-sqlite"
    ;;
  PostgreSql|postgresql|Postgres|postgres)
    provider="PostgreSql"
    bundle="$bundle_directory/vistara-migrate-postgres"
    ;;
  *)
    echo "MIGRATION_PROVIDER must be Sqlite or PostgreSql." >&2
    exit 64
    ;;
esac

managed_identity_client_id="${MIGRATION_MANAGED_IDENTITY_CLIENT_ID:-}"
if [ -z "$managed_identity_client_id" ]; then
  connection_string="${ConnectionStrings__Vistara:-${Persistence__ConnectionString:-}}"
  if [ -z "$connection_string" ]; then
    echo "ConnectionStrings__Vistara is required." >&2
    exit 64
  fi

  exec "$bundle" --connection "$connection_string"
fi

# --- Microsoft Entra ID token mode -------------------------------------------

matches() {
  printf '%s' "$1" | grep -Eq "$2"
}

require_value() {
  # name, value, pattern, maximum length. Control characters are rejected before
  # the pattern runs so a newline can never split the value into a matching line.
  local name="$1"
  local value="$2"
  local pattern="$3"
  local limit="$4"
  [ -n "$value" ] ||
    fail "$name is required for managed-identity migrations."
  case "$value" in
    *[![:print:]]*) fail "$name must not contain control characters." ;;
  esac
  [ "${#value}" -le "$limit" ] ||
    fail "$name must be at most $limit characters."
  matches "$value" "$pattern" ||
    fail "$name contains characters that are not allowed."
}

lowercase() {
  printf '%s' "$1" | tr '[:upper:]' '[:lower:]'
}

url_encode() {
  local value="$1"
  local encoded=""
  local index
  local character
  for ((index = 0; index < ${#value}; index++)); do
    character="${value:index:1}"
    case "$character" in
      [A-Za-z0-9.~_-]) encoded="$encoded$character" ;;
      *) encoded="$encoded$(printf '%%%02X' "'$character")" ;;
    esac
  done
  printf '%s' "$encoded"
}

json_string_value() {
  # Reads one scalar member without echoing the rest of the document.
  sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\"\{0,1\}\([^\",}]*\)\"\{0,1\}.*/\1/p" <<<"$2" |
    head -n 1
}

require_guid() {
  local hex4="[0-9a-fA-F][0-9a-fA-F][0-9a-fA-F][0-9a-fA-F]"
  # shellcheck disable=SC2254
  case "$1" in
    $hex4$hex4-$hex4-$hex4-$hex4-$hex4$hex4$hex4) ;;
    *) fail "$2" ;;
  esac
}

# Nothing the operator supplies as a connection string, and nothing libpq or
# Npgsql would read from the environment, may reach the bundle: the constructed
# string is the only source of connection settings. HOME moves to the private
# token directory so a planted ~/.pgpass or ~/.postgresql/root.crt cannot alter
# authentication or certificate trust either.
scrub_environment() {
  local name
  for name in $(compgen -e 2>/dev/null || true); do
    case "$name" in
      PG*|ConnectionStrings__*|Persistence__*) unset "$name" ;;
    esac
  done
  unset PGPASSWORD PGPASSFILE PGUSER PGHOST PGHOSTADDR PGPORT PGDATABASE \
    PGOPTIONS PGAPPNAME PGCLIENTENCODING PGTZ PGSSLMODE PGSSLNEGOTIATION \
    PGSSLCERT PGSSLKEY PGSSLROOTCERT PGSSLCRL PGGSSENCMODE PGREQUIREAUTH \
    PGTARGETSESSIONATTRS PGSERVICE PGSERVICEFILE PGSYSCONFDIR \
    ConnectionStrings__Vistara Persistence__ConnectionString \
    IDENTITY_ENDPOINT IDENTITY_HEADER 2>/dev/null || true
  export HOME="$token_directory"
}

if [ "$provider" != "PostgreSql" ]; then
  fail "MIGRATION_MANAGED_IDENTITY_CLIENT_ID requires MIGRATION_PROVIDER=PostgreSql."
fi

if [ -n "${ConnectionStrings__Vistara:-}${Persistence__ConnectionString:-}" ]; then
  echo "Ignoring the supplied connection string: managed-identity migrations build their own." >&2
fi

require_guid "$managed_identity_client_id" \
  "MIGRATION_MANAGED_IDENTITY_CLIENT_ID must be the client id GUID of a user-assigned managed identity."

token_scope="${MIGRATION_ENTRA_TOKEN_SCOPE:-$DEFAULT_TOKEN_SCOPE}"
case "$token_scope" in
  https://*/.default) ;;
  *) fail "MIGRATION_ENTRA_TOKEN_SCOPE must be an HTTPS scope ending in /.default." ;;
esac
token_resource="${token_scope%/.default}"

# --- Connection settings, taken only from discrete validated variables --------

postgres_host="$(lowercase "${MIGRATION_POSTGRES_HOST:-}")"
require_value MIGRATION_POSTGRES_HOST "$postgres_host" \
  "$HOST_PATTERN" "$MAXIMUM_HOST_LENGTH"

host_allowlist="${MIGRATION_POSTGRES_HOST_ALLOWLIST:-}"
host_allowed="no"
if [ -n "$host_allowlist" ]; then
  for allowed_host in $host_allowlist; do
    allowed_host="$(lowercase "$allowed_host")"
    require_value MIGRATION_POSTGRES_HOST_ALLOWLIST "$allowed_host" \
      "$HOST_PATTERN" "$MAXIMUM_HOST_LENGTH"
    if [ "$allowed_host" = "$postgres_host" ]; then
      host_allowed="yes"
    fi
  done
  [ "$host_allowed" = "yes" ] ||
    fail "MIGRATION_POSTGRES_HOST is not listed in MIGRATION_POSTGRES_HOST_ALLOWLIST."
else
  for allowed_suffix in ${MIGRATION_POSTGRES_HOST_SUFFIXES:-$DEFAULT_POSTGRES_HOST_SUFFIXES}; do
    allowed_suffix="$(lowercase "$allowed_suffix")"
    case "$allowed_suffix" in
      .*) ;;
      *) fail "MIGRATION_POSTGRES_HOST_SUFFIXES entries must start with a dot." ;;
    esac
    require_value MIGRATION_POSTGRES_HOST_SUFFIXES "${allowed_suffix#.}" \
      "$HOST_PATTERN" "$MAXIMUM_HOST_LENGTH"
    case "$postgres_host" in
      *"$allowed_suffix") host_allowed="yes" ;;
    esac
  done
  [ "$host_allowed" = "yes" ] ||
    fail "MIGRATION_POSTGRES_HOST must be an Azure Database for PostgreSQL host; set MIGRATION_POSTGRES_HOST_SUFFIXES or MIGRATION_POSTGRES_HOST_ALLOWLIST to widen the policy."
fi

postgres_port="${MIGRATION_POSTGRES_PORT:-$DEFAULT_POSTGRES_PORT}"
require_value MIGRATION_POSTGRES_PORT "$postgres_port" "$PORT_PATTERN" 5
[ "$postgres_port" -le 65535 ] ||
  fail "MIGRATION_POSTGRES_PORT must be between 1 and 65535."

postgres_database="${MIGRATION_POSTGRES_DATABASE:-}"
require_value MIGRATION_POSTGRES_DATABASE "$postgres_database" \
  "$DATABASE_PATTERN" "$MAXIMUM_IDENTIFIER_LENGTH"

postgres_username="${MIGRATION_POSTGRES_USERNAME:-}"
require_value MIGRATION_POSTGRES_USERNAME "$postgres_username" \
  "$USERNAME_PATTERN" "$MAXIMUM_IDENTIFIER_LENGTH"

root_certificate="${MIGRATION_POSTGRES_ROOT_CERTIFICATE:-}"
if [ -n "$root_certificate" ]; then
  require_value MIGRATION_POSTGRES_ROOT_CERTIFICATE "$root_certificate" \
    "$PATH_PATTERN" "$MAXIMUM_PATH_LENGTH"
  [ -r "$root_certificate" ] ||
    fail "MIGRATION_POSTGRES_ROOT_CERTIFICATE is not a readable file."
fi

# --- Managed-identity endpoint ------------------------------------------------

identity_endpoint="${IDENTITY_ENDPOINT:-}"
identity_header="${IDENTITY_HEADER:-}"
[ -n "$identity_endpoint" ] ||
  fail "IDENTITY_ENDPOINT is required for managed-identity migrations."
[ -n "$identity_header" ] ||
  fail "IDENTITY_HEADER is required for managed-identity migrations."

case "$identity_endpoint" in
  https://*) identity_scheme="https"; identity_rest="${identity_endpoint#https://}" ;;
  http://*) identity_scheme="http"; identity_rest="${identity_endpoint#http://}" ;;
  *) fail "IDENTITY_ENDPOINT must be an http or https URL." ;;
esac

identity_authority="${identity_rest%%/*}"
if [ "$identity_authority" = "$identity_rest" ]; then
  identity_path="/"
else
  identity_path="/${identity_rest#*/}"
fi
identity_path="${identity_path%%\?*}"
identity_host="${identity_authority%%:*}"
if [ "$identity_host" = "$identity_authority" ]; then
  identity_port=""
else
  identity_port="${identity_authority##*:}"
fi

if [ "$identity_scheme" = "http" ]; then
  # Plaintext is only acceptable to the loopback or link-local identity service
  # that the platform injects; anywhere else would put the token on a network.
  case "$identity_host" in
    localhost|127.0.0.1|::1|169.254.*) ;;
    *) fail "IDENTITY_ENDPOINT may only use http for the loopback or link-local identity service." ;;
  esac
  identity_port="${identity_port:-80}"
else
  identity_port="${identity_port:-443}"
fi

case "$identity_port" in
  ''|*[!0-9]*) fail "IDENTITY_ENDPOINT has a non-numeric port." ;;
esac

identity_target="$identity_path?api-version=$IDENTITY_API_VERSION"
identity_target="$identity_target&resource=$(url_encode "$token_resource")"
identity_target="$identity_target&client_id=$(url_encode "$managed_identity_client_id")"

# Written straight to the socket: a command substitution would strip the
# trailing newline that terminates the request headers.
write_identity_request() {
  printf 'GET %s HTTP/1.1\r\n' "$identity_target"
  printf 'Host: %s\r\n' "$identity_authority"
  printf 'X-IDENTITY-HEADER: %s\r\n' "$identity_header"
  printf 'Metadata: true\r\n'
  printf 'Accept: application/json\r\n'
  printf 'Connection: close\r\n'
  printf '\r\n'
}

read_identity_response() {
  local response=""
  local line=""
  while IFS= read -r -t "$IDENTITY_TIMEOUT_SECONDS" line <&3; do
    response="$response${line%$'\r'}"$'\n'
  done
  if [ -n "$line" ]; then
    response="$response${line%$'\r'}"
  fi
  printf '%s' "$response"
}

fetch_identity_response() {
  if [ "$identity_scheme" = "https" ]; then
    write_identity_request |
      timeout "$IDENTITY_TIMEOUT_SECONDS" \
        openssl s_client -quiet -no_ign_eof -verify_return_error \
        -connect "$identity_host:$identity_port" -servername "$identity_host" \
        2>/dev/null
    return
  fi

  exec 3<>"/dev/tcp/$identity_host/$identity_port" ||
    fail "The managed-identity endpoint refused a connection."
  write_identity_request >&3
  read_identity_response
  exec 3<&-
}

identity_response="$(fetch_identity_response || true)"
[ -n "$identity_response" ] ||
  fail "The managed-identity endpoint returned no response."

identity_status="$(head -n 1 <<<"$identity_response" | tr -d '\r')"
case "$identity_status" in
  "HTTP/1.0 200"*|"HTTP/1.1 200"*) ;;
  *) fail "The managed-identity endpoint rejected the token request." ;;
esac

access_token="$(json_string_value access_token "$identity_response")"
expires_on="$(json_string_value expires_on "$identity_response")"
identity_response=""

[ -n "$access_token" ] ||
  fail "The managed-identity response carried no access token."
# A pgpass password may not contain a colon or a backslash, and a bearer token
# never does; anything else is a malformed or hostile response.
case "$access_token" in
  *[!A-Za-z0-9._~+/=-]*) fail "The managed-identity access token has an unexpected format." ;;
esac
[ "${#access_token}" -ge 40 ] ||
  fail "The managed-identity access token is implausibly short."

case "$expires_on" in
  ''|*[!0-9]*) fail "The managed-identity response carried no numeric expires_on." ;;
esac
now="$(date +%s)"
if [ "$((expires_on - now))" -lt "$MINIMUM_TOKEN_LIFETIME_SECONDS" ]; then
  fail "The managed-identity access token expires within ${MINIMUM_TOKEN_LIFETIME_SECONDS}s."
fi

# --- Constructed connection string -------------------------------------------

trap cleanup EXIT
umask 077
token_directory="$(mktemp -d "${MIGRATION_TOKEN_DIRECTORY:-${XDG_RUNTIME_DIR:-${TMPDIR:-/tmp}}}/vistara-migrate.XXXXXXXX")"
chmod 700 "$token_directory"
token_file="$token_directory/pgpass"
: >"$token_file"
chmod 600 "$token_file"
printf '*:*:*:*:%s\n' "$access_token" >"$token_file"
access_token=""

connection_string="Host=$postgres_host"
connection_string="$connection_string;Port=$postgres_port"
connection_string="$connection_string;Database=$postgres_database"
connection_string="$connection_string;Username=$postgres_username"
connection_string="$connection_string;SSL Mode=VerifyFull"
connection_string="$connection_string;GSS Encryption Mode=Disable"
connection_string="$connection_string;Include Error Detail=false"
connection_string="$connection_string;Passfile=$token_file"
if [ -n "$root_certificate" ]; then
  connection_string="$connection_string;Root Certificate=$root_certificate"
fi

scrub_environment

"$bundle" --connection "$connection_string" &
bundle_pid=$!
trap 'kill -TERM "$bundle_pid" 2>/dev/null || true' INT TERM
bundle_status=0
wait "$bundle_pid" || bundle_status=$?
exit "$bundle_status"
