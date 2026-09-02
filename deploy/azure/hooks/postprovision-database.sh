#!/usr/bin/env bash
# Vistara hosted bootstrap — PostgreSQL principals and grants.
#
# The three managed identities have no password, so they cannot be created with
# CREATE ROLE: `pgaadauth_create_principal_with_oid` maps a directory object ID
# onto a PostgreSQL role, and it takes five arguments. The grants that follow
# reproduce the privilege model of `deploy/postgres/init-runtime-roles.sh`.
#
# The server accepts no Azure-internal path for this, so the operator's own
# address is allowed through the firewall for exactly as long as the SQL takes
# and removed again by the exit trap, including on failure or interrupt.
#
# Idempotent: `sql/bootstrap-roles.sql` skips principals that already exist,
# and a completed run is recorded so the second provisioning pass does not open
# the firewall again.
if [ -z "${BASH_VERSION:-}" ]; then
  exec bash "$0" "$@"
fi

set -euo pipefail

# shellcheck source=lib/common.sh
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/common.sh"

vistara_load_env

vistara_step 'PostgreSQL principals and grants'

resource_group=$(vistara_require_env AZURE_RESOURCE_GROUP)
postgres_host=$(vistara_require_env POSTGRES_HOST)
postgres_database=$(vistara_require_env POSTGRES_DATABASE)
api_role=$(vistara_require_env POSTGRES_API_ROLE)
worker_role=$(vistara_require_env POSTGRES_WORKER_ROLE)
migrator_role=$(vistara_require_env POSTGRES_MIGRATOR_ROLE)
api_object_id=$(vistara_require_env API_IDENTITY_PRINCIPAL_ID)
worker_object_id=$(vistara_require_env WORKER_IDENTITY_PRINCIPAL_ID)
migrate_object_id=$(vistara_require_env MIGRATE_IDENTITY_PRINCIPAL_ID)
admin_principal_name=$(vistara_require_env VISTARA_POSTGRES_ADMIN_PRINCIPAL_NAME)

vistara_require_guid "$api_object_id" 'API_IDENTITY_PRINCIPAL_ID'
vistara_require_guid "$worker_object_id" 'WORKER_IDENTITY_PRINCIPAL_ID'
vistara_require_guid "$migrate_object_id" 'MIGRATE_IDENTITY_PRINCIPAL_ID'

server_name=${postgres_host%%.*}
postgres_port=${VISTARA_POSTGRES_PORT:-5432}
state_signature="${postgres_host}|${api_object_id}|${worker_object_id}|${migrate_object_id}"

if [ "${VISTARA_FORCE_DATABASE_BOOTSTRAP:-0}" != '1' ] \
  && [ "$(vistara_env VISTARA_DATABASE_BOOTSTRAP_STATE)" = "$state_signature" ]; then
  vistara_log 'database principals and grants are already in place; nothing to do.'
  exit 0
fi

# ---------------------------------------------------------------------------
# A client that can speak to PostgreSQL
# ---------------------------------------------------------------------------

# The preflight already refused a run that could not get here, so this is the
# defensive half of that check: it costs nothing and it is what makes the step
# correct on its own, however it was reached.
vistara_require_postgres_client 'creating the database roles'

psql_mode='local'
if ! vistara_have_command psql; then
  psql_mode='docker'
  vistara_log 'psql is not installed; running the bootstrap SQL through a container instead.'
fi

# ---------------------------------------------------------------------------
# Temporary firewall opening
# ---------------------------------------------------------------------------

firewall_rule_created=0
pgpass_file=''

cleanup() {
  local status=$?
  if [ "${cleanup_done:-0}" = '1' ]; then
    return "$status"
  fi
  cleanup_done=1
  if [ -n "$pgpass_file" ]; then
    vistara_shred "$pgpass_file"
  fi
  if [ "$firewall_rule_created" = '1' ]; then
    vistara_log "removing the temporary firewall rule ${VISTARA_FIREWALL_RULE_NAME}."
    az postgres flexible-server firewall-rule delete \
      --resource-group "$resource_group" \
      --name "$server_name" \
      --rule-name "$VISTARA_FIREWALL_RULE_NAME" \
      --yes --output none 2>/dev/null \
      || vistara_warn "could not remove the firewall rule. Remove it with: az postgres flexible-server firewall-rule delete --resource-group ${resource_group} --name ${server_name} --rule-name ${VISTARA_FIREWALL_RULE_NAME} --yes"
  fi
  return "$status"
}

# A signal handler that only returns lets the script carry on with its
# credentials shredded and its firewall opening already closed, so the signal
# paths end the run instead.
on_interrupt() {
  cleanup
  exit 130
}

on_terminate() {
  cleanup
  exit 143
}

cleanup_done=0
trap cleanup EXIT
trap on_interrupt INT
trap on_terminate TERM

