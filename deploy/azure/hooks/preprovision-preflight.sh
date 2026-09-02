#!/usr/bin/env bash
# Vistara hosted bootstrap — preprovision preflight.
#
# `azd` runs this before every provisioning pass, and `up.sh` relies on that
# rather than duplicating the checks. It refuses a run that cannot succeed
# instead of letting a deployment fail halfway: a missing tool, a signed-out
# CLI, an unregistered resource provider, a mutable image tag, or a directory
# that will not accept the application registration.
#
# Re-executed under bash when a POSIX shell invoked it, because `azd` hook
# configuration only distinguishes `sh` from `pwsh`.
if [ -z "${BASH_VERSION:-}" ]; then
  exec bash "$0" "$@"
fi

set -euo pipefail

# shellcheck source=lib/common.sh
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/common.sh"

vistara_load_env

vistara_step 'Preflight'

# ---------------------------------------------------------------------------
# Tools
# ---------------------------------------------------------------------------

vistara_require_command az 'Install the Azure CLI: https://aka.ms/azure-cli'
vistara_require_command azd 'Install the Azure Developer CLI: https://aka.ms/azd-install'
vistara_require_command curl 'Install curl.'
vistara_require_command openssl 'Install openssl; it generates the API key pepper.'

az_version=$(az version --output tsv --query '"azure-cli"' 2>/dev/null | vistara_extract_version || true)
if [ -z "$az_version" ]; then
  vistara_die "$VISTARA_EXIT_MISSING_TOOL" 'could not read the Azure CLI version from `az version`.'
fi
if ! vistara_version_at_least "$az_version" "$VISTARA_MINIMUM_AZ_VERSION"; then
  vistara_die "$VISTARA_EXIT_MISSING_TOOL" \
    "Azure CLI ${VISTARA_MINIMUM_AZ_VERSION} or newer is required, found ${az_version}. Run: az upgrade"
fi

azd_version=$(azd version 2>/dev/null | vistara_extract_version || true)
if [ -z "$azd_version" ]; then
  vistara_die "$VISTARA_EXIT_MISSING_TOOL" 'could not read the Azure Developer CLI version from `azd version`.'
fi
if ! vistara_version_at_least "$azd_version" "$VISTARA_MINIMUM_AZD_VERSION"; then
  vistara_die "$VISTARA_EXIT_MISSING_TOOL" \
    "Azure Developer CLI ${VISTARA_MINIMUM_AZD_VERSION} or newer is required, found ${azd_version}."
fi

bicep_version=$(az bicep version 2>/dev/null | vistara_extract_version || true)
if [ -z "$bicep_version" ]; then
  vistara_die "$VISTARA_EXIT_MISSING_TOOL" \
    'no Bicep compiler is available. Run: az bicep install'
fi
if ! vistara_version_at_least "$bicep_version" "$VISTARA_MINIMUM_BICEP_VERSION"; then
  vistara_die "$VISTARA_EXIT_MISSING_TOOL" \
    "Bicep ${VISTARA_MINIMUM_BICEP_VERSION} or newer is required, found ${bicep_version}. Run: az bicep upgrade"
fi
if ! vistara_version_at_least "$bicep_version" "$VISTARA_PINNED_BICEP_VERSION"; then
  vistara_warn "Bicep ${bicep_version} is older than the ${VISTARA_PINNED_BICEP_VERSION} compiler this repository validates against."
fi

vistara_log "az ${az_version}, azd ${azd_version}, bicep ${bicep_version}"

# ---------------------------------------------------------------------------
# Sign-in and subscription
# ---------------------------------------------------------------------------

if ! account_id=$(az account show --query id --output tsv 2>/dev/null); then
  vistara_die "$VISTARA_EXIT_PERMISSION" 'the Azure CLI is not signed in. Run: az login'
fi
account_id=$(printf '%s' "$account_id" | tr -d '\r\n')

expected_subscription=$(vistara_env AZURE_SUBSCRIPTION_ID)
if [ -n "$expected_subscription" ] && [ "$expected_subscription" != "$account_id" ]; then
  vistara_die "$VISTARA_EXIT_PERMISSION" \
    "the active Azure CLI subscription is ${account_id} but this environment provisions into ${expected_subscription}. Run: az account set --subscription ${expected_subscription}"
fi

# ---------------------------------------------------------------------------
# Parameters the template cannot default
# ---------------------------------------------------------------------------

environment_name=$(vistara_require_env AZURE_ENV_NAME 'Run ./deploy/azure/up.sh rather than azd directly.')
location=$(vistara_require_env AZURE_LOCATION 'Run: azd env set AZURE_LOCATION <region>')
vistara_log "environment ${environment_name} in ${location}"

for image_variable in VISTARA_API_IMAGE VISTARA_WORKER_IMAGE VISTARA_MIGRATION_IMAGE; do
  image_value=$(vistara_env "$image_variable")
  if [ -z "$image_value" ]; then
    vistara_die "$VISTARA_EXIT_USAGE" \
      "${image_variable} is not set. Run ./deploy/azure/up.sh, which resolves release digests."
  fi
  if ! vistara_is_digest_reference "$image_value"; then
    vistara_die "$VISTARA_EXIT_USAGE" \
      "${image_variable} must pin an immutable digest of the form registry/name@sha256:<64 hex>, got '${image_value}'."
  fi
done

first_owner=$(vistara_require_env VISTARA_FIRST_OWNER_OBJECT_ID 'Pass --owner-object-id to up.sh.')
vistara_require_guid "$first_owner" 'VISTARA_FIRST_OWNER_OBJECT_ID'

