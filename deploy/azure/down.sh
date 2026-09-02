#!/usr/bin/env bash
# Vistara hosted bootstrap — teardown.
#
#   ./deploy/azure/down.sh                 keeps the data, removes the running cost
#   ./deploy/azure/down.sh --delete-data   removes everything, after a typed confirmation
#
# The default is deliberately partial. Almost all of the hourly cost of this
# deployment is compute: the Container Apps environment, the API, the worker,
# the migration job, and Log Analytics. The PostgreSQL server, the media
# storage account, and the Key Vault hold the only data that cannot be
# recreated, they carry `CanNotDelete` locks, and this script does not touch
# them unless it is told to.
#
# Nothing is deleted without first proving that the resource group belongs to
# this environment, in this subscription. Every destructive call names the
# subscription explicitly rather than relying on whichever one the CLI happens
# to have selected.
#
# Exit codes: 0 ok · 64 usage · 69 missing tool · 70 teardown failure ·
#             77 insufficient permissions or a refused target
if [ -z "${BASH_VERSION:-}" ]; then
  exec bash "$0" "$@"
fi

set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

# shellcheck source=hooks/lib/common.sh
. "${SCRIPT_DIR}/hooks/lib/common.sh"

usage() {
  cat <<'USAGE'
Usage: ./deploy/azure/down.sh [options]

  --env-name NAME             azd environment (default: the selected one)
  --delete-data               also delete PostgreSQL, storage, and Key Vault
  --keep-app-registration     keep the Entra application even with --delete-data
  --yes                       skip the confirmations
  -h, --help                  show this help

Default: deletes the compute that costs money and keeps every byte of data.

Exit codes: 0 ok · 64 usage · 69 missing tool · 70 teardown failure ·
            77 insufficient permissions or a refused target
USAGE
}

env_name=''
delete_data=0
keep_app_registration=0

while [ "$#" -gt 0 ]; do
  case "$1" in
    --env-name)
      if [ -z "${2:-}" ]; then
        vistara_error '--env-name requires a value.'
        usage >&2
        exit "$VISTARA_EXIT_USAGE"
      fi
      env_name=$2
      shift 2
      ;;
    --delete-data) delete_data=1; shift ;;
    --keep-app-registration) keep_app_registration=1; shift ;;
    --yes|-y) export VISTARA_ASSUME_YES=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *)
      vistara_error "unknown option '$1'."
      usage >&2
      exit "$VISTARA_EXIT_USAGE"
      ;;
  esac
done

vistara_require_command az 'Install the Azure CLI: https://aka.ms/azure-cli'
vistara_require_command azd 'Install the Azure Developer CLI: https://aka.ms/azd-install'

if ! az account show --query id --output tsv >/dev/null 2>&1; then
  vistara_die "$VISTARA_EXIT_PERMISSION" 'the Azure CLI is not signed in. Run: az login'
fi

cd "$SCRIPT_DIR"

# The environment is named, never selected. `azd env select` writes the default
# environment to local state, and everything up to the confirmation below has
# to be readable without changing anything at all.
if [ -n "$env_name" ]; then
  # Reading the environment's values is both the existence check and the only
  # thing needed from it. Listing environments and matching names would mean
  # parsing an output format, and `azd env select` would write the default
  # environment to local state before the operator has agreed to anything.
  if ! azd env get-values --environment "$env_name" >/dev/null 2>&1; then
    vistara_die "$VISTARA_EXIT_USAGE" "no azd environment named ${env_name}. List them with: azd env list"
  fi
  export AZURE_ENV_NAME="$env_name"
  export VISTARA_AZD_ENVIRONMENT="$env_name"
fi

export VISTARA_STATE_DIR="${VISTARA_STATE_DIR:-${SCRIPT_DIR}/.azure/${env_name:-default}/.vistara}"

vistara_load_env

env_name=${env_name:-$(vistara_env AZURE_ENV_NAME)}
if [ -z "$env_name" ]; then
  vistara_die "$VISTARA_EXIT_USAGE" 'no azd environment is selected. Pass --env-name NAME.'
