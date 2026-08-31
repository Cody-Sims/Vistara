#!/usr/bin/env bash
# Runs one migration bundle against the configured database.
#
# Password deployments are unchanged: the connection string arrives in
# ConnectionStrings__Vistara and is handed to the bundle as-is.
#
# Hosted Azure deployments set MIGRATION_MANAGED_IDENTITY_CLIENT_ID instead of a
# password. The token is then read directly from the Container Apps identity
# endpoint (IDENTITY_ENDPOINT/IDENTITY_HEADER) with no Azure CLI, and is handed
# to the bundle through a private pgpass file that Npgsql reads. Migration
# bundles only accept --connection, and process arguments are world-readable
# through /proc, so the token is never spliced into the command line, an
# environment variable, or any log line. Query values are URL-encoded, the
# response is validated, and every failure exits non-zero without running a
# migration.
set -euo pipefail
set +o history 2>/dev/null || true

DEFAULT_TOKEN_SCOPE="https://ossrdbms-aad.database.windows.net/.default"
IDENTITY_API_VERSION="2019-08-01"
IDENTITY_TIMEOUT_SECONDS="${MIGRATION_IDENTITY_TIMEOUT_SECONDS:-10}"
# A bundle run has to outlive the token it started with, so refuse anything that
# expires inside the window a migration can plausibly need.
MINIMUM_TOKEN_LIFETIME_SECONDS=300

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

connection_string="${ConnectionStrings__Vistara:-${Persistence__ConnectionString:-}}"
if [ -z "$connection_string" ]; then
  echo "ConnectionStrings__Vistara is required." >&2
  exit 64
fi

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
  exec "$bundle" --connection "$connection_string"
fi

# --- Microsoft Entra ID token mode -------------------------------------------

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

if [ "$provider" != "PostgreSql" ]; then
  fail "MIGRATION_MANAGED_IDENTITY_CLIENT_ID requires MIGRATION_PROVIDER=PostgreSql."
fi

require_guid "$managed_identity_client_id" \
  "MIGRATION_MANAGED_IDENTITY_CLIENT_ID must be the client id GUID of a user-assigned managed identity."

token_scope="${MIGRATION_ENTRA_TOKEN_SCOPE:-$DEFAULT_TOKEN_SCOPE}"
case "$token_scope" in
  https://*/.default) ;;
  *) fail "MIGRATION_ENTRA_TOKEN_SCOPE must be an HTTPS scope ending in /.default." ;;
esac
token_resource="${token_scope%/.default}"

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

# The token replaces the password, so a connection string that still carries one
# would either leak it or silently win; both fail closed here.
normalized_connection="$(tr -d '[:space:]' <<<"$connection_string" | tr '[:upper:]' '[:lower:]')"
case "$normalized_connection" in
  *password=*) fail "A managed-identity connection string must not contain Password." ;;
esac
case "$normalized_connection" in
  *passfile=*) fail "A managed-identity connection string must not contain Passfile." ;;
esac
case "$normalized_connection" in
  *sslmode=verifyfull*) ;;
  *) fail "A managed-identity connection string must set SSL Mode=VerifyFull." ;;
esac
case "$normalized_connection" in
  *gssencryptionmode=disable*) ;;
  *) fail "A managed-identity connection string must set GSS Encryption Mode=Disable." ;;
esac

trap cleanup EXIT
umask 077
token_directory="$(mktemp -d "${MIGRATION_TOKEN_DIRECTORY:-${XDG_RUNTIME_DIR:-${TMPDIR:-/tmp}}}/vistara-migrate.XXXXXXXX")"
chmod 700 "$token_directory"
token_file="$token_directory/pgpass"
: >"$token_file"
chmod 600 "$token_file"
printf '*:*:*:*:%s\n' "$access_token" >"$token_file"
access_token=""

case "$connection_string" in
  *\;) connection_string="${connection_string}Passfile=$token_file" ;;
  *) connection_string="$connection_string;Passfile=$token_file" ;;
esac

"$bundle" --connection "$connection_string" &
bundle_pid=$!
trap 'kill -TERM "$bundle_pid" 2>/dev/null || true' INT TERM
bundle_status=0
wait "$bundle_pid" || bundle_status=$?
exit "$bundle_status"
