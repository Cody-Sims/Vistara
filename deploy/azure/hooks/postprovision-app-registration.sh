#!/usr/bin/env bash
# Vistara hosted bootstrap — Entra application registration.
#
# Runs after provisioning, because the reply URLs and the federated identity
# credential subject are both outputs of the deployment: the API host name and
# the API managed identity principal ID do not exist before it.
#
# `infra/main.bicep` deliberately does not deploy the Graph module: `azd`'s
# Bicep provider support for the Microsoft Graph extension is not verified
# end-to-end, and a template that fails inside the extension leaves no way to
# recover other than the manual path. The registration is therefore created
# here with the Azure CLI in the exact shape
# `infra/entra/app-registration.bicep` describes, and the shape is asserted by
# `postprovision-verify-fic.sh` afterwards.
#
# Idempotent: an existing registration is patched back into the declared shape
# rather than duplicated, so the second provisioning pass and every rerun are
# no-ops.
if [ -z "${BASH_VERSION:-}" ]; then
  exec bash "$0" "$@"
fi

set -euo pipefail

# shellcheck source=lib/common.sh
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/common.sh"

vistara_load_env

# Asked before anything is created in a directory, and asked of Azure rather
# than of the value being used: the reply URLs, the issuer, and the credential
# subject are all derived from AZURE_TENANT_ID, so a wrong tenant would create
# a registration that is perfectly consistent with the wrong tenant.
vistara_require_tenant_matches_subscription 'the Entra application registration'

environment_name=$(vistara_require_env AZURE_ENV_NAME)
tenant_id=$(vistara_require_env AZURE_TENANT_ID)
api_uri=$(vistara_require_env SERVICE_API_URI)
api_principal_id=$(vistara_require_env API_IDENTITY_PRINCIPAL_ID)
vistara_require_guid "$api_principal_id" 'API_IDENTITY_PRINCIPAL_ID'
vistara_require_guid "$tenant_id" 'AZURE_TENANT_ID'

display_name="Vistara ${environment_name}"
callback_uri="${api_uri}${VISTARA_OIDC_CALLBACK_PATH}"
signed_out_uri="${api_uri}${VISTARA_OIDC_SIGNED_OUT_PATH}"
# The subject is matched case-sensitively by Entra against the `sub` claim of
# the managed identity token, which is the lowercase principal ID.
expected_subject=$(printf '%s' "$api_principal_id" | tr '[:upper:]' '[:lower:]')
expected_issuer="https://login.microsoftonline.com/$(printf '%s' "$tenant_id" | tr '[:upper:]' '[:lower:]')/v2.0"

client_id=$(vistara_env VISTARA_APPLICATION_CLIENT_ID)

if [ "$(vistara_env VISTARA_DEPLOY_APP_REGISTRATION)" = 'false' ]; then
  vistara_step 'Entra application registration (operator supplied)'
  if [ -z "$client_id" ]; then
    vistara_die "$VISTARA_EXIT_USAGE" \
      '--skip-app-registration was used but no client ID is set. Rerun up.sh with --client-id <appId>.'
  fi
  vistara_require_guid "$client_id" 'VISTARA_APPLICATION_CLIENT_ID'
  vistara_log "using the existing registration ${client_id}; its shape is verified next."
  exit 0
fi

vistara_step 'Entra application registration'

# ---------------------------------------------------------------------------
# Resolve or create the application
# ---------------------------------------------------------------------------

app_object_id=''
if [ -n "$client_id" ]; then
  vistara_require_guid "$client_id" 'VISTARA_APPLICATION_CLIENT_ID'
  app_object_id=$(az ad app show --id "$client_id" --query id --output tsv 2>/dev/null || true)
  app_object_id=$(printf '%s' "$app_object_id" | tr -d '\r\n')
  if [ -z "$app_object_id" ]; then
    vistara_warn "the recorded application ${client_id} no longer exists in this tenant; looking for one named '${display_name}'."
    client_id=''
  fi
fi

