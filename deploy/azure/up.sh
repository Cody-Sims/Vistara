#!/usr/bin/env bash
# Vistara hosted bootstrap — one command from a laptop to a running deployment.
#
#   ./deploy/azure/up.sh
#
# What it does, and why it takes two provisioning passes:
#
#   The Entra application registration needs the API host name, which does not
#   exist until the Container Apps environment is deployed. The API key pepper
#   needs the Key Vault, which does not exist either. The API container needs
#   both, and refuses to start without the pepper. So the first pass creates
#   everything except the API and worker, the registration and the pepper are
#   created against real outputs, the database roles are added and the schema
#   is migrated, and the second pass turns the applications on with the client
#   ID and the secret reference in place.
#
# Every step is idempotent. A failed run never deletes anything: rerun the same
# command and it resumes at the first step that has not completed.
#
# Exit codes: 0 ok · 64 usage · 69 missing tool · 70 provisioning failure ·
#             71 migration failure · 75 health timeout · 77 insufficient permissions
if [ -z "${BASH_VERSION:-}" ]; then
  exec bash "$0" "$@"
fi

set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

# shellcheck source=hooks/lib/common.sh
. "${SCRIPT_DIR}/hooks/lib/common.sh"

DEFAULT_ENV_NAME='vistara-eval'
DEFAULT_BUDGET_AMOUNT=25
DEFAULT_DB_SKU='Standard_B1ms'
DEFAULT_MAX_REPLICAS=2
DEFAULT_IMAGE_REPOSITORY_PREFIX='vistara'

usage() {
  cat <<'USAGE'
Usage: ./deploy/azure/up.sh [options]

  --env-name NAME             azd environment (default: vistara-eval)
  --location REGION           Azure region (default: $AZURE_LOCATION, or prompt)
  --subscription ID           Azure subscription (default: current az account)
  --owner-object-id GUID      first-owner Entra oid (default: the signed-in user)
  --tenant-id GUID            Entra tenant (default: the signed-in tenant)
  --image-namespace NS        default: ghcr.io/<repository owner, lowercased>
  --release TAG               resolve digests from this release tag (default: latest release)
  --api-digest sha256:...     override; must be a digest, tags are rejected
  --worker-digest sha256:...
  --migration-digest sha256:...
  --budget-amount N           default 25 (USD)
  --db-sku SKU                default Standard_B1ms
  --max-replicas N            default 2
  --custom-domain HOST        optional
  --client-ip ADDRESS         address to allow through the PostgreSQL firewall
  --skip-app-registration     use --client-id instead of creating a registration
  --client-id GUID            existing app registration
  --what-if                   run az deployment sub what-if and exit
  --yes                       non-interactive; fail instead of prompting
  --no-open                   do not launch a browser at the end
  -h, --help                  show this help

Exit codes: 0 ok · 64 usage · 69 missing tool · 70 provisioning failure ·
            71 migration failure · 75 health timeout · 77 insufficient permissions
USAGE
}

# ---------------------------------------------------------------------------
# Arguments
# ---------------------------------------------------------------------------

env_name=''
location=${AZURE_LOCATION:-}
subscription_id=''
owner_object_id=''
owner_object_id_explicit=0
tenant_id=''
image_namespace=''
release_tag=''
api_digest=''
worker_digest=''
migration_digest=''
budget_amount=''
db_sku=''
max_replicas=''
custom_domain=''
client_ip=''
client_id=''
skip_app_registration=0
what_if=0