fi

resource_group=$(vistara_env AZURE_RESOURCE_GROUP)
subscription_id=$(vistara_env AZURE_SUBSCRIPTION_ID)

if [ -z "$resource_group" ] || [ -z "$subscription_id" ]; then
  vistara_die "$VISTARA_EXIT_USAGE" \
    "environment ${env_name} records no resource group or subscription, so there is nothing this script can safely delete."
fi

vistara_step "Vistara teardown — environment ${env_name}"

# ---------------------------------------------------------------------------
# Prove the target
#
# A resource group name is not proof. The tag is written by this deployment's
# template, and a group without it belongs to something else.
# ---------------------------------------------------------------------------

if ! group_tag=$(az group show \
  --name "$resource_group" \
  --subscription "$subscription_id" \
  --query 'tags."azd-env-name"' --output tsv 2>/dev/null); then
  vistara_log "resource group ${resource_group} does not exist in subscription ${subscription_id}; nothing to delete."
  exit 0
fi
group_tag=$(printf '%s' "$group_tag" | tr -d '\r\n')

if [ "$group_tag" != "$env_name" ]; then
  vistara_die "$VISTARA_EXIT_PERMISSION" \
    "resource group ${resource_group} is tagged azd-env-name='${group_tag}', not '${env_name}'. Refusing to delete resources this environment does not own."
fi

app_registration_ownership=$(az group show \
  --name "$resource_group" \
  --subscription "$subscription_id" \
  --query 'tags."vistara-app-registration"' --output tsv 2>/dev/null | tr -d '\r\n' || true)

vistara_log "resource group ${resource_group} in subscription ${subscription_id}"

list_resources() {
  local resource_type=$1
  az resource list \
    --resource-group "$resource_group" \
    --subscription "$subscription_id" \
    --resource-type "$resource_type" \
    --query "[?tags.\"azd-env-name\"=='${env_name}'].id" \
    --output tsv 2>/dev/null | tr -d '\r' | sed '/^$/d' || true
}

# ---------------------------------------------------------------------------
# Role assignments this bootstrap created
#
# `up.sh` grants the operator secret access on the vault so it can write the
# API key pepper. Teardown gives it back in both modes — but only once the
# operator has agreed to the teardown: removing an access grant from a
# deployment that is still running is a change, and a run that is declined
# makes none.
# ---------------------------------------------------------------------------

operator_object_id=$(vistara_env VISTARA_OPERATOR_OBJECT_ID)
operator_scope=$(vistara_env VISTARA_OPERATOR_KEY_VAULT_ROLE_SCOPE)

remove_operator_role_assignment() {
  [ -n "$operator_object_id" ] || return 0
  [ -n "$operator_scope" ] || return 0
  case "$operator_scope" in
    "/subscriptions/${subscription_id}/resourceGroups/${resource_group}/"*)
      vistara_log "removing the operator '${VISTARA_KEY_VAULT_OPERATOR_ROLE}' assignment on the vault."
      az role assignment delete \
        --assignee "$operator_object_id" \
        --role "$VISTARA_KEY_VAULT_OPERATOR_ROLE" \
        --scope "$operator_scope" \
        --subscription "$subscription_id" \
        --yes --output none 2>/dev/null \
        || vistara_warn 'the role assignment was already gone.'
      ;;
    *)
      vistara_warn "recorded role assignment scope ${operator_scope} is outside ${resource_group}; leaving it alone."
      ;;
  esac
}

# ---------------------------------------------------------------------------
# Retaining teardown (default)
# ---------------------------------------------------------------------------

