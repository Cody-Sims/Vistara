#!/usr/bin/env bash
# Vistara hosted bootstrap — API key pepper.
#
# The API refuses to start without the pepper listed in
# `Security:RequiredSecretKeys`, and the container reads it as a Key Vault
# secret reference, so the secret has to exist before the activation pass.
#
# The value never becomes a command-line argument, an environment variable, an
# `azd` environment value, or a line of output: `openssl` writes it straight
# into a 0600 file, the Azure CLI reads that file, and the file is removed.
# Command lines are visible to every process on the machine and land in shell
# history; `azd` environment values are stored in plaintext under `.azure/`.
#
# Idempotent: an existing secret is reused, so reruns never rotate the pepper
# out from under the API keys that were hashed with it.
if [ -z "${BASH_VERSION:-}" ]; then
  exec bash "$0" "$@"
fi

set -euo pipefail

# shellcheck source=lib/common.sh
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/common.sh"

vistara_load_env

vistara_step 'API key pepper'

resource_group=$(vistara_require_env AZURE_RESOURCE_GROUP)
vault_endpoint=$(vistara_require_env AZURE_KEY_VAULT_ENDPOINT)
vault_name=$(vistara_vault_name_from_endpoint "$vault_endpoint")
if [ -z "$vault_name" ]; then
  vistara_die "$VISTARA_EXIT_PROVISION" "could not derive a vault name from AZURE_KEY_VAULT_ENDPOINT='${vault_endpoint}'."
fi

pepper_file=''
cleanup() {
  [ -n "$pepper_file" ] && vistara_shred "$pepper_file"
  return 0
}
trap cleanup EXIT INT TERM

# ---------------------------------------------------------------------------
# Data-plane access for the operator
#
# The vault is RBAC-authorized and the template grants only the workload
# identities, so the deployer needs its own assignment to write the secret.
# `down.sh` removes it again.
# ---------------------------------------------------------------------------

vault_id=$(az keyvault show --name "$vault_name" --resource-group "$resource_group" --query id --output tsv 2>/dev/null || true)
vault_id=$(printf '%s' "$vault_id" | tr -d '\r\n')
if [ -z "$vault_id" ]; then
  vistara_die "$VISTARA_EXIT_PROVISION" "Key Vault ${vault_name} was not found in resource group ${resource_group}."
fi

operator_object_id=$(vistara_env VISTARA_OPERATOR_OBJECT_ID)
if [ -z "$operator_object_id" ]; then
  operator_object_id=$(az ad signed-in-user show --query id --output tsv 2>/dev/null || true)
  operator_object_id=$(printf '%s' "$operator_object_id" | tr -d '\r\n')
fi

if [ -n "$operator_object_id" ]; then
  existing_assignment=$(az role assignment list --assignee "$operator_object_id" --scope "$vault_id" \
    --role "$VISTARA_KEY_VAULT_OPERATOR_ROLE" --query '[0].id' --output tsv 2>/dev/null || true)
  existing_assignment=$(printf '%s' "$existing_assignment" | tr -d '\r\n')
  if [ -z "$existing_assignment" ]; then
    vistara_log "granting '${VISTARA_KEY_VAULT_OPERATOR_ROLE}' on ${vault_name} to the deploying principal."
    if ! assignment_output=$(az role assignment create \
      --role "$VISTARA_KEY_VAULT_OPERATOR_ROLE" \
      --assignee-object-id "$operator_object_id" \
      --assignee-principal-type User \
      --scope "$vault_id" \
      --output none 2>&1); then
      vistara_warn "$(printf '%s' "$assignment_output" | vistara_redact)"
      vistara_warn 'continuing: an equivalent assignment may already exist at a higher scope.'
    fi
    vistara_azd_env_set VISTARA_OPERATOR_KEY_VAULT_ROLE_SCOPE "$vault_id"
  fi
  vistara_azd_env_set VISTARA_OPERATOR_OBJECT_ID "$operator_object_id"
else
  vistara_warn 'could not resolve the signed-in principal; assuming the vault already grants it secret access.'
fi

# ---------------------------------------------------------------------------
# The secret itself
# ---------------------------------------------------------------------------

# Role assignments are eventually consistent. Poll the data plane rather than
# failing on the first denial, which is what an unlucky first run would hit.
propagation_timeout=${VISTARA_ROLE_PROPAGATION_TIMEOUT_SECONDS:-180}
propagation_poll=${VISTARA_ROLE_PROPAGATION_POLL_SECONDS:-10}
deadline=$(( $(vistara_seconds) + propagation_timeout ))
while : ; do
  if az keyvault secret list --vault-name "$vault_name" --query '[].name' --output tsv >/dev/null 2>&1; then
    break
  fi
  if [ "$(vistara_seconds)" -ge "$deadline" ]; then
    vistara_die "$VISTARA_EXIT_PERMISSION" \
      "the deploying principal still cannot read secrets in ${vault_name} after ${propagation_timeout}s. Grant it '${VISTARA_KEY_VAULT_OPERATOR_ROLE}' on ${vault_id} and rerun up.sh."
  fi
  vistara_log 'waiting for the Key Vault role assignment to take effect.'
  sleep "$propagation_poll"
done

existing_secret=$(az keyvault secret show --vault-name "$vault_name" --name "$VISTARA_API_KEY_PEPPER_SECRET_NAME" \
  --query id --output tsv 2>/dev/null || true)
existing_secret=$(printf '%s' "$existing_secret" | tr -d '\r\n')

if [ -n "$existing_secret" ]; then
  vistara_log "reusing the existing '${VISTARA_API_KEY_PEPPER_SECRET_NAME}' secret; API keys hashed with it stay valid."
else
  vistara_log "generating the API key pepper into ${vault_name}."
  pepper_file=$(vistara_private_file 'api-key-pepper')
  if ! openssl rand -base64 32 >"$pepper_file"; then
    vistara_die "$VISTARA_EXIT_PROVISION" 'openssl could not generate the API key pepper.'
  fi
  if [ ! -s "$pepper_file" ]; then
    vistara_die "$VISTARA_EXIT_PROVISION" 'the generated API key pepper was empty.'
  fi
  if ! set_output=$(az keyvault secret set \
    --vault-name "$vault_name" \
    --name "$VISTARA_API_KEY_PEPPER_SECRET_NAME" \
    --file "$pepper_file" \
    --encoding utf-8 \
    --output none 2>&1); then
    vistara_error "$(printf '%s' "$set_output" | vistara_redact)"
    vistara_die "$VISTARA_EXIT_PROVISION" "could not write the '${VISTARA_API_KEY_PEPPER_SECRET_NAME}' secret to ${vault_name}."
  fi
  vistara_shred "$pepper_file"
  pepper_file=''
fi

# A versionless URI: Container Apps resolves the current version at start, so
# rotating the secret does not require a template change.
pepper_uri="${vault_endpoint%/}/secrets/${VISTARA_API_KEY_PEPPER_SECRET_NAME}"
vistara_azd_env_set VISTARA_API_KEY_PEPPER_SECRET_URI "$pepper_uri"

vistara_log "pepper reference ${pepper_uri}"