client_ip=$(vistara_env VISTARA_CLIENT_IP)
if [ -z "$client_ip" ]; then
  ip_lookup_url=${VISTARA_PUBLIC_IP_URL:-https://api.ipify.org}
  client_ip=$(curl -fsS --max-time 15 "$ip_lookup_url" 2>/dev/null || true)
  client_ip=$(printf '%s' "$client_ip" | tr -d '\r\n')
fi

ipv4_pattern='^[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}$'
if ! [[ $client_ip =~ $ipv4_pattern ]]; then
  vistara_die "$VISTARA_EXIT_PROVISION" \
    "could not determine this machine's public IPv4 address. Rerun with --client-ip <address>, which is the address the server must allow while the roles are created."
fi

vistara_log "allowing ${client_ip} through the PostgreSQL firewall for the duration of this step."
# Marked before the call, not after: a create that times out or is interrupted
# can still have reached Azure, and an opening nobody remembers making is one
# that never gets closed.
firewall_rule_created=1
if ! firewall_output=$(az postgres flexible-server firewall-rule create \
  --resource-group "$resource_group" \
  --name "$server_name" \
  --rule-name "$VISTARA_FIREWALL_RULE_NAME" \
  --start-ip-address "$client_ip" \
  --end-ip-address "$client_ip" \
  --output none 2>&1); then
  vistara_error "$(printf '%s' "$firewall_output" | vistara_redact)"
  vistara_die "$VISTARA_EXIT_PROVISION" \
    "could not open the PostgreSQL firewall for ${client_ip} on ${server_name}."
fi

# ---------------------------------------------------------------------------
# Credentials
#
# The administrator connects with a directory access token. It is written
# straight into a 0600 password file: a token on a command line is visible to
# every process on the machine, and PGPASSWORD is inherited by everything the
# script starts.
# ---------------------------------------------------------------------------

pgpass_file=$(vistara_private_file 'pgpass')
if ! az account get-access-token \
  --resource-type oss-rdbms \
  --query accessToken --output tsv 2>/dev/null \
  | tr -d '\r\n' \
  | awk -v host="$postgres_host" -v port="$postgres_port" -v user="$admin_principal_name" \
    '{ printf "%s:%s:*:%s:%s\n", host, port, user, $0 }' >"$pgpass_file"; then
  vistara_die "$VISTARA_EXIT_PERMISSION" \
    'could not acquire a PostgreSQL access token. Run: az login'
fi
chmod 600 "$pgpass_file"
if [ ! -s "$pgpass_file" ]; then
  vistara_die "$VISTARA_EXIT_PERMISSION" \
    'the PostgreSQL access token request returned nothing. Run: az login'
fi

# ---------------------------------------------------------------------------
# Run the SQL
# ---------------------------------------------------------------------------

sql_directory="${VISTARA_AZURE_DIR}/sql"
sql_file="${sql_directory}/bootstrap-roles.sql"
[ -f "$sql_file" ] || vistara_die "$VISTARA_EXIT_PROVISION" "missing ${sql_file}."

run_directory=$(vistara_run_dir)
ssl_mode=${VISTARA_PG_SSLMODE:-verify-full}
# libpq 16 and newer resolve `system` to the platform trust store, which is what
# validates the Azure server certificate without shipping a bundle.
ssl_root_certificate=${VISTARA_PG_SSLROOTCERT:-system}

connection="host=${postgres_host} port=${postgres_port} dbname=postgres user=${admin_principal_name} sslmode=${ssl_mode} sslrootcert=${ssl_root_certificate}"

psql_status=0
if [ "$psql_mode" = 'local' ]; then
  PGPASSFILE="$pgpass_file" psql \
    "$connection" \
    --no-password \
    --no-psqlrc \
    --set=ON_ERROR_STOP=1 \
    --set=application_database="$postgres_database" \
    --set=api_role="$api_role" \
    --set=worker_role="$worker_role" \
    --set=migrator_role="$migrator_role" \
    --set=api_object_id="$api_object_id" \
    --set=worker_object_id="$worker_object_id" \
    --set=migrator_object_id="$migrate_object_id" \
    --file "$sql_file" 2>&1 | vistara_redact >&2 || psql_status=$?
else
  psql_image=${VISTARA_PSQL_IMAGE:-docker.io/library/postgres:17-alpine}
  docker run --rm \
    --volume "${run_directory}:/vistara-run:ro" \
    --volume "${sql_directory}:/vistara-sql:ro" \
    --env PGPASSFILE=/vistara-run/pgpass \
    "$psql_image" \
    psql \
    "$connection" \
    --no-password \
    --no-psqlrc \
    --set=ON_ERROR_STOP=1 \
    --set=application_database="$postgres_database" \
    --set=api_role="$api_role" \
    --set=worker_role="$worker_role" \
    --set=migrator_role="$migrator_role" \
    --set=api_object_id="$api_object_id" \
    --set=worker_object_id="$worker_object_id" \
    --set=migrator_object_id="$migrate_object_id" \
    --file /vistara-sql/bootstrap-roles.sql 2>&1 | vistara_redact >&2 || psql_status=$?
fi

if [ "$psql_status" -ne 0 ]; then
  vistara_error "the bootstrap SQL failed against ${postgres_host}."
  vistara_error "Connect as the Entra administrator (${admin_principal_name}) and rerun it by hand if the cause is not obvious:"
  vistara_error "  ./deploy/azure/up.sh --env-name ${AZURE_ENV_NAME:-<env>}"
  vistara_die "$VISTARA_EXIT_PROVISION" 'PostgreSQL role bootstrap failed; nothing was deleted.'
fi

vistara_shred "$pgpass_file"
pgpass_file=''

vistara_azd_env_set VISTARA_DATABASE_BOOTSTRAP_STATE "$state_signature"
vistara_log "roles ${migrator_role}, ${api_role}, and ${worker_role} are present with the expected grants."