if [ "$delete_data" != '1' ]; then
  vistara_log ''
  vistara_log 'This removes the compute that costs money and keeps all data:'
  vistara_log '  deleted:  container apps, migration job, Container Apps environment, Log Analytics'
  vistara_log '  kept:     PostgreSQL server, media storage account, Key Vault, budget'
  vistara_log ''

  # Read the list first so the operator is agreeing to named resources rather
  # than to a category.
  for resource_type in \
    Microsoft.App/containerApps \
    Microsoft.App/jobs \
    Microsoft.App/managedEnvironments \
    Microsoft.OperationalInsights/workspaces; do
    resource_ids=$(list_resources "$resource_type")
    [ -n "$resource_ids" ] || continue
    printf '%s\n' "$resource_ids" | sed 's/^/  /' >&2
  done
  vistara_log ''

  if ! vistara_confirm "Delete the compute for ${env_name}?"; then
    vistara_log 'Nothing was changed.'
    exit 0
  fi

  remove_operator_role_assignment

  # Ordered so nothing is deleted while something else still depends on it.
  # Each type is deleted with its own command rather than a generic resource
  # delete: those handle the asynchronous teardown Container Apps performs and
  # report a refusal in terms the operator can act on.
  for resource_type in \
    Microsoft.App/containerApps \
    Microsoft.App/jobs \
    Microsoft.App/managedEnvironments \
    Microsoft.OperationalInsights/workspaces; do
    resource_ids=$(list_resources "$resource_type")
    [ -n "$resource_ids" ] || continue
    while IFS= read -r resource_id; do
      [ -n "$resource_id" ] || continue
      vistara_log "deleting ${resource_id##*/} (${resource_type})"
      case "$resource_type" in
        Microsoft.App/containerApps)
          az containerapp delete --ids "$resource_id" --subscription "$subscription_id" --yes --output none 2>/dev/null \
            || vistara_warn "could not delete ${resource_id}; delete it by hand if it is still there."
          ;;
        Microsoft.App/jobs)
          az containerapp job delete --ids "$resource_id" --subscription "$subscription_id" --yes --output none 2>/dev/null \
            || vistara_warn "could not delete ${resource_id}; delete it by hand if it is still there."
          ;;
        Microsoft.App/managedEnvironments)
          az containerapp env delete --ids "$resource_id" --subscription "$subscription_id" --yes --output none 2>/dev/null \
            || vistara_warn "could not delete ${resource_id}; delete it by hand if it is still there."
          ;;
        Microsoft.OperationalInsights/workspaces)
          az monitor log-analytics workspace delete --ids "$resource_id" --subscription "$subscription_id" --force --yes --output none 2>/dev/null \
            || vistara_warn "could not delete ${resource_id}; delete it by hand if it is still there."
          ;;
      esac
    done <<EOF
$resource_ids
EOF
  done

  # The applications no longer exist, so the environment must not claim they do.
  vistara_azd_env_set VISTARA_DEPLOY_APPLICATIONS 'false'

  vistara_log ''
  vistara_log 'Retained — these still cost money and still hold your data:'
  for resource_type in \
    Microsoft.DBforPostgreSQL/flexibleServers \
    Microsoft.Storage/storageAccounts \
    Microsoft.KeyVault/vaults; do
    retained=$(az resource list \
      --resource-group "$resource_group" \
      --subscription "$subscription_id" \
      --resource-type "$resource_type" \
      --query '[].id' --output tsv 2>/dev/null | tr -d '\r' | sed '/^$/d' || true)
    [ -n "$retained" ] || continue
    printf '%s\n' "$retained" | sed 's/^/  /' >&2
  done

  # `az consumption budget show` addresses a subscription-scoped budget and
  # answers nothing for this one: the template creates it at resource-group
  # scope, which is what the -with-rg form addresses.
  budget_spend=$(az consumption budget show-with-rg \
    --resource-group "$resource_group" \
    --budget-name "budget-vistara-$(printf '%s' "$env_name" | tr '[:upper:]' '[:lower:]')" \
    --subscription "$subscription_id" \
    --query 'currentSpend.amount' --output tsv 2>/dev/null | tr -d '\r\n' || true)
  if [ -n "$budget_spend" ]; then
    vistara_log ''
    vistara_log "Month to date against the budget: ${budget_spend}"
  fi

  vistara_log ''
  vistara_log 'A stopped PostgreSQL flexible server still bills for its provisioned storage, and'
  vistara_log 'the storage account bills for what it holds. To remove those too:'
  vistara_log "  ./deploy/azure/down.sh --env-name ${env_name} --delete-data"
  vistara_log ''
  vistara_log "To bring the deployment back: ./deploy/azure/up.sh --env-name ${env_name}"
  exit 0
