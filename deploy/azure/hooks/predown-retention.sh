#!/usr/bin/env bash
# Vistara hosted bootstrap — teardown guard.
#
# `azd down` deletes every resource it finds in the environment's resource
# group. The PostgreSQL server, the storage account, and the Key Vault carry
# `CanNotDelete` locks precisely so that this cannot happen by accident, and a
# bare `azd down` would delete the compute first and only then fail on the
# locks, leaving a half-torn-down environment.
#
# This hook therefore stops a teardown that has not gone through
# `./deploy/azure/down.sh`, which is where the retain-or-delete decision, the
# typed confirmation, and the lock removal live.
if [ -z "${BASH_VERSION:-}" ]; then
  exec bash "$0" "$@"
fi

set -euo pipefail

# shellcheck source=lib/common.sh
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/common.sh"

vistara_load_env

vistara_step 'Teardown guard'

environment_name=$(vistara_env AZURE_ENV_NAME)
resource_group=$(vistara_env AZURE_RESOURCE_GROUP)

if [ -z "$resource_group" ]; then
  vistara_warn 'no resource group is recorded for this environment; nothing to guard.'
  exit 0
fi

locks=$(az lock list --resource-group "$resource_group" --query '[].id' --output tsv 2>/dev/null || true)
locks=$(printf '%s' "$locks" | tr -d '\r' | sed '/^$/d')

if [ -n "$locks" ] && [ "${VISTARA_DOWN_CONFIRMED:-0}" != '1' ]; then
  vistara_error "resource group ${resource_group} still holds delete locks:"
  printf '%s\n' "$locks" >&2
  vistara_error ''
  vistara_error 'These protect the database, the media storage account, and the Key Vault.'
  vistara_error 'Tear the environment down through the wrapper, which decides what is kept:'
  vistara_error ''
  vistara_error "  ./deploy/azure/down.sh --env-name ${environment_name:-<env>}                # keep the data"
  vistara_error "  ./deploy/azure/down.sh --env-name ${environment_name:-<env>} --delete-data  # delete everything"
  vistara_die "$VISTARA_EXIT_USAGE" 'refusing to run azd down while data locks are in place.'
fi

for resource_type in Microsoft.DBforPostgreSQL/flexibleServers Microsoft.Storage/storageAccounts Microsoft.KeyVault/vaults; do
  retained=$(az resource list \
    --resource-group "$resource_group" \
    --resource-type "$resource_type" \
    --query '[].id' --output tsv 2>/dev/null || true)
  retained=$(printf '%s' "$retained" | tr -d '\r' | sed '/^$/d')
  [ -n "$retained" ] || continue
  vistara_warn "about to delete ${resource_type}:"
  printf '%s\n' "$retained" >&2
done

vistara_log 'Teardown may proceed.'
