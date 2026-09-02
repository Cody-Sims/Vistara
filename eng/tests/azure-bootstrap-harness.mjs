// Test harness for the hosted Azure bootstrap scripts.
//
// `up.sh` and `down.sh` only ever act through `az`, `azd`, `curl`, `openssl`,
// `psql`, `docker`, and a browser opener. Every one of those is replaced here
// by a recording stand-in on PATH, so the real scripts run end to end — the
// same argument parsing, the same ordering, the same traps — without an Azure
// subscription and without creating anything billable.
//
// Sandboxes live under `artifacts/` in the repository rather than the system
// temporary directory: the scripts write private files with restrictive
// permissions and a shared temporary directory is the wrong place to exercise
// that.

import { execFileSync, spawnSync } from 'node:child_process';
import { mkdirSync, readFileSync, rmSync, writeFileSync, existsSync, chmodSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
export const REPOSITORY_ROOT = resolve(HERE, '../..');
export const AZURE_DIR = resolve(REPOSITORY_ROOT, 'deploy/azure');
export const UP_SCRIPT = resolve(AZURE_DIR, 'up.sh');
export const DOWN_SCRIPT = resolve(AZURE_DIR, 'down.sh');

const SANDBOX_ROOT = resolve(REPOSITORY_ROOT, 'artifacts/azure-bootstrap-tests');

export const SUBSCRIPTION_ID = '11111111-1111-1111-1111-111111111111';
export const TENANT_ID = '22222222-2222-2222-2222-222222222222';
export const SIGNED_IN_OBJECT_ID = '33333333-3333-3333-3333-333333333333';
export const API_PRINCIPAL_ID = '44444444-4444-4444-4444-444444444444';
export const WORKER_PRINCIPAL_ID = '55555555-5555-5555-5555-555555555555';
export const MIGRATE_PRINCIPAL_ID = '66666666-6666-6666-6666-666666666666';
export const APP_CLIENT_ID = '77777777-7777-7777-7777-777777777777';
export const APP_OBJECT_ID = '88888888-8888-8888-8888-888888888888';
export const SERVICE_PRINCIPAL_ID = '99999999-9999-9999-9999-999999999999';
export const API_FQDN = 'ca-api-eval.icymoss-1234.eastus.azurecontainerapps.io';
export const SERVICE_API_URI = `https://${API_FQDN}`;
export const RESOURCE_GROUP = 'rg-vistara-eval';
export const VAULT_NAME = 'kv-vistara-abcdefghijk';
export const VAULT_ENDPOINT = `https://${VAULT_NAME}.vault.azure.net/`;
export const POSTGRES_HOST = 'psql-vistara-abcdefghij.postgres.database.azure.com';
export const MIGRATION_JOB_NAME = 'cj-migrate-eval';
export const API_CONTAINER_APP_NAME = 'ca-api-eval';
export const WORKER_CONTAINER_APP_NAME = 'ca-worker-eval';
export const PEPPER_MARKER = 'FAKEPEPPER0000000000000000000000000000000000=';
export const ACCESS_TOKEN_MARKER = 'eyJhbGciOiJSUzI1NiJ9.eyJmYWtlIjoidG9rZW4ifQ.c2lnbmF0dXJl';
export const API_DIGEST = `sha256:${'a'.repeat(64)}`;
export const WORKER_DIGEST = `sha256:${'b'.repeat(64)}`;
export const MIGRATION_DIGEST = `sha256:${'c'.repeat(64)}`;

let sandboxCounter = 0;

/** Writes an executable stand-in onto the sandbox PATH. */
function writeFake(binDirectory, name, body) {
  const path = join(binDirectory, name);
  writeFileSync(path, `#!/bin/bash\n${body}`, 'utf8');
  chmodSync(path, 0o755);
}

const LOGGING_PREAMBLE = `
set -u
tool=$(basename "$0")
log_line="$tool"
for argument in "$@"; do
  case "$argument" in
    @*)
      body_path=\${argument#@}
      if [ -f "$body_path" ]; then
        {
          printf '=== %s %s\\n' "$tool" "$body_path"
          cat "$body_path"
          printf '\\n'
        } >>"$FAKE_STATE/bodies.log"
        log_line="$log_line"$'\\t'"@body"
        continue
      fi
      ;;
  esac
  log_line="$log_line"$'\\t'"$argument"
done
printf '%s\\n' "$log_line" >>"$FAKE_STATE/calls.log"

flag_value() {
  local wanted=$1
  shift
  while [ "$#" -gt 0 ]; do
    if [ "$1" = "$wanted" ]; then
      printf '%s' "\${2:-}"
      return 0
    fi
    shift
  done
  printf ''
}

state_read() {
  if [ -f "$FAKE_STATE/$1" ]; then
    cat "$FAKE_STATE/$1"
  else
    printf '%s' "\${2:-}"
  fi
}
`;

const FAKE_AZ = `${LOGGING_PREAMBLE}
if [ -f "$FAKE_STATE/az_missing" ]; then
  echo "az: command not found" >&2
  exit 127
fi

query=$(flag_value --query "$@")
command_path="\${1:-} \${2:-} \${3:-}"

case "$command_path" in
  'version '*)
    printf '%s\\n' "$(state_read az_version 2.89.1)"
    exit 0
    ;;
  'bicep version '*)
    printf 'Bicep CLI version %s (deadbeef)\\n' "$(state_read bicep_version 0.46.1)"
    exit 0
    ;;
esac

case "\${1:-}" in
  account)
    case "\${2:-}" in
      show)
        if [ -f "$FAKE_STATE/not_logged_in" ]; then
          echo "Please run 'az login' to setup account." >&2
          exit 1
        fi
        requested=$(flag_value --subscription "$@")
        if [ -n "$requested" ] \
          && [ "$requested" != "$(state_read subscription_id ${SUBSCRIPTION_ID})" ] \
          && [ ! -f "$FAKE_STATE/allow_subscription_switch" ]; then
          echo "The subscription of '$requested' doesn't exist." >&2
          exit 1
        fi
        case "$query" in
          id) printf '%s\\n' "$(state_read subscription_id ${SUBSCRIPTION_ID})" ;;
          name) printf 'Scratch Subscription\\n' ;;
          tenantId)
            if [ -n "$requested" ] && [ "$requested" != "$(state_read subscription_id ${SUBSCRIPTION_ID})" ]; then
              printf '%s\\n' "$(state_read foreign_tenant_id "$(state_read tenant_id ${TENANT_ID})")"
            else
              printf '%s\\n' "$(state_read tenant_id ${TENANT_ID})"
            fi
            ;;
          user.name) printf 'operator@example.com\\n' ;;
          *) printf '\\n' ;;
        esac
        exit 0
        ;;
      set)
        requested=$(flag_value --subscription "$@")
        if [ "$requested" != "$(state_read subscription_id ${SUBSCRIPTION_ID})" ] && [ ! -f "$FAKE_STATE/allow_subscription_switch" ]; then
          echo "The subscription of '$requested' doesn't exist." >&2
          exit 1
        fi
        printf '%s' "$requested" >"$FAKE_STATE/subscription_id"
        exit 0
        ;;
      get-access-token)
        if [ -f "$FAKE_STATE/token_fails" ]; then
          echo "AADSTS50076" >&2
          exit 1
        fi
        printf '%s\\n' '${ACCESS_TOKEN_MARKER}'
        exit 0
        ;;
    esac
    ;;
  ad)
    case "\${2:-} \${3:-}" in
      'signed-in-user show')
        case "$query" in
          id) printf '%s\\n' "$(state_read signed_in_object_id ${SIGNED_IN_OBJECT_ID})" ;;
          userPrincipalName) printf 'operator@example.com\\n' ;;
          *) printf '\\n' ;;
        esac
        exit 0
        ;;
      'app list')
        if [ -f "$FAKE_STATE/no_directory_rights" ]; then
          echo "Insufficient privileges to complete the operation." >&2
          exit 1
        fi
        state_read ad_app_list ''
        exit 0
        ;;
      'app show')
        if [ ! -f "$FAKE_STATE/app_exists" ]; then
          echo "Application not found" >&2
          exit 1
        fi
        case "$query" in
          id) printf '%s\\n' '${APP_OBJECT_ID}' ;;
          web.redirectUris) state_read app_reply_urls "${SERVICE_API_URI}/api/v1/auth/oidc/entra/callback"$'\\t'"${SERVICE_API_URI}/api/v1/auth/oidc/entra/signed-out" ;;
          web.logoutUrl) state_read app_logout_url '' ;;
          *) printf '\\n' ;;
        esac
        exit 0
        ;;
      'app delete')
        touch "$FAKE_STATE/app_deleted"
        exit 0
        ;;
      'sp show')
        if [ ! -f "$FAKE_STATE/sp_exists" ]; then
          echo "Service principal not found" >&2
          exit 1
        fi
        printf '%s\\n' '${SERVICE_PRINCIPAL_ID}'
        exit 0
        ;;
      'sp create')
        touch "$FAKE_STATE/sp_exists"
        printf '%s\\n' '${SERVICE_PRINCIPAL_ID}'
        exit 0
        ;;
      'app federated-credential')
        case "\${4:-}" in
          list)
            case "$query" in
              *issuer*) state_read fic_issuer '' ;;
              *subject*) state_read fic_subject '' ;;
              *audiences*) state_read fic_audience '' ;;
              *) printf '\\n' ;;
            esac
            exit 0
            ;;
          create)
            printf '%s' "$(grep -o '"issuer": "[^"]*"' "$FAKE_STATE/bodies.log" | tail -n 1 | sed 's/.*: "//; s/"$//')" >"$FAKE_STATE/fic_issuer"
            printf '%s' "$(grep -o '"subject": "[^"]*"' "$FAKE_STATE/bodies.log" | tail -n 1 | sed 's/.*: "//; s/"$//')" >"$FAKE_STATE/fic_subject"
            printf '%s' "$(grep -o '"api://AzureADTokenExchange"' "$FAKE_STATE/bodies.log" | tail -n 1 | tr -d '"')" >"$FAKE_STATE/fic_audience"
            exit 0
            ;;
          delete)
            rm -f "$FAKE_STATE/fic_issuer" "$FAKE_STATE/fic_subject" "$FAKE_STATE/fic_audience"
            exit 0
            ;;
        esac
        ;;
    esac
    ;;
  rest)
    url=$(flag_value --url "$@")
    method=$(flag_value --method "$@")
    case "$method $url" in
      'POST https://graph.microsoft.com/v1.0/applications')
        if [ -f "$FAKE_STATE/app_create_denied" ]; then
          echo "Authorization_RequestDenied" >&2
          exit 1
        fi
        touch "$FAKE_STATE/app_exists"
        printf '%s\\n' '${APP_CLIENT_ID}'
        exit 0
        ;;
      PATCH*)
        exit 0
        ;;
    esac
    exit 0
    ;;
  provider)
    case "\${2:-}" in
      show) printf '%s\\n' "$(state_read provider_state Registered)" ; exit 0 ;;
      register) printf 'Registered' >"$FAKE_STATE/provider_state" ; exit 0 ;;
    esac
    ;;
  keyvault)
    case "\${2:-} \${3:-}" in
      'show '*)
        printf '/subscriptions/%s/resourceGroups/%s/providers/Microsoft.KeyVault/vaults/%s\\n' \\
          "$(state_read subscription_id ${SUBSCRIPTION_ID})" '${RESOURCE_GROUP}' '${VAULT_NAME}'
        exit 0
        ;;
      'secret list')
        if [ -f "$FAKE_STATE/vault_denied" ] && [ ! -f "$FAKE_STATE/role_assigned" ]; then
          echo "Caller is not authorized to perform action on resource." >&2
          exit 1
        fi
        exit 0
        ;;
      'secret show')
        if [ ! -f "$FAKE_STATE/secret_exists" ]; then
          echo "SecretNotFound" >&2
          exit 1
        fi
        printf 'https://%s.vault.azure.net/secrets/api-key-pepper/0123\\n' '${VAULT_NAME}'
        exit 0
        ;;
      'secret set')
        secret_file=$(flag_value --file "$@")
        if [ -n "$secret_file" ] && [ -f "$secret_file" ]; then
          stat -c '%a' "$secret_file" >"$FAKE_STATE/secret-file-mode" 2>/dev/null \\
            || stat -f '%Lp' "$secret_file" >"$FAKE_STATE/secret-file-mode" 2>/dev/null || true
          cp "$secret_file" "$FAKE_STATE/secret-value"
        fi
        touch "$FAKE_STATE/secret_exists"
        exit 0
        ;;
    esac
    ;;
  role)
    case "\${2:-} \${3:-}" in
      'assignment list') state_read role_assignment_id '' ; exit 0 ;;
      'assignment create') touch "$FAKE_STATE/role_assigned" ; exit 0 ;;
      'assignment delete') touch "$FAKE_STATE/role_assignment_deleted" ; exit 0 ;;
    esac
    ;;
  postgres)
    case "\${4:-}" in
      create)
        if [ -f "$FAKE_STATE/firewall_create_fails" ]; then
          echo "AuthorizationFailed" >&2
          exit 1
        fi
        touch "$FAKE_STATE/firewall_open"
        exit 0
        ;;
      delete)
        rm -f "$FAKE_STATE/firewall_open"
        touch "$FAKE_STATE/firewall_removed"
        exit 0
        ;;
    esac
    exit 0
    ;;
  containerapp)
    case "\${2:-} \${3:-}" in
      'job start')
        count=$(( $(state_read job_start_count 0) + 1 ))
        printf '%s' "$count" >"$FAKE_STATE/job_start_count"
        if [ -f "$FAKE_STATE/job_start_fails" ]; then
          echo "JobStartFailed" >&2
          exit 1
        fi
        printf '%s\\n' "$(state_read job_execution_name cj-migrate-eval-abc123)"
        exit 0
        ;;
      'job execution')
        sequence_file="$FAKE_STATE/job_status_sequence"
        if [ -f "$sequence_file" ]; then
          status=$(head -n 1 "$sequence_file")
          remaining=$(tail -n +2 "$sequence_file")
          if [ -n "$remaining" ]; then
            printf '%s\\n' "$remaining" >"$sequence_file"
          fi
        else
          status=Succeeded
        fi
        printf '%s\\n' "$status"
        exit 0
        ;;
      'job logs')
        printf 'migration replica log line with token ${ACCESS_TOKEN_MARKER}\\n'
        printf 'Npgsql connection Host=x;Password=hunter2 failed\\n'
        exit 0
        ;;
      'show '*)
        printf '%s\\n' "$(state_read api_fqdn ${API_FQDN})"
        exit 0
        ;;
      'logs show')
        printf 'api log line\\n'
        exit 0
        ;;
      'delete '*|'job delete'|'env delete')
        printf '%s\\n' "$(flag_value --ids "$@")" >>"$FAKE_STATE/deleted-resources.log"
        exit 0
        ;;
    esac
    ;;
  monitor)
    printf '%s\\n' "$(flag_value --ids "$@")" >>"$FAKE_STATE/deleted-resources.log"
    exit 0
    ;;
  deployment)
    printf 'Resource and property changes are indicated with this symbol\\n'
    exit 0
    ;;
  group)
    if [ -f "$FAKE_STATE/group_missing" ]; then
      echo "ResourceGroupNotFound" >&2
      exit 1
    fi
    case "$query" in
      *azd-env-name*) state_read group_env_tag "$(state_read current_env_name eval)" ; exit 0 ;;
      *vistara-app-registration*) state_read group_app_registration_tag template-managed ; exit 0 ;;
    esac
    exit 0
    ;;
  resource)
    case "\${2:-}" in
      list)
        resource_type=$(flag_value --resource-type "$@")
        key=$(printf '%s' "$resource_type" | tr '/.' '__')
        state_read "resources_$key" ''
        exit 0
        ;;
      delete)
        printf '%s\\n' "$(flag_value --ids "$@")" >>"$FAKE_STATE/deleted-resources.log"
        exit 0
        ;;
    esac
    ;;
  lock)
    case "\${2:-}" in
      list) state_read locks '' ; exit 0 ;;
      delete) printf '%s\\n' "$(flag_value --ids "$@")" >>"$FAKE_STATE/deleted-locks.log" ; exit 0 ;;
    esac
    ;;
  consumption)
    # Only the resource-group form addresses the budget this template creates.
    # The subscription-scoped form is answered the way Azure answers it for a
    # budget it does not address: with nothing.
    if [ "\${2:-} \${3:-}" != 'budget show-with-rg' ]; then
      echo "The budget 'x' is not found." >&2
      exit 1
    fi
    if [ -z "$(flag_value --resource-group "$@")" ] \
      || [ -z "$(flag_value --budget-name "$@")" ] \
      || [ -z "$(flag_value --subscription "$@")" ]; then
      echo "the following arguments are required" >&2
      exit 1
    fi
    printf '12.34\\n'
    exit 0
    ;;
esac

exit 0
`;

const FAKE_AZD = `${LOGGING_PREAMBLE}
environment_file() {
  printf '%s/env-%s.env' "$FAKE_STATE" "$1"
}

current_environment() {
  state_read current_env_name ''
}

# --environment NAME names an environment without selecting it, which is how
# a script reads one before it has been given permission to change anything.
target_environment() {
  local previous=''
  for argument in "$@"; do
    case "$previous" in
      --environment|-e)
        printf '%s' "$argument"
        return 0
        ;;
    esac
    previous=$argument
  done
  current_environment
}

env_get() {
  local file
  file=$(environment_file "$ENVIRONMENT_NAME")
  [ -f "$file" ] || return 1
  grep "^$1=" "$file" | tail -n 1 | sed "s/^$1=//"
}

env_set() {
  local file
  file=$(environment_file "$ENVIRONMENT_NAME")
  touch "$file"
  grep -v "^$1=" "$file" >"$file.next" 2>/dev/null || true
  printf '%s=%s\\n' "$1" "$2" >>"$file.next"
  mv "$file.next" "$file"
}

merge_outputs() {
  local source_file=$1
  [ -f "$source_file" ] || return 0
  while IFS= read -r line; do
    case "$line" in
      *=*) env_set "\${line%%=*}" "\${line#*=}" ;;
    esac
  done <"$source_file"
}

export_environment() {
  local file line
  file=$(environment_file "$ENVIRONMENT_NAME")
  [ -f "$file" ] || return 0
  while IFS= read -r line; do
    case "$line" in
      *=*) export "\${line%%=*}=\${line#*=}" ;;
    esac
  done <"$file"
}

ENVIRONMENT_NAME=$(target_environment "$@")

# azd supports exactly these output formats; a script that asks for another one
# fails before its action runs, so the stand-in fails too.
requested_format=$(flag_value --output "$@")
case "$requested_format" in
  ''|json|table|none|dotenv) ;;
  *)
    echo "ERROR: unsupported format '$requested_format'" >&2
    exit 1
    ;;
esac

case "\${1:-}" in
  version)
    printf 'azd version %s (commit deadbeef)\\n' "$(state_read azd_version 1.32.0)"
    exit 0
    ;;
  env)
    case "\${2:-}" in
      select)
        if [ ! -f "$(environment_file "\${3:-}")" ]; then
          echo "environment '\${3:-}' does not exist" >&2
          exit 1
        fi
        printf '%s' "\${3:-}" >"$FAKE_STATE/current_env_name"
        exit 0
        ;;
      new)
        printf '%s' "\${3:-}" >"$FAKE_STATE/current_env_name"
        ENVIRONMENT_NAME=\${3:-}
        : >"$(environment_file "\${3:-}")"
        env_set AZURE_ENV_NAME "\${3:-}"
        env_set AZURE_LOCATION "$(flag_value --location "$@")"
        env_set AZURE_SUBSCRIPTION_ID "$(flag_value --subscription "$@")"
        exit 0
        ;;
      set)
        env_set "\${3:-}" "\${4:-}"
        exit 0
        ;;
      get-value)
        value=$(env_get "\${3:-}" || true)
        if [ -z "$value" ]; then
          exit 1
        fi
        printf '%s\\n' "$value"
        exit 0
        ;;
      get-values)
        file=$(environment_file "$ENVIRONMENT_NAME")
        if [ ! -f "$file" ]; then
          echo "ERROR: environment '$ENVIRONMENT_NAME' does not exist" >&2
          exit 1
        fi
        while IFS= read -r line; do
          case "$line" in
            *=*) printf '%s="%s"\\n' "\${line%%=*}" "\${line#*=}" ;;
          esac
        done <"$file"
        exit 0
        ;;
      list)
        for candidate in "$FAKE_STATE"/env-*.env; do
          [ -f "$candidate" ] || continue
          candidate=\${candidate##*/env-}
          printf '%s\\tfalse\\tfalse\\n' "\${candidate%.env}"
        done
        exit 0
        ;;
    esac
    ;;
  provision)
    pass=$(( $(state_read provision_count 0) + 1 ))
    printf '%s' "$pass" >"$FAKE_STATE/provision_count"
    export_environment
    if ! bash "$VISTARA_AZURE_DIR/hooks/preprovision-preflight.sh"; then
      echo "ERROR: preprovision hook failed" >&2
      exit 1
    fi
    if grep -q "^$pass$" "$FAKE_STATE/fail_provision" 2>/dev/null; then
      echo "ERROR: deployment failed" >&2
      exit 1
    fi
    merge_outputs "$FAKE_STATE/outputs.env"
    if [ "$(env_get VISTARA_DEPLOY_APPLICATIONS || true)" = 'true' ]; then
      merge_outputs "$FAKE_STATE/outputs-pass2.env"
    fi
    export_environment
    if ! bash "$VISTARA_AZURE_DIR/hooks/postprovision.sh"; then
      echo "ERROR: postprovision hook failed" >&2
      exit 1
    fi
    exit 0
    ;;
  deploy)
    touch "$FAKE_STATE/deploy_called"
    exit 0
    ;;
  down)
    export_environment
    bash "$VISTARA_AZURE_DIR/hooks/predown-retention.sh" || exit 1
    touch "$FAKE_STATE/down_called"
    exit 0
    ;;
esac

exit 0
`;

const FAKE_CURL = `${LOGGING_PREAMBLE}
# Curl accepts either spelling of each of these, so the stand-in does too.
either_flag() {
  local short=$1
  local long=$2
  shift 2
  local value
  value=$(flag_value "$short" "$@")
  if [ -z "$value" ]; then
    value=$(flag_value "$long" "$@")
  fi
  printf '%s' "$value"
}

output_file=$(either_flag -o --output "$@")
dump_file=$(either_flag -D --dump-header "$@")
write_format=$(either_flag -w --write-out "$@")

url=''
for argument in "$@"; do
  case "$argument" in
    http://*|https://*) url=$argument ;;
  esac
done

emit_body() {
  if [ -n "$output_file" ]; then
    printf '%s' "$1" >"$output_file"
  else
    printf '%s' "$1"
  fi
}

case "$url" in
  *'/token?scope='*)
    emit_body '{"token":"fake-registry-token","expires_in":300}'
    exit 0
    ;;
  *'/manifests/'*)
    repository=\${url#*/v2/}
    repository=\${repository%%/manifests/*}
    case "$repository" in
      *-api) digest='${API_DIGEST}' ;;
      *-worker) digest='${WORKER_DIGEST}' ;;
      *-migrations) digest='${MIGRATION_DIGEST}' ;;
      *) digest='' ;;
    esac
    if [ -z "$digest" ] || [ -f "$FAKE_STATE/registry_unavailable" ]; then
      exit 22
    fi
    if [ -n "$dump_file" ]; then
      {
        printf 'HTTP/1.1 200 OK\\r\\n'
        printf 'Content-Type: application/vnd.oci.image.index.v1+json\\r\\n'
        printf 'Docker-Content-Digest: %s\\r\\n' "$digest"
        printf '\\r\\n'
      } >"$dump_file"
    fi
    exit 0
    ;;
  *'/releases/latest')
    # GitHub answers this with a redirect. Curl only follows one when it is
    # told to, so a caller that forgets -L is answered the way the real
    # command would answer it: the redirect itself, still pointing at the URL
    # that was asked for.
    follows_redirects=0
    for argument in "$@"; do
      case "$argument" in
        --location) follows_redirects=1 ;;
        -[!-]*)
          case "$argument" in
            *L*) follows_redirects=1 ;;
          esac
          ;;
      esac
    done

    final_url=$url
    status=302
    if [ "$follows_redirects" = '1' ]; then
      status=$(state_read release_status 200)
      final_url=$(state_read release_final_url "$(printf '%s' "$url" | sed 's#/releases/latest#/releases/tag/'"$(state_read release_tag v1.4.0)"'#')")
    fi

    case "$write_format" in
      *'%{http_code}'*'%{url_effective}'*) printf '%s %s' "$status" "$final_url" ;;
      *'%{url_effective}'*) printf '%s' "$final_url" ;;
      *'%{http_code}'*) printf '%s' "$status" ;;
    esac
    exit 0
    ;;
  *api.ipify.org*)
    if [ -f "$FAKE_STATE/ip_lookup_fails" ]; then
      exit 7
    fi
    printf '%s' "$(state_read client_ip 203.0.113.7)"
    exit 0
    ;;
  */health/live)
    code=$(state_read health_live 200)
    emit_body ''
    printf '%s' "$code"
    exit 0
    ;;
  */health/startup)
    code=$(state_read health_startup 200)
    emit_body ''
    printf '%s' "$code"
    exit 0
    ;;
  */health/ready)
    code=$(state_read health_ready 200)
    emit_body ''
    printf '%s' "$code"
    exit 0
    ;;
  */api/v1/setup)
    emit_body "$(state_read setup_body '{"available":true,"signInProviders":[{"id":"entra","displayName":"Microsoft Entra ID","startUrl":"/api/v1/auth/oidc/entra/start"}]}')"
    printf '%s' "$(state_read setup_code 200)"
    exit 0
    ;;
esac

exit 1
`;

const FAKE_OPENSSL = `${LOGGING_PREAMBLE}
case "\${1:-} \${2:-}" in
  'rand -base64')
    printf '%s\\n' '${PEPPER_MARKER}'
    exit 0
    ;;
esac
exit 1
`;

const FAKE_PSQL = `
if [ "\${1:-}" = '--version' ]; then
  exit 0
fi
${LOGGING_PREAMBLE}
{
  printf 'PGPASSFILE=%s\\n' "\${PGPASSFILE:-}"
  if [ -n "\${PGPASSFILE:-}" ] && [ -f "\${PGPASSFILE}" ]; then
    printf 'MODE=%s\\n' "$(stat -c '%a' "$PGPASSFILE" 2>/dev/null || stat -f '%Lp' "$PGPASSFILE" 2>/dev/null)"
    printf 'CONTENT=%s\\n' "$(cat "$PGPASSFILE")"
  fi
  printf 'PGPASSWORD=%s\\n' "\${PGPASSWORD:-unset}"
} >>"$FAKE_STATE/psql-environment.log"

sql_file=$(flag_value --file "$@")
if [ -n "$sql_file" ] && [ -f "$sql_file" ]; then
  cat "$sql_file" >>"$FAKE_STATE/psql-sql.log"
fi

if [ -f "$FAKE_STATE/psql_fails" ]; then
  echo "psql: error: connection to server failed" >&2
  exit 2
fi
exit 0
`;

const FAKE_DOCKER = `
if [ "\${1:-}" = 'version' ]; then
  exit 0
fi
${LOGGING_PREAMBLE}
if [ -f "$FAKE_STATE/docker_psql_fails" ]; then
  echo "psql: error: connection to server failed" >&2
  exit 2
fi
exit 0
`;

const FAKE_OPEN = `${LOGGING_PREAMBLE}
exit 0
`;

/**
 * Builds a sandbox with every external command replaced and the `azd`
 * environment pre-populated the way a real run would leave it.
 */
export function createSandbox(name, options = {}) {
  sandboxCounter += 1;
  const root = join(SANDBOX_ROOT, `${name}-${process.pid}-${sandboxCounter}`);
  const binDirectory = join(root, 'bin');
  const stateDirectory = join(root, 'state');
  const runDirectory = join(root, 'run');
  const homeDirectory = join(root, 'home');
  const bashEnvironment = join(root, 'bash-env');

  for (const directory of [binDirectory, stateDirectory, runDirectory, homeDirectory]) {
    mkdirSync(directory, { recursive: true });
  }

  writeFileSync(
    bashEnvironment,
    `command() {
  if [ "\${1:-}" = '-v' ] && [ -n "\${2:-}" ] \\
    && [ -f "$FAKE_STATE/missing-command-\${2}" ]; then
    return 1
  fi
  builtin command "$@"
}
`,
    'utf8',
  );
  writeFileSync(join(stateDirectory, 'calls.log'), '', 'utf8');
  writeFileSync(join(stateDirectory, 'bodies.log'), '', 'utf8');

  writeFake(binDirectory, 'az', FAKE_AZ);
  writeFake(binDirectory, 'azd', FAKE_AZD);
  writeFake(binDirectory, 'curl', FAKE_CURL);
  writeFake(binDirectory, 'openssl', FAKE_OPENSSL);
  writeFake(binDirectory, 'psql', FAKE_PSQL);
  writeFake(binDirectory, 'docker', FAKE_DOCKER);
  writeFake(binDirectory, 'open', FAKE_OPEN);
  writeFake(binDirectory, 'xdg-open', FAKE_OPEN);

  const sandbox = {
    root,
    binDirectory,
    stateDirectory,
    runDirectory,
    homeDirectory,
    bashEnvironment,
    state(file, contents) {
      writeFileSync(join(stateDirectory, file), contents, 'utf8');
      return sandbox;
    },
    touch(file) {
      writeFileSync(join(stateDirectory, file), '', 'utf8');
      return sandbox;
    },
    remove(file) {
      rmSync(join(stateDirectory, file), { force: true });
      return sandbox;
    },
    removeFake(name) {
      writeFileSync(join(stateDirectory, `missing-command-${name}`), '', 'utf8');
      writeFake(binDirectory, name, 'exit 127');
      return sandbox;
    },
    has(file) {
      return existsSync(join(stateDirectory, file));
    },
    read(file) {
      const path = join(stateDirectory, file);
      return existsSync(path) ? readFileSync(path, 'utf8') : '';
    },
    /** Every recorded invocation, as an array of argument arrays. */
    calls() {
      return sandbox
        .read('calls.log')
        .split('\n')
        .filter((line) => line.length > 0)
        .map((line) => line.split('\t'));
    },
    callsFor(tool) {
      return sandbox.calls().filter((call) => call[0] === tool);
    },
    /** The `azd` environment as the fake persisted it. */
    environment(environmentName) {
      const contents = sandbox.read(`env-${environmentName}.env`);
      const values = {};
      for (const line of contents.split('\n')) {
        if (!line.includes('=')) {
          continue;
        }
        values[line.slice(0, line.indexOf('='))] = line.slice(line.indexOf('=') + 1);
      }
      return values;
    },
    cleanup() {
      rmSync(root, { recursive: true, force: true });
    },
  };

  const defaultOutputs = [
    `AZURE_TENANT_ID=${TENANT_ID}`,
    `AZURE_RESOURCE_GROUP=${RESOURCE_GROUP}`,
    `SERVICE_API_URI=${SERVICE_API_URI}`,
    `API_IDENTITY_PRINCIPAL_ID=${API_PRINCIPAL_ID}`,
    'API_IDENTITY_CLIENT_ID=aaaaaaaa-0000-0000-0000-000000000001',
    `WORKER_IDENTITY_PRINCIPAL_ID=${WORKER_PRINCIPAL_ID}`,
    `MIGRATE_IDENTITY_PRINCIPAL_ID=${MIGRATE_PRINCIPAL_ID}`,
    `POSTGRES_HOST=${POSTGRES_HOST}`,
    'POSTGRES_DATABASE=vistara',
    'POSTGRES_API_ROLE=vistara_api_runtime',
    'POSTGRES_WORKER_ROLE=vistara_worker_runtime',
    'POSTGRES_MIGRATOR_ROLE=vistara_migrator',
    `AZURE_KEY_VAULT_ENDPOINT=${VAULT_ENDPOINT}`,
    `MIGRATION_JOB_NAME=${MIGRATION_JOB_NAME}`,
    '',
  ].join('\n');

  const pass2Outputs = [
    `API_CONTAINER_APP_NAME=${API_CONTAINER_APP_NAME}`,
    `WORKER_CONTAINER_APP_NAME=${WORKER_CONTAINER_APP_NAME}`,
    '',
  ].join('\n');

  sandbox.state('outputs.env', options.outputs ?? defaultOutputs);
  sandbox.state('outputs-pass2.env', options.pass2Outputs ?? pass2Outputs);

  return sandbox;
}

/** Runs a bootstrap script inside the sandbox and captures everything. */
export function run(sandbox, script, argumentList, options = {}) {
  const result = spawnSync('bash', [script, ...argumentList], {
    encoding: 'utf8',
    cwd: options.cwd ?? REPOSITORY_ROOT,
    input: options.input ?? '',
    timeout: options.timeout ?? 120_000,
    env: {
      PATH: `${sandbox.binDirectory}:/usr/bin:/bin:/usr/sbin:/sbin`,
      BASH_ENV: sandbox.bashEnvironment,
      HOME: sandbox.homeDirectory,
      FAKE_STATE: sandbox.stateDirectory,
      VISTARA_STATE_DIR: sandbox.runDirectory,
      VISTARA_AZURE_DIR: AZURE_DIR,
      VISTARA_MIGRATION_POLL_SECONDS: '0',
      VISTARA_HEALTH_POLL_SECONDS: '0',
      VISTARA_PROVIDER_POLL_SECONDS: '0',
      VISTARA_ROLE_PROPAGATION_POLL_SECONDS: '0',
      VISTARA_MIGRATION_TIMEOUT_SECONDS: '2',
      VISTARA_HEALTH_TIMEOUT_SECONDS: '2',
      VISTARA_ROLE_PROPAGATION_TIMEOUT_SECONDS: '2',
      VISTARA_PROVIDER_TIMEOUT_SECONDS: '2',
      LC_ALL: 'C',
      ...options.env,
    },
  });
  return {
    status: result.status,
    stdout: result.stdout ?? '',
    stderr: result.stderr ?? '',
    output: `${result.stdout ?? ''}${result.stderr ?? ''}`,
  };
}

/**
 * Runs a script attached to a real terminal, so the confirmation prompts are
 * reached instead of refused for want of one. Standard input is closed, which
 * a prompt reads as an empty answer: the declined path, and the only
 * interactive answer that can be delivered reliably through a pty.
 */
export function runInteractive(sandbox, script, argumentList, options = {}) {
  const command = ['bash', script, ...argumentList];
  const quoted = command.map((part) => `'${part.split("'").join(`'\''`)}'`).join(' ');
  const scriptArguments =
    process.platform === 'linux'
      ? ['-qec', quoted, '/dev/null']
      : ['-q', '/dev/null', ...command];

  const result = spawnSync('script', scriptArguments, {
    encoding: 'utf8',
    cwd: options.cwd ?? REPOSITORY_ROOT,
    // script(1) reads terminal settings from its own standard input, which a
    // pipe cannot answer; /dev/null can, and the pty it then allocates for the
    // child is what the prompts need.
    stdio: ['ignore', 'pipe', 'pipe'],
    timeout: options.timeout ?? 120_000,
    env: {
      PATH: `${sandbox.binDirectory}:/usr/bin:/bin:/usr/sbin:/sbin`,
      BASH_ENV: sandbox.bashEnvironment,
      HOME: sandbox.homeDirectory,
      FAKE_STATE: sandbox.stateDirectory,
      VISTARA_STATE_DIR: sandbox.runDirectory,
      VISTARA_AZURE_DIR: AZURE_DIR,
      LC_ALL: 'C',
      TERM: 'dumb',
      ...options.env,
    },
  });

  const output = `${result.stdout ?? ''}${result.stderr ?? ''}`.split('\r').join('');
  return { status: result.status, output };
}

export function interactiveRunsAvailable() {
  try {
    execFileSync('script', ['-q', '/dev/null', 'true'], { stdio: 'ignore', timeout: 10_000 });
    return true;
  } catch {
    try {
      execFileSync('script', ['-qec', 'true', '/dev/null'], { stdio: 'ignore', timeout: 10_000 });
      return true;
    } catch {
      return false;
    }
  }
}

/** The arguments `up.sh` needs to run without prompting. */
export function upArguments(overrides = []) {
  return [
    '--env-name',
    'eval',
    '--location',
    'eastus',
    '--image-namespace',
    'ghcr.io/cody-sims',
    '--release',
    'v1.4.0',
    '--owner-object-id',
    SIGNED_IN_OBJECT_ID,
    '--yes',
    '--no-open',
    ...overrides,
  ];
}

export function shellcheckAvailable() {
  try {
    execFileSync('shellcheck', ['--version'], { stdio: 'ignore' });
    return true;
  } catch {
    return false;
  }
}

export function cleanupSandboxRoot() {
  rmSync(SANDBOX_ROOT, { recursive: true, force: true });
}