fi

# ---------------------------------------------------------------------------
# Destructive teardown
# ---------------------------------------------------------------------------

vistara_log ''
vistara_log "--delete-data deletes the PostgreSQL server, the media storage account, and the Key"
vistara_log "Vault in ${resource_group}. Every image, every user, and the Data Protection key ring"
vistara_log 'go with them. This cannot be undone.'
vistara_log ''

if ! vistara_confirm_phrase "Delete all data for ${env_name}?" "$env_name"; then
  vistara_log 'Nothing was changed.'
  exit 0
fi

remove_operator_role_assignment

locks=$(az lock list \
  --resource-group "$resource_group" \
  --subscription "$subscription_id" \
  --query '[].id' --output tsv 2>/dev/null | tr -d '\r' | sed '/^$/d' || true)

if [ -n "$locks" ]; then
  while IFS= read -r lock_id; do
    [ -n "$lock_id" ] || continue
    case "$lock_id" in
      "/subscriptions/${subscription_id}/resourceGroups/${resource_group}/"*)
        vistara_log "removing lock ${lock_id##*/}"
        az lock delete --ids "$lock_id" --output none 2>/dev/null \
          || vistara_warn "could not remove lock ${lock_id}."
        ;;
      *)
        vistara_warn "lock ${lock_id} is outside ${resource_group}; leaving it alone."
        ;;
    esac
  done <<EOF
$locks
EOF
fi

# The predown hook refuses a teardown that did not come through this script;
# the locks are gone and the operator has typed the environment name, so this
# is the one place that flag is set.
export VISTARA_DOWN_CONFIRMED=1

vistara_step 'Deleting the environment'
if ! azd down --environment "$env_name" --force --purge; then
  vistara_error 'azd down did not finish.'
  vistara_error "Inspect what is left: az resource list --resource-group ${resource_group} --subscription ${subscription_id} --output table"
  vistara_die "$VISTARA_EXIT_PROVISION" 'teardown failed.'
fi

# ---------------------------------------------------------------------------
# Directory and environment cleanup
# ---------------------------------------------------------------------------

client_id=$(vistara_env VISTARA_APPLICATION_CLIENT_ID)
if [ "$keep_app_registration" != '1' ] \
  && [ "$app_registration_ownership" = 'template-managed' ] \
  && [ -n "$client_id" ]; then
  vistara_log "deleting the Entra application registration ${client_id} created for this environment."
  az ad app delete --id "$client_id" --output none 2>/dev/null \
    || vistara_warn "could not delete application ${client_id}; delete it with: az ad app delete --id ${client_id}"
  vistara_azd_env_clear VISTARA_APPLICATION_CLIENT_ID
  vistara_azd_env_clear ENTRA_APPLICATION_CLIENT_ID
fi

# The budget went with the resource group. Clearing the recorded start date is
# what lets a future `up.sh` create a new one: a start date in a past month is
# rejected by Cost Management, so a stale value would block the next
# deployment. In the retaining mode above the budget still exists, so the date
# is deliberately left in place there.
vistara_azd_env_clear VISTARA_BUDGET_START_DATE
vistara_azd_env_set VISTARA_DEPLOY_APPLICATIONS 'false'
vistara_azd_env_clear VISTARA_API_KEY_PEPPER_SECRET_URI
vistara_azd_env_clear VISTARA_DATABASE_BOOTSTRAP_STATE
vistara_azd_env_clear VISTARA_MIGRATION_COMPLETED_DIGEST

vistara_log ''
vistara_log "Environment ${env_name} is deleted. The Key Vault was purged, so its name is reusable."
vistara_log "To deploy again: ./deploy/azure/up.sh --env-name ${env_name}"
