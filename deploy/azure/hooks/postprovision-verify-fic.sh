#!/usr/bin/env bash
# Vistara hosted bootstrap — federated credential and reply URL verification.
#
# A federated identity credential with a wrong issuer, subject, or audience is
# accepted by Entra and fails only when the API first exchanges a managed
# identity assertion for a token — after deployment, on a real sign-in, with an
# error that says nothing useful. The same is true of a reply URL that differs
# by a character. Both are therefore byte-compared here, against the values the
# deployment actually produced, before anything else depends on them.
#
# This hook only reads. It fails the run rather than repairing anything, so an
# operator-supplied registration is never silently rewritten.
if [ -z "${BASH_VERSION:-}" ]; then
  exec bash "$0" "$@"
fi

set -euo pipefail

# shellcheck source=lib/common.sh
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/common.sh"

vistara_load_env

vistara_step 'Verifying the Entra registration'

# This hook exists to catch a federated credential that was registered with the
# wrong issuer, and the issuer is built from AZURE_TENANT_ID. Comparing what
# was registered against what this value produces cannot catch a wrong value:
# both sides would be wrong in the same way. The check is repeated here rather
# than inherited from the registration hook, so verification depends on Azure
# rather than on another step's conclusion.
vistara_require_tenant_matches_subscription 'the federated credential verification'

tenant_id=$(vistara_require_env AZURE_TENANT_ID)
api_uri=$(vistara_require_env SERVICE_API_URI)
api_principal_id=$(vistara_require_env API_IDENTITY_PRINCIPAL_ID)
client_id=$(vistara_require_env VISTARA_APPLICATION_CLIENT_ID 'The registration hook records this.')

callback_uri="${api_uri}${VISTARA_OIDC_CALLBACK_PATH}"
signed_out_uri="${api_uri}${VISTARA_OIDC_SIGNED_OUT_PATH}"
expected_subject=$(printf '%s' "$api_principal_id" | tr '[:upper:]' '[:lower:]')
expected_issuer="https://login.microsoftonline.com/$(printf '%s' "$tenant_id" | tr '[:upper:]' '[:lower:]')/v2.0"

app_object_id=$(vistara_env ENTRA_APPLICATION_OBJECT_ID)
if [ -z "$app_object_id" ]; then
  app_object_id=$(az ad app show --id "$client_id" --query id --output tsv 2>/dev/null || true)
  app_object_id=$(printf '%s' "$app_object_id" | tr -d '\r\n')
fi
if [ -z "$app_object_id" ]; then
  vistara_die "$VISTARA_EXIT_PROVISION" \
    "application ${client_id} was not found in this tenant. Check the value of --client-id."
fi

fail_with_manual_commands() {
  vistara_error 'Repair the registration and rerun up.sh:'
  vistara_error ''
  vistara_error "  az ad app update --id ${app_object_id} \\"
  vistara_error "    --web-redirect-uris \"${callback_uri}\" \"${signed_out_uri}\" \\"
  vistara_error '    --enable-id-token-issuance false --enable-access-token-issuance false'
  vistara_error "  az ad app federated-credential create --id ${app_object_id} --parameters '{\"name\":\"${VISTARA_FEDERATED_CREDENTIAL_NAME}\",\"issuer\":\"${expected_issuer}\",\"subject\":\"${expected_subject}\",\"audiences\":[\"${VISTARA_FEDERATED_CREDENTIAL_AUDIENCE}\"]}'"
  vistara_die "$VISTARA_EXIT_PROVISION" "$1"
}

# ---------------------------------------------------------------------------
# Reply URLs: exactly the two the API serves, in either order, and nothing else
# ---------------------------------------------------------------------------

reply_urls=$(az ad app show --id "$client_id" --query 'web.redirectUris' --output tsv 2>/dev/null || true)
reply_urls=$(printf '%s' "$reply_urls" | tr -d '\r' | tr '\t' '\n' | sed '/^$/d' | LC_ALL=C sort)
expected_urls=$(printf '%s\n%s\n' "$callback_uri" "$signed_out_uri" | LC_ALL=C sort)

if [ "$reply_urls" != "$expected_urls" ]; then
  vistara_error 'the registered reply URLs do not match this deployment.'
  vistara_error "expected: $(printf '%s' "$expected_urls" | tr '\n' ' ')"
  vistara_error "actual:   $(printf '%s' "$reply_urls" | tr '\n' ' ')"
  fail_with_manual_commands 'reply URL mismatch.'
fi

# A registered logoutUrl advertises a front-channel sign-out that cannot work:
# Entra issues it as a cross-site GET and the SameSite=Lax session cookie is
# never attached, so the endpoint would appear to sign the user out while the
# session stayed valid.
logout_url=$(az ad app show --id "$client_id" --query 'web.logoutUrl' --output tsv 2>/dev/null || true)
logout_url=$(printf '%s' "$logout_url" | tr -d '\r\n')
case "$logout_url" in
  ''|None|null) ;;
  *)
    vistara_error "the registration declares web.logoutUrl='${logout_url}'."
    vistara_error 'Front-channel sign-out cannot clear a SameSite=Lax session cookie, so this deployment registers none.'
    vistara_error "  az ad app update --id ${app_object_id} --set web.logoutUrl=null"
    fail_with_manual_commands 'web.logoutUrl must not be registered.'
    ;;
esac

# ---------------------------------------------------------------------------
# Federated identity credential
# ---------------------------------------------------------------------------

actual_issuer=$(az ad app federated-credential list --id "$app_object_id" \
  --query "[?name=='${VISTARA_FEDERATED_CREDENTIAL_NAME}'].issuer | [0]" --output tsv 2>/dev/null || true)
actual_issuer=$(printf '%s' "$actual_issuer" | tr -d '\r\n')
actual_subject=$(az ad app federated-credential list --id "$app_object_id" \
  --query "[?name=='${VISTARA_FEDERATED_CREDENTIAL_NAME}'].subject | [0]" --output tsv 2>/dev/null || true)
actual_subject=$(printf '%s' "$actual_subject" | tr -d '\r\n')
actual_audience=$(az ad app federated-credential list --id "$app_object_id" \
  --query "[?name=='${VISTARA_FEDERATED_CREDENTIAL_NAME}'].audiences[0] | [0]" --output tsv 2>/dev/null || true)
actual_audience=$(printf '%s' "$actual_audience" | tr -d '\r\n')

if [ -z "$actual_issuer" ] && [ -z "$actual_subject" ]; then
  vistara_error "no federated identity credential named '${VISTARA_FEDERATED_CREDENTIAL_NAME}' exists on application ${client_id}."
  fail_with_manual_commands 'missing federated identity credential.'
fi

if [ "$actual_issuer" != "$expected_issuer" ]; then
  vistara_error "federated credential issuer mismatch: expected '${expected_issuer}', found '${actual_issuer}'."
  fail_with_manual_commands 'federated identity credential issuer mismatch.'
fi

if [ "$actual_subject" != "$expected_subject" ]; then
  vistara_error "federated credential subject mismatch: expected '${expected_subject}', found '${actual_subject}'."
  fail_with_manual_commands 'federated identity credential subject mismatch.'
fi

if [ "$actual_audience" != "$VISTARA_FEDERATED_CREDENTIAL_AUDIENCE" ]; then
  vistara_error "federated credential audience mismatch: expected '${VISTARA_FEDERATED_CREDENTIAL_AUDIENCE}', found '${actual_audience}'."
  fail_with_manual_commands 'federated identity credential audience mismatch.'
fi

vistara_log 'Reply URLs and the federated identity credential match the deployment.'