postgres_admin=$(vistara_require_env VISTARA_POSTGRES_ADMIN_OBJECT_ID 'Pass --owner-object-id to up.sh.')
vistara_require_guid "$postgres_admin" 'VISTARA_POSTGRES_ADMIN_OBJECT_ID'

vistara_require_env VISTARA_POSTGRES_ADMIN_PRINCIPAL_NAME 'Run ./deploy/azure/up.sh.' >/dev/null

budget_start=$(vistara_require_env VISTARA_BUDGET_START_DATE 'Run ./deploy/azure/up.sh.')
budget_pattern='^[0-9]{4}-[0-9]{2}-01$'
if ! [[ $budget_start =~ $budget_pattern ]]; then
  vistara_die "$VISTARA_EXIT_USAGE" \
    "VISTARA_BUDGET_START_DATE must be the first day of a month (yyyy-MM-01), got '${budget_start}'. Cost Management rebases a budget that moves."
fi

# The activation pass needs both values the first pass produced. Catching that
# here keeps a direct `azd up` from failing inside a Bicep expression.
if [ "$(vistara_env VISTARA_DEPLOY_APPLICATIONS)" = 'true' ]; then
  if [ -z "$(vistara_env VISTARA_APPLICATION_CLIENT_ID)" ]; then
    vistara_die "$VISTARA_EXIT_USAGE" \
      'VISTARA_DEPLOY_APPLICATIONS is true but no application client ID is set. Run: ./deploy/azure/up.sh --env-name '"${environment_name}"
  fi
  if [ -z "$(vistara_env VISTARA_API_KEY_PEPPER_SECRET_URI)" ]; then
    vistara_die "$VISTARA_EXIT_USAGE" \
      'VISTARA_DEPLOY_APPLICATIONS is true but no API key pepper secret URI is set. Run: ./deploy/azure/up.sh --env-name '"${environment_name}"
  fi
fi

# ---------------------------------------------------------------------------
# Resource providers
#
# A subscription that has never hosted these services rejects the deployment
# with an opaque error, so registration is confirmed before anything is
# created. Registration is idempotent and free.
# ---------------------------------------------------------------------------

provider_timeout=${VISTARA_PROVIDER_TIMEOUT_SECONDS:-300}
provider_poll=${VISTARA_PROVIDER_POLL_SECONDS:-10}

for provider in Microsoft.App Microsoft.DBforPostgreSQL Microsoft.OperationalInsights Microsoft.KeyVault Microsoft.Storage Microsoft.ManagedIdentity; do
  state=$(az provider show --namespace "$provider" --query registrationState --output tsv 2>/dev/null || true)
  state=$(printf '%s' "$state" | tr -d '\r\n')
  if [ "$state" = 'Registered' ]; then
    continue
  fi
  vistara_log "registering resource provider ${provider} (currently ${state:-unknown})"
  if ! az provider register --namespace "$provider" --output none 2>/dev/null; then
    vistara_die "$VISTARA_EXIT_PERMISSION" \
      "could not register the ${provider} resource provider. Ask a subscription Contributor to run: az provider register --namespace ${provider}"
  fi
  deadline=$(( $(vistara_seconds) + provider_timeout ))
  while [ "$state" != 'Registered' ]; do
    if [ "$(vistara_seconds)" -ge "$deadline" ]; then
      vistara_die "$VISTARA_EXIT_PERMISSION" \
        "resource provider ${provider} was still ${state:-unknown} after ${provider_timeout}s. Check: az provider show --namespace ${provider} --query registrationState"
    fi
    sleep "$provider_poll"
    state=$(az provider show --namespace "$provider" --query registrationState --output tsv 2>/dev/null || true)
    state=$(printf '%s' "$state" | tr -d '\r\n')
  done
done

# ---------------------------------------------------------------------------
# Directory rights
#
# The application registration is created after provisioning, once the API host
# name exists. Finding out then that the deployer has no directory rights would
# waste a full provisioning run, so the capability is probed now.
# ---------------------------------------------------------------------------

if [ "$(vistara_env VISTARA_DEPLOY_APP_REGISTRATION)" != 'false' ] \
  && [ -z "$(vistara_env VISTARA_APPLICATION_CLIENT_ID)" ]; then
  if ! az ad app list --filter "displayName eq 'vistara-directory-rights-probe'" --query '[].appId' --output tsv >/dev/null 2>&1; then
    vistara_error 'the signed-in principal cannot read application registrations in this tenant.'
    vistara_error 'Ask a directory administrator for Application.ReadWrite.OwnedBy, or have them create the'
    vistara_error 'registration once and rerun with --skip-app-registration --client-id <appId>:'
    vistara_error ''
    vistara_error "  az ad app create --display-name \"Vistara ${environment_name}\" \\"
    vistara_error '    --sign-in-audience AzureADMyOrg \'
    vistara_error "    --web-redirect-uris \"https://<api fqdn>${VISTARA_OIDC_CALLBACK_PATH}\" \"https://<api fqdn>${VISTARA_OIDC_SIGNED_OUT_PATH}\" \\"
    vistara_error '    --enable-id-token-issuance false --enable-access-token-issuance false'
    vistara_error '  az ad sp create --id <appId>'
    vistara_error "  az ad app federated-credential create --id <appObjectId> --parameters '{\"name\":\"${VISTARA_FEDERATED_CREDENTIAL_NAME}\",\"issuer\":\"https://login.microsoftonline.com/<tenantId>/v2.0\",\"subject\":\"<api identity principal id>\",\"audiences\":[\"${VISTARA_FEDERATED_CREDENTIAL_AUDIENCE}\"]}'"
    vistara_die "$VISTARA_EXIT_PERMISSION" 'insufficient directory rights for the application registration.'
  fi
fi

vistara_log 'Preflight passed.'
