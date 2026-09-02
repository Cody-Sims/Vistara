#!/usr/bin/env bash
# Vistara hosted bootstrap — health and sign-in readiness.
#
# Runs after the activation pass and after any `azd deploy`. Container Apps
# reports a revision as provisioned once the container is running, which says
# nothing about whether the application can serve a request, so the three
# application probes are polled directly:
#
#   /health/live     answered before rate limiting and authentication
#   /health/startup  the composition root finished
#   /health/ready    database, storage, and queue probes pass
#
# The default ingress host name is used rather than a custom domain, because a
# custom domain may not have DNS or a certificate yet, and the template always
# allows the default host. The `Host` header is sent explicitly so the request
# is the one the application's allowed-hosts guard sees.
if [ -z "${BASH_VERSION:-}" ]; then
  exec bash "$0" "$@"
fi

set -euo pipefail

# shellcheck source=lib/common.sh
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/common.sh"

vistara_load_env

if [ "$(vistara_env VISTARA_DEPLOY_APPLICATIONS)" != 'true' ]; then
  vistara_log 'applications are not deployed yet; skipping the health gate.'
  exit 0
fi

vistara_step 'Health'

resource_group=$(vistara_require_env AZURE_RESOURCE_GROUP)
api_app_name=$(vistara_require_env API_CONTAINER_APP_NAME)
service_uri=$(vistara_require_env SERVICE_API_URI)

ingress_fqdn=$(az containerapp show \
  --name "$api_app_name" \
  --resource-group "$resource_group" \
  --query properties.configuration.ingress.fqdn --output tsv 2>/dev/null || true)
ingress_fqdn=$(printf '%s' "$ingress_fqdn" | tr -d '\r\n')
if [ -z "$ingress_fqdn" ]; then
  vistara_die "$VISTARA_EXIT_HEALTH" \
    "container app ${api_app_name} has no ingress host name yet. Check: az containerapp show --name ${api_app_name} --resource-group ${resource_group}"
fi

base_uri="https://${ingress_fqdn}"
timeout_seconds=${VISTARA_HEALTH_TIMEOUT_SECONDS:-300}
poll_seconds=${VISTARA_HEALTH_POLL_SECONDS:-5}
body_file=$(vistara_private_file 'health-response')

probe() {
  local path=$1
  local code
  code=$(curl -sS -o "$body_file" -w '%{http_code}' \
    --max-time 15 \
    -H "Host: ${ingress_fqdn}" \
    "${base_uri}${path}" 2>/dev/null || true)
  printf '%s' "$(printf '%s' "$code" | tr -d '\r\n')"
}

wait_for() {
  local path=$1
  local deadline=$2
  local code=''
  while : ; do
    code=$(probe "$path")
    if [ "$code" = '200' ] || [ "$code" = '204' ]; then
      vistara_log "${path} answered ${code}."
      return 0
    fi
    if [ "$(vistara_seconds)" -ge "$deadline" ]; then
      vistara_error "${path} was still answering '${code:-no response}' after the health timeout."
      vistara_error "Inspect: az containerapp logs show --name ${api_app_name} --resource-group ${resource_group} --tail 200"
      vistara_die "$VISTARA_EXIT_HEALTH" "the API never became healthy at ${base_uri}${path}."
    fi
    sleep "$poll_seconds"
  done
}

deadline=$(( $(vistara_seconds) + timeout_seconds ))
wait_for '/health/live' "$deadline"
wait_for '/health/startup' "$deadline"
wait_for '/health/ready' "$deadline"

# ---------------------------------------------------------------------------
# Sign-in surface
#
# `/api/v1/setup` is anonymous and reports whether first-owner provisioning is
# still open and which hosted providers are advertised. A deployment that is
# healthy but does not advertise Entra would leave the operator staring at a
# login form they cannot use.
# ---------------------------------------------------------------------------

setup_code=$(probe '/api/v1/setup')
if [ "$setup_code" != '200' ]; then
  vistara_die "$VISTARA_EXIT_HEALTH" \
    "GET ${base_uri}/api/v1/setup answered '${setup_code:-no response}'."
fi

setup_body=$(tr -d ' \n\r' <"$body_file" 2>/dev/null || printf '')
sign_in_url="${service_uri}/login"
case "$setup_body" in
  *'"id":"entra"'*)
    vistara_log 'the API advertises Microsoft Entra sign-in.'
    ;;
  *)
    case "$setup_body" in
      *'"available":true'*)
        vistara_warn 'the API does not advertise Entra sign-in yet; the local first-owner form is still available.'
        sign_in_url="${service_uri}/setup"
        ;;
      *)
        vistara_die "$VISTARA_EXIT_HEALTH" \
          "the API neither advertises Entra sign-in nor offers first-owner setup. Check the Platform__Authentication__Oidc configuration on ${api_app_name}."
        ;;
    esac
    ;;
esac

vistara_shred "$body_file"

# `up.sh` reads this to print and open the URL, so both paths agree on one
# value rather than deriving it twice.
run_directory=$(vistara_run_dir)
printf '%s\n' "$sign_in_url" >"${run_directory}/sign-in-url"

vistara_log "sign-in URL ${sign_in_url}"