if [ -z "$client_id" ]; then
  matches=$(az ad app list --filter "displayName eq '${display_name}'" --query '[].appId' --output tsv 2>/dev/null || true)
  matches=$(printf '%s' "$matches" | tr -d '\r' | sed '/^$/d')
  if [ -n "$matches" ]; then
    match_count=$(printf '%s\n' "$matches" | wc -l | tr -d ' ')
  else
    match_count=0
  fi
  if [ "$match_count" -gt 1 ]; then
    vistara_die "$VISTARA_EXIT_PERMISSION" \
      "${match_count} Entra applications are named '${display_name}'. Delete the duplicates or rerun with --skip-app-registration --client-id <appId>."
  fi
  if [ "$match_count" -eq 1 ]; then
    client_id=$(printf '%s\n' "$matches" | head -n 1)
    vistara_log "reusing the existing registration ${client_id}."
  fi
fi

request_body=$(vistara_private_file 'application-body.json')
cat >"$request_body" <<JSON
{
  "displayName": "${display_name}",
  "description": "Vistara hosted API interactive sign-in. Single tenant, authorization code with PKCE, secretless managed-identity client assertion.",
  "signInAudience": "AzureADMyOrg",
  "groupMembershipClaims": "None",
  "isDeviceOnlyAuthSupported": false,
  "isFallbackPublicClient": false,
  "publicClient": { "redirectUris": [] },
  "spa": { "redirectUris": [] },
  "web": {
    "homePageUrl": "${api_uri}",
    "logoutUrl": null,
    "redirectUris": [
      "${callback_uri}",
      "${signed_out_uri}"
    ],
    "implicitGrantSettings": {
      "enableAccessTokenIssuance": false,
      "enableIdTokenIssuance": false
    }
  },
  "requiredResourceAccess": [
    {
      "resourceAppId": "00000003-0000-0000-c000-000000000000",
      "resourceAccess": [
        { "id": "37f7f235-527c-4136-accd-4a02d197296e", "type": "Scope" },
        { "id": "14dad69e-099b-42c9-810b-d002981feec1", "type": "Scope" },
        { "id": "64a6cdd6-aab1-4aaf-94b8-3cc8405e90d0", "type": "Scope" }
      ]
    }
  ]
}
JSON

if [ -z "$client_id" ]; then
  vistara_log "creating the application registration '${display_name}'."
  if ! client_id=$(az rest --method POST \
    --url 'https://graph.microsoft.com/v1.0/applications' \
    --headers 'Content-Type=application/json' \
    --body "@${request_body}" \
    --query appId --output tsv 2>&1); then
    vistara_error "$(printf '%s' "$client_id" | vistara_redact)"
    vistara_die "$VISTARA_EXIT_PERMISSION" \
      'creating the application registration failed. Retry with --skip-app-registration --client-id <appId> once a directory administrator has created it.'
  fi
  client_id=$(printf '%s' "$client_id" | tr -d '\r\n')
  vistara_require_guid "$client_id" 'the created application client ID'
else
  vistara_log "reconciling the registration ${client_id} with the declared shape."
fi

app_object_id=$(az ad app show --id "$client_id" --query id --output tsv 2>/dev/null || true)
app_object_id=$(printf '%s' "$app_object_id" | tr -d '\r\n')
if [ -z "$app_object_id" ]; then
  vistara_die "$VISTARA_EXIT_PERMISSION" "could not read the object ID of application ${client_id}."
fi

# The PATCH is what makes reruns converge: it restores exactly two reply URLs,
# clears any logoutUrl someone added by hand, and keeps both implicit grants
# off. Every value is derived from deployment outputs, so nothing external is
# ever merged into the reply list.
if ! patch_output=$(az rest --method PATCH \
  --url "https://graph.microsoft.com/v1.0/applications/${app_object_id}" \
  --headers 'Content-Type=application/json' \
  --body "@${request_body}" \
  --output none 2>&1); then
  vistara_error "$(printf '%s' "$patch_output" | vistara_redact)"
  vistara_die "$VISTARA_EXIT_PERMISSION" "could not update the application registration ${client_id}."
fi

vistara_shred "$request_body"

# ---------------------------------------------------------------------------
# Service principal
#
# The application object alone cannot be assigned to, consented for, or signed
# in against in this tenant; the service principal is the tenant-local half.
# ---------------------------------------------------------------------------