require_value() {
  if [ "$#" -lt 2 ] || [ -z "${2:-}" ]; then
    vistara_error "option $1 requires a value."
    usage >&2
    exit "$VISTARA_EXIT_USAGE"
  fi
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --env-name) require_value "$1" "${2:-}"; env_name=$2; shift 2 ;;
    --location) require_value "$1" "${2:-}"; location=$2; shift 2 ;;
    --subscription) require_value "$1" "${2:-}"; subscription_id=$2; shift 2 ;;
    --owner-object-id) require_value "$1" "${2:-}"; owner_object_id=$2; owner_object_id_explicit=1; shift 2 ;;
    --tenant-id) require_value "$1" "${2:-}"; tenant_id=$2; shift 2 ;;
    --image-namespace) require_value "$1" "${2:-}"; image_namespace=$2; shift 2 ;;
    --release) require_value "$1" "${2:-}"; release_tag=$2; shift 2 ;;
    --api-digest) require_value "$1" "${2:-}"; api_digest=$2; shift 2 ;;
    --worker-digest) require_value "$1" "${2:-}"; worker_digest=$2; shift 2 ;;
    --migration-digest) require_value "$1" "${2:-}"; migration_digest=$2; shift 2 ;;
    --budget-amount) require_value "$1" "${2:-}"; budget_amount=$2; shift 2 ;;
    --db-sku) require_value "$1" "${2:-}"; db_sku=$2; shift 2 ;;
    --max-replicas) require_value "$1" "${2:-}"; max_replicas=$2; shift 2 ;;
    --custom-domain) require_value "$1" "${2:-}"; custom_domain=$2; shift 2 ;;
    --client-ip) require_value "$1" "${2:-}"; client_ip=$2; shift 2 ;;
    --client-id) require_value "$1" "${2:-}"; client_id=$2; shift 2 ;;
    --skip-app-registration) skip_app_registration=1; shift ;;
    --what-if) what_if=1; shift ;;
    --yes|-y) export VISTARA_ASSUME_YES=1; shift ;;
    --no-open) export VISTARA_NO_OPEN=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *)
      vistara_error "unknown option '$1'."
      usage >&2
      exit "$VISTARA_EXIT_USAGE"
      ;;
  esac
done

env_name=${env_name:-${AZURE_ENV_NAME:-$DEFAULT_ENV_NAME}}
budget_amount=${budget_amount:-$DEFAULT_BUDGET_AMOUNT}
db_sku=${db_sku:-$DEFAULT_DB_SKU}
max_replicas=${max_replicas:-$DEFAULT_MAX_REPLICAS}

environment_name_pattern='^[a-zA-Z0-9][a-zA-Z0-9-]{1,30}[a-zA-Z0-9]$'
if ! [[ $env_name =~ $environment_name_pattern ]]; then
  vistara_die "$VISTARA_EXIT_USAGE" \
    "--env-name must be 3-32 alphanumeric or dash characters, got '${env_name}'. It names Azure resources."
fi

if ! [[ $budget_amount =~ ^[0-9]+$ ]] || [ "$budget_amount" -lt 1 ]; then
  vistara_die "$VISTARA_EXIT_USAGE" "--budget-amount must be a positive whole number of dollars, got '${budget_amount}'."
fi

if ! [[ $max_replicas =~ ^[0-9]+$ ]] || [ "$max_replicas" -lt 1 ]; then
  vistara_die "$VISTARA_EXIT_USAGE" "--max-replicas must be at least 1, got '${max_replicas}'."
fi

if [ "$skip_app_registration" = '1' ] && [ -z "$client_id" ]; then
  vistara_die "$VISTARA_EXIT_USAGE" \
    '--skip-app-registration needs --client-id: without a registration the deployment has nothing to sign in against.'
fi

if [ -n "$client_id" ]; then
  vistara_require_guid "$client_id" '--client-id'
  skip_app_registration=1
fi

for digest_value in "$api_digest" "$worker_digest" "$migration_digest"; do
  [ -n "$digest_value" ] || continue
  if ! vistara_is_digest "$digest_value"; then
    vistara_die "$VISTARA_EXIT_USAGE" \
      "image overrides must be digests of the form sha256:<64 hex>, got '${digest_value}'. A tag can be repointed after review."
  fi
done

export AZURE_ENV_NAME="$env_name"
# Every azd read and write below names this environment rather than relying on
# whichever one is selected, so a preview can read an environment it has not
# selected and a run cannot write into someone else's.
export VISTARA_AZD_ENVIRONMENT="$env_name"
export VISTARA_STATE_DIR="${VISTARA_STATE_DIR:-${SCRIPT_DIR}/.azure/${env_name}/.vistara}"

# ---------------------------------------------------------------------------
# Tools and account, before anything is created
# ---------------------------------------------------------------------------

