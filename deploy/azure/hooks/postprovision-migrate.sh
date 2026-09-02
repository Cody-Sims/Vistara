#!/usr/bin/env bash
# Vistara hosted bootstrap — database migration.
#
# The migration job is a manual-trigger Container Apps job pinned to the same
# digest the API and worker run. It has to succeed before the applications are
# turned on: a replica that starts against an un-migrated schema fails its
# readiness probe and the deployment looks broken for the wrong reason.
#
# The execution that this run started is the execution that is polled. Reading
# "the latest execution" instead would silently follow someone else's run.
#
# Idempotent: a completed migration for the current image digest is recorded,
# so the activation pass does not run the job a second time.
if [ -z "${BASH_VERSION:-}" ]; then
  exec bash "$0" "$@"
fi

set -euo pipefail

# shellcheck source=lib/common.sh
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/common.sh"

vistara_load_env

vistara_step 'Database migration'

resource_group=$(vistara_require_env AZURE_RESOURCE_GROUP)
job_name=$(vistara_require_env MIGRATION_JOB_NAME)
migration_image=$(vistara_require_env VISTARA_MIGRATION_IMAGE)
migration_digest=${migration_image##*@}

if ! vistara_is_digest "$migration_digest"; then
  vistara_die "$VISTARA_EXIT_USAGE" \
    "VISTARA_MIGRATION_IMAGE must pin a digest, got '${migration_image}'."
fi

if [ "${VISTARA_FORCE_MIGRATION:-0}" != '1' ] \
  && [ "$(vistara_env VISTARA_MIGRATION_COMPLETED_DIGEST)" = "$migration_digest" ]; then
  vistara_log "migrations for ${migration_digest} already completed; nothing to do."
  exit 0
fi

timeout_seconds=${VISTARA_MIGRATION_TIMEOUT_SECONDS:-900}
poll_seconds=${VISTARA_MIGRATION_POLL_SECONDS:-10}

vistara_log "starting job ${job_name} at ${migration_digest}."
if ! execution_name=$(az containerapp job start \
  --name "$job_name" \
  --resource-group "$resource_group" \
  --query name --output tsv 2>&1); then
  vistara_error "$(printf '%s' "$execution_name" | vistara_redact)"
  vistara_die "$VISTARA_EXIT_MIGRATION" "could not start the migration job ${job_name}."
fi
execution_name=$(printf '%s' "$execution_name" | tr -d '\r\n')

if [ -z "$execution_name" ]; then
  vistara_die "$VISTARA_EXIT_MIGRATION" \
    "starting ${job_name} returned no execution name, so its outcome cannot be verified."
fi

vistara_log "execution ${execution_name}"

dump_logs() {
  vistara_error "migration logs for ${execution_name}:"
  az containerapp job logs show \
    --name "$job_name" \
    --resource-group "$resource_group" \
    --container migrate \
    --execution "$execution_name" \
    --tail 200 2>&1 | vistara_redact >&2 || vistara_warn 'no logs were available for this execution.'
}

deadline=$(( $(vistara_seconds) + timeout_seconds ))
status=''
while : ; do
  status=$(az containerapp job execution show \
    --name "$job_name" \
    --resource-group "$resource_group" \
    --job-execution-name "$execution_name" \
    --query properties.status --output tsv 2>/dev/null || true)
  status=$(printf '%s' "$status" | tr -d '\r\n')

  case "$status" in
    Succeeded)
      break
      ;;
    Failed|Degraded|Cancelled)
      vistara_error "migration execution ${execution_name} finished as ${status}."
      dump_logs
      vistara_error 'The API and worker were not deployed, and nothing was deleted.'
      vistara_error "Inspect: az containerapp job execution show --name ${job_name} --resource-group ${resource_group} --job-execution-name ${execution_name}"
      vistara_die "$VISTARA_EXIT_MIGRATION" 'database migration failed.'
      ;;
  esac

  if [ "$(vistara_seconds)" -ge "$deadline" ]; then
    vistara_error "migration execution ${execution_name} was still ${status:-unknown} after ${timeout_seconds}s."
    dump_logs
    vistara_error "Inspect: az containerapp job execution show --name ${job_name} --resource-group ${resource_group} --job-execution-name ${execution_name}"
    vistara_die "$VISTARA_EXIT_MIGRATION" 'database migration timed out.'
  fi

  sleep "$poll_seconds"
done

vistara_azd_env_set VISTARA_MIGRATION_COMPLETED_DIGEST "$migration_digest"
vistara_log "migration execution ${execution_name} succeeded."