service_principal_id=$(az ad sp show --id "$client_id" --query id --output tsv 2>/dev/null || true)
service_principal_id=$(printf '%s' "$service_principal_id" | tr -d '\r\n')
if [ -z "$service_principal_id" ]; then
  vistara_log 'creating the service principal.'
  if ! sp_output=$(az ad sp create --id "$client_id" --query id --output tsv 2>&1); then
    vistara_error "$(printf '%s' "$sp_output" | vistara_redact)"
    vistara_die "$VISTARA_EXIT_PERMISSION" "could not create the service principal for ${client_id}."
  fi
  service_principal_id=$(printf '%s' "$sp_output" | tr -d '\r\n')
fi

# ---------------------------------------------------------------------------
# Federated identity credential
#
# A credential with a wrong issuer, subject, or audience is accepted by Entra
# and only fails later at token exchange, so a mismatch is replaced rather than
# left in place, and the result is byte-compared by the verification hook.
# ---------------------------------------------------------------------------

current_issuer=$(az ad app federated-credential list --id "$app_object_id" \
  --query "[?name=='${VISTARA_FEDERATED_CREDENTIAL_NAME}'].issuer | [0]" --output tsv 2>/dev/null || true)
current_issuer=$(printf '%s' "$current_issuer" | tr -d '\r\n')
current_subject=$(az ad app federated-credential list --id "$app_object_id" \
  --query "[?name=='${VISTARA_FEDERATED_CREDENTIAL_NAME}'].subject | [0]" --output tsv 2>/dev/null || true)
current_subject=$(printf '%s' "$current_subject" | tr -d '\r\n')
current_audience=$(az ad app federated-credential list --id "$app_object_id" \
  --query "[?name=='${VISTARA_FEDERATED_CREDENTIAL_NAME}'].audiences[0] | [0]" --output tsv 2>/dev/null || true)
current_audience=$(printf '%s' "$current_audience" | tr -d '\r\n')

credential_matches=0
if [ "$current_issuer" = "$expected_issuer" ] \
  && [ "$current_subject" = "$expected_subject" ] \
  && [ "$current_audience" = "$VISTARA_FEDERATED_CREDENTIAL_AUDIENCE" ]; then
  credential_matches=1
fi

if [ "$credential_matches" -eq 0 ]; then
  if [ -n "$current_issuer" ] || [ -n "$current_subject" ]; then
    vistara_warn "replacing the federated identity credential '${VISTARA_FEDERATED_CREDENTIAL_NAME}': it does not match this deployment."
    az ad app federated-credential delete --id "$app_object_id" \
      --federated-credential-id "$VISTARA_FEDERATED_CREDENTIAL_NAME" --output none 2>/dev/null || true
  fi

  credential_body=$(vistara_private_file 'federated-credential.json')
  cat >"$credential_body" <<JSON
{
  "name": "${VISTARA_FEDERATED_CREDENTIAL_NAME}",
  "issuer": "${expected_issuer}",
  "subject": "${expected_subject}",
  "description": "Trusts the Vistara API user-assigned managed identity to present client assertions for this application.",
  "audiences": [ "${VISTARA_FEDERATED_CREDENTIAL_AUDIENCE}" ]
}
JSON

  vistara_log 'creating the federated identity credential for the API managed identity.'
  if ! credential_output=$(az ad app federated-credential create --id "$app_object_id" \
    --parameters "@${credential_body}" --output none 2>&1); then
    vistara_error "$(printf '%s' "$credential_output" | vistara_redact)"
    vistara_shred "$credential_body"
    vistara_die "$VISTARA_EXIT_PERMISSION" 'could not create the federated identity credential.'
  fi
  vistara_shred "$credential_body"
fi

vistara_azd_env_set VISTARA_APPLICATION_CLIENT_ID "$client_id"
vistara_azd_env_set ENTRA_APPLICATION_CLIENT_ID "$client_id"
vistara_azd_env_set ENTRA_APPLICATION_OBJECT_ID "$app_object_id"
vistara_azd_env_set ENTRA_SERVICE_PRINCIPAL_OBJECT_ID "$service_principal_id"

vistara_log "application ${client_id} is registered with the reply URL ${callback_uri}."