vistara_step "Vistara hosted bootstrap — environment ${env_name}"

vistara_require_command az 'Install the Azure CLI: https://aka.ms/azure-cli'
vistara_require_command azd 'Install the Azure Developer CLI: https://aka.ms/azd-install'
vistara_require_command curl 'Install curl.'
vistara_require_command openssl 'Install openssl; it generates the API key pepper.'

# A missing browser opener is not a reason to refuse a deployment — the URL is
# always printed — but the operator should hear about it now rather than at the
# end of a twenty minute run.
if [ "${VISTARA_NO_OPEN:-0}" != '1' ] \
  && ! vistara_have_command open && ! vistara_have_command xdg-open; then
  vistara_warn 'no browser opener (open or xdg-open) is available; the sign-in URL will only be printed.'
  export VISTARA_NO_OPEN=1
fi

if ! account_json_id=$(az account show --query id --output tsv 2>/dev/null); then
  vistara_die "$VISTARA_EXIT_PERMISSION" 'the Azure CLI is not signed in. Run: az login'
fi
account_json_id=$(printf '%s' "$account_json_id" | tr -d '\r\n')

if [ -z "$subscription_id" ]; then
  subscription_id=$account_json_id
fi

# Read the subscription by name rather than switching to it. Selecting a
# subscription changes the operator's CLI for every other shell they have open,
# and nothing should change until the summary below has been accepted.
if ! subscription_name=$(az account show --subscription "$subscription_id" --query name --output tsv 2>/dev/null); then
  vistara_die "$VISTARA_EXIT_PERMISSION" \
    "subscription ${subscription_id} is not available to this sign-in. Check: az account list --output table"
fi
subscription_name=$(printf '%s' "$subscription_name" | tr -d '\r\n')
subscription_tenant=$(az account show --subscription "$subscription_id" --query tenantId --output tsv 2>/dev/null | tr -d '\r\n' || true)
active_tenant=$(az account show --query tenantId --output tsv 2>/dev/null | tr -d '\r\n' || true)

# `az ad` has no subscription or tenant switch: it answers for whichever tenant
# the CLI is currently pointed at. So when the subscription being deployed into
# belongs to a different tenant than the active one, the signed-in object ID
# read below would come from the wrong directory — an owner allowlist and a
# PostgreSQL administrator naming a principal that does not exist where the
# deployment trusts it. Rather than silently switch the operator's CLI before
# they have agreed to anything, say so and let them point it themselves.
if [ -n "$subscription_tenant" ] && [ -n "$active_tenant" ] && [ "$subscription_tenant" != "$active_tenant" ]; then
  vistara_die "$VISTARA_EXIT_USAGE" \
    "subscription ${subscription_id} belongs to tenant ${subscription_tenant} but the Azure CLI is signed in to ${active_tenant}, so directory lookups would answer for the wrong tenant. Run: az account set --subscription ${subscription_id} (or az login --tenant ${subscription_tenant}) and rerun."
fi

tenant_id=${tenant_id:-$subscription_tenant}
vistara_require_guid "$tenant_id" '--tenant-id'

signed_in_object_id=$(az ad signed-in-user show --query id --output tsv 2>/dev/null | tr -d '\r\n' || true)
signed_in_name=$(az ad signed-in-user show --query userPrincipalName --output tsv 2>/dev/null | tr -d '\r\n' || true)
if [ -z "$signed_in_name" ]; then
  signed_in_name=$(az account show --subscription "$subscription_id" --query user.name --output tsv 2>/dev/null | tr -d '\r\n' || true)
fi

if [ -z "$owner_object_id" ]; then
  owner_object_id=$signed_in_object_id
fi
if [ -z "$owner_object_id" ]; then
  vistara_die "$VISTARA_EXIT_USAGE" \
    'could not resolve the signed-in directory object ID. Pass --owner-object-id <GUID>; it is the only account allowed to claim ownership.'
fi
vistara_require_guid "$owner_object_id" '--owner-object-id'

# The first owner is the one account in the directory that may claim this
# deployment. Defaulting it silently would be the wrong kind of convenient, so
# an unattended run has to name it explicitly.
if vistara_assume_yes && [ "$owner_object_id_explicit" != '1' ]; then
  vistara_die "$VISTARA_EXIT_USAGE" \
    "--yes requires --owner-object-id: the first owner claims the deployment on first sign-in and must be stated, not inferred. The signed-in account is ${owner_object_id}."
fi

if [ -z "$location" ]; then
  if vistara_assume_yes; then
    vistara_die "$VISTARA_EXIT_USAGE" '--yes requires --location (or AZURE_LOCATION).'
  fi
  printf 'Azure region (for example eastus, westeurope): ' >&2
  IFS= read -r location || location=''
fi
if [ -z "$location" ]; then
  vistara_die "$VISTARA_EXIT_USAGE" 'a location is required.'
fi

# ---------------------------------------------------------------------------
# Container images
#
# The templates only accept digests. A tag can be moved after it was reviewed,
# and a rollback that depends on `:latest` is not a rollback.
# ---------------------------------------------------------------------------

resolve_repository_owner() {
  local remote=''
  remote=$(git -C "$SCRIPT_DIR" remote get-url origin 2>/dev/null || true)
  case "$remote" in
    *github.com[:/]*)
      remote=${remote#*github.com}
      remote=${remote#:}
      remote=${remote#/}
      printf '%s' "${remote%%/*}" | tr '[:upper:]' '[:lower:]'
      ;;
    *) printf '' ;;
  esac
}

resolve_repository_name() {
  local remote=''
  remote=$(git -C "$SCRIPT_DIR" remote get-url origin 2>/dev/null || true)
  case "$remote" in
    *github.com[:/]*)
      remote=${remote#*github.com}
      remote=${remote#:}
      remote=${remote#/}
      remote=${remote#*/}
      remote=${remote%.git}
      printf '%s' "$remote"
      ;;
    *) printf '' ;;
  esac
}

if [ -z "$image_namespace" ]; then
  repository_owner=$(resolve_repository_owner)
  if [ -z "$repository_owner" ]; then
    vistara_die "$VISTARA_EXIT_USAGE" \
      'could not derive the image namespace from the git remote. Pass --image-namespace ghcr.io/<owner>.'
  fi
  image_namespace="ghcr.io/${repository_owner}"
fi
image_namespace=${image_namespace%/}

# Resolves a tag to the immutable digest the registry currently serves. The
# bearer token goes into a curl config file rather than the command line.
resolve_digest_for_tag() {
  local repository=$1
  local tag=$2
  local registry_host=${image_namespace%%/*}
  local repository_path="${image_namespace#*/}/${repository}"
  local token_file config_file header_file token digest

  token_file=$(vistara_private_file 'registry-token.json')
  config_file=$(vistara_private_file 'registry-curl.conf')
  header_file=$(vistara_private_file 'registry-headers.txt')

  if ! curl -fsS --max-time 30 \
    -o "$token_file" \
    "https://${registry_host}/token?scope=repository:${repository_path}:pull&service=${registry_host}" 2>/dev/null; then
    vistara_shred "$token_file" "$config_file" "$header_file"
    printf ''
    return 0
  fi

  token=$(sed -n 's/.*"token"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$token_file" | head -n 1)
  vistara_shred "$token_file"
  if [ -z "$token" ]; then
    vistara_shred "$config_file" "$header_file"
    printf ''
    return 0
  fi

  {
    printf 'header = "Authorization: Bearer %s"\n' "$token"
    printf 'header = "Accept: application/vnd.oci.image.index.v1+json"\n'
    printf 'header = "Accept: application/vnd.oci.image.manifest.v1+json"\n'
    printf 'header = "Accept: application/vnd.docker.distribution.manifest.list.v2+json"\n'
    printf 'header = "Accept: application/vnd.docker.distribution.manifest.v2+json"\n'
  } >"$config_file"

  if ! curl -fsS --max-time 30 \
    --config "$config_file" \
    --head \
    -o /dev/null \
    -D "$header_file" \
    "https://${registry_host}/v2/${repository_path}/manifests/${tag}" 2>/dev/null; then
    vistara_shred "$config_file" "$header_file"
    printf ''
    return 0
  fi
  vistara_shred "$config_file"

  digest=$(sed -n 's/^[Dd]ocker-[Cc]ontent-[Dd]igest:[[:space:]]*\(sha256:[0-9a-f]*\).*/\1/p' "$header_file" | tail -n 1)
  vistara_shred "$header_file"
  printf '%s' "$digest"
}

if [ -z "$api_digest" ] || [ -z "$worker_digest" ] || [ -z "$migration_digest" ]; then
  if [ -z "$release_tag" ]; then
    repository_owner=$(resolve_repository_owner)
    repository_name=$(resolve_repository_name)
    if [ -n "$repository_owner" ] && [ -n "$repository_name" ]; then
      vistara_log 'resolving the latest published release.'
      release_tag=$(vistara_resolve_latest_release_tag "$repository_owner" "$repository_name" || true)
    fi
  fi
  if [ -z "$release_tag" ]; then
    vistara_die "$VISTARA_EXIT_USAGE" \
      'no release tag could be resolved. Pass --release <tag>, or pin every image with --api-digest, --worker-digest, and --migration-digest.'
  fi
  vistara_log "resolving image digests for release ${release_tag} from ${image_namespace}."
fi

if [ -z "$api_digest" ]; then
  api_digest=$(resolve_digest_for_tag "${DEFAULT_IMAGE_REPOSITORY_PREFIX}-api" "$release_tag")
fi
if [ -z "$worker_digest" ]; then
  worker_digest=$(resolve_digest_for_tag "${DEFAULT_IMAGE_REPOSITORY_PREFIX}-worker" "$release_tag")
fi
if [ -z "$migration_digest" ]; then
  migration_digest=$(resolve_digest_for_tag "${DEFAULT_IMAGE_REPOSITORY_PREFIX}-migrations" "$release_tag")
fi

for pair in "api:${api_digest}" "worker:${worker_digest}" "migration:${migration_digest}"; do
  role=${pair%%:*}
  value=${pair#*:}
  if ! vistara_is_digest "$value"; then
    vistara_die "$VISTARA_EXIT_USAGE" \
      "could not resolve an immutable digest for the ${role} image from ${image_namespace} at '${release_tag}'. Pass --${role}-digest sha256:<64 hex>, or check that the release published a public image."
  fi
done

api_image="${image_namespace}/${DEFAULT_IMAGE_REPOSITORY_PREFIX}-api@${api_digest}"
worker_image="${image_namespace}/${DEFAULT_IMAGE_REPOSITORY_PREFIX}-worker@${worker_digest}"
migration_image="${image_namespace}/${DEFAULT_IMAGE_REPOSITORY_PREFIX}-migrations@${migration_digest}"

# ---------------------------------------------------------------------------
# Confirmation
# ---------------------------------------------------------------------------

vistara_log ''
vistara_log "  subscription      ${subscription_name:-unknown} (${subscription_id})"
vistara_log "  tenant            ${tenant_id}"
vistara_log "  environment       ${env_name}"
vistara_log "  location          ${location}"
vistara_log "  first owner       ${owner_object_id}${signed_in_name:+ (${signed_in_name})}"
vistara_log "  api image         ${api_image}"
vistara_log "  worker image      ${worker_image}"
vistara_log "  migration image   ${migration_image}"
vistara_log "  monthly budget    \$${budget_amount}"
vistara_log "  database sku      ${db_sku}"
vistara_log ''
vistara_log "The first owner is the only account that may claim this deployment: the first sign-in"
vistara_log "by object ID ${owner_object_id} in tenant ${tenant_id} becomes the owner, and nobody else can."
vistara_log ''

if [ "$what_if" != '1' ]; then
  if ! vistara_confirm 'Create or update this deployment?'; then
    vistara_log 'Nothing was changed.'
    exit 0
  fi
  if ! vistara_confirm "Confirm ${owner_object_id} as the first owner?"; then
    vistara_die "$VISTARA_EXIT_USAGE" 'the first owner was not confirmed. Rerun with --owner-object-id <GUID>.'
  fi
fi

# ---------------------------------------------------------------------------
# What-if
#
# A preview changes nothing: not the operator's selected subscription, not the
# azd environment, not the recorded budget period. Every value it needs is
# either already in hand or read back from an environment that already exists.
# ---------------------------------------------------------------------------

cd "$SCRIPT_DIR"

budget_start_date=$(vistara_env VISTARA_BUDGET_START_DATE)
if [ -z "$budget_start_date" ]; then
  budget_start_date=$(date -u +%Y-%m-01)
fi

if [ "$what_if" = '1' ]; then
  vistara_step 'Deployment what-if'
  az deployment sub what-if \
    --location "$location" \
    --subscription "$subscription_id" \
    --template-file "${SCRIPT_DIR}/infra/main.bicep" \
    --parameters \
      environmentName="$env_name" \
      location="$location" \
      apiImage="$api_image" \
      workerImage="$worker_image" \
      migrationImage="$migration_image" \
      entraTenantId="$tenant_id" \
      firstOwnerObjectId="$owner_object_id" \
      postgresEntraAdminObjectId="${signed_in_object_id:-$owner_object_id}" \
      postgresEntraAdminPrincipalName="${signed_in_name:-$owner_object_id}" \
      postgresSku="$db_sku" \
      apiMaxReplicas="$max_replicas" \
      budgetAmount="$budget_amount" \
      budgetStartDate="$budget_start_date" \
      deployApplications=false \
    || vistara_die "$VISTARA_EXIT_PROVISION" 'what-if failed.'
  exit 0
fi

# ---------------------------------------------------------------------------
# From here on the run changes things
# ---------------------------------------------------------------------------

# The CLI is pointed at the subscription now, after the confirmation, because
# the preflight hook refuses a deployment whose active subscription is not the
# one the environment provisions into.
if [ "$subscription_id" != "$account_json_id" ]; then
  vistara_log "switching the Azure CLI to subscription ${subscription_id}."
  if ! az account set --subscription "$subscription_id" >/dev/null 2>&1; then
    vistara_die "$VISTARA_EXIT_PERMISSION" "could not select subscription ${subscription_id}. Check: az account list --output table"
  fi
fi

if azd env select "$env_name" >/dev/null 2>&1; then
  vistara_log "using the existing azd environment ${env_name}."
else
  vistara_log "creating the azd environment ${env_name}."
  if ! azd env new "$env_name" --location "$location" --subscription "$subscription_id" --no-prompt >/dev/null 2>&1; then
    vistara_die "$VISTARA_EXIT_PROVISION" "could not create the azd environment ${env_name}."
  fi
fi

vistara_azd_env_set AZURE_LOCATION "$location"
vistara_azd_env_set AZURE_SUBSCRIPTION_ID "$subscription_id"
vistara_azd_env_set AZURE_TENANT_ID "$tenant_id"
vistara_azd_env_set VISTARA_API_IMAGE "$api_image"
vistara_azd_env_set VISTARA_WORKER_IMAGE "$worker_image"
vistara_azd_env_set VISTARA_MIGRATION_IMAGE "$migration_image"
vistara_azd_env_set VISTARA_FIRST_OWNER_OBJECT_ID "$owner_object_id"
vistara_azd_env_set VISTARA_POSTGRES_ADMIN_OBJECT_ID "${signed_in_object_id:-$owner_object_id}"
vistara_azd_env_set VISTARA_POSTGRES_ADMIN_PRINCIPAL_NAME "${signed_in_name:-$owner_object_id}"
vistara_azd_env_set VISTARA_POSTGRES_ADMIN_PRINCIPAL_TYPE 'User'
vistara_azd_env_set VISTARA_POSTGRES_SKU "$db_sku"
vistara_azd_env_set VISTARA_API_MAX_REPLICAS "$max_replicas"
vistara_azd_env_set VISTARA_BUDGET_AMOUNT "$budget_amount"
vistara_azd_env_set VISTARA_CUSTOM_DOMAIN_NAME "$custom_domain"
vistara_azd_env_set VISTARA_RETAIN_DATA 'true'

if [ -n "$client_ip" ]; then
  vistara_azd_env_set VISTARA_CLIENT_IP "$client_ip"
fi

if [ "$skip_app_registration" = '1' ]; then
  vistara_azd_env_set VISTARA_DEPLOY_APP_REGISTRATION 'false'
  vistara_azd_env_set VISTARA_APPLICATION_CLIENT_ID "$client_id"
else
  vistara_azd_env_set VISTARA_DEPLOY_APP_REGISTRATION 'true'
fi

# Cost Management accrues a budget against the month it starts in. Recomputing
# the start date on every deployment would silently rebase the accrual, so it
# is written once and then left alone for the life of the environment.
existing_budget_start=$(vistara_env VISTARA_BUDGET_START_DATE)
if [ -z "$existing_budget_start" ]; then
  vistara_azd_env_set VISTARA_BUDGET_START_DATE "$budget_start_date"
  vistara_log "budget period starts ${budget_start_date}."
else
  vistara_log "keeping the existing budget start date ${existing_budget_start}."
fi

# ---------------------------------------------------------------------------
# Provisioning
# ---------------------------------------------------------------------------

rerun_command="./deploy/azure/up.sh --env-name ${env_name}"

# `azd` collapses every hook failure into its own exit status, so the hooks
# record the specified code and it is recovered here.
provision_failure_code() {
  local recorded=''
  local directory
  directory=$(vistara_run_dir) || true
  if [ -n "${directory:-}" ] && [ -f "${directory}/last-error-code" ]; then
    recorded=$(tr -d '\r\n' <"${directory}/last-error-code")
  fi
  case "$recorded" in
    64|69|70|71|75|77) printf '%s' "$recorded" ;;
    *) printf '%s' "$VISTARA_EXIT_PROVISION" ;;
  esac
}

run_provision() {
  local label=$1
  local status=0
  vistara_clear_failure
  vistara_step "$label"
  azd provision --no-prompt || status=$?
  if [ "$status" -ne 0 ]; then
    local code
    code=$(provision_failure_code)
    vistara_error "${label} failed. Nothing was deleted."
    vistara_error "Fix the reported problem and rerun the same command; it resumes where it stopped:"
    vistara_error "  ${rerun_command}"
    exit "$code"
  fi
}

vistara_azd_env_set VISTARA_DEPLOY_APPLICATIONS 'false'
run_provision 'Pass 1 of 2: platform, identity, database, and migration'

client_id_after_pass_one=$(vistara_env VISTARA_APPLICATION_CLIENT_ID)
pepper_uri_after_pass_one=$(vistara_env VISTARA_API_KEY_PEPPER_SECRET_URI)

if [ -z "$client_id_after_pass_one" ]; then
  vistara_die "$VISTARA_EXIT_PROVISION" \
    "the first pass finished without an application client ID. Rerun: ${rerun_command}"
fi
if [ -z "$pepper_uri_after_pass_one" ]; then
  vistara_die "$VISTARA_EXIT_PROVISION" \
    "the first pass finished without an API key pepper reference. Rerun: ${rerun_command}"
fi

vistara_azd_env_set VISTARA_DEPLOY_APPLICATIONS 'true'
run_provision 'Pass 2 of 2: API and worker'

# ---------------------------------------------------------------------------
# Done
# ---------------------------------------------------------------------------

vistara_load_env

run_directory=$(vistara_run_dir)
sign_in_url=''
if [ -f "${run_directory}/sign-in-url" ]; then
  sign_in_url=$(tr -d '\r\n' <"${run_directory}/sign-in-url")
fi
if [ -z "$sign_in_url" ]; then
  sign_in_url="$(vistara_env SERVICE_API_URI)/login"
fi

cat >&2 <<SUMMARY

Vistara is deployed.

  sign in       ${sign_in_url}
  first owner   ${owner_object_id} in tenant ${tenant_id}
  resource group $(vistara_env AZURE_RESOURCE_GROUP)

Sign in with that account to claim ownership. No other account can: every other
directory identity is refused until an owner exists and invites it.

  tear down, keeping the data:   ./deploy/azure/down.sh --env-name ${env_name}
  tear down everything:          ./deploy/azure/down.sh --env-name ${env_name} --delete-data

SUMMARY

printf '%s\n' "$sign_in_url"
vistara_open_url "$sign_in_url"
