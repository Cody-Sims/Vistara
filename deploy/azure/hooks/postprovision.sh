#!/usr/bin/env bash
# Vistara hosted bootstrap — postprovision orchestration.
#
# `azd` allows one hook per event, so the ordered steps live here. The order is
# a dependency chain, not a preference:
#
#   1. the application registration needs the API host name and the API
#      identity principal ID, which only exist after provisioning;
#   2. verification is what turns a silently wrong federated credential into a
#      failed deployment instead of a failed sign-in a day later;
#   3. the API key pepper must exist in Key Vault before the pass that
#      references it starts a container;
#   4. the database roles must exist before the migration job connects;
#   5. the migration must succeed before the API and worker are turned on.
#
# The first failure stops the chain and keeps its exit code, so `up.sh` can
# report the specified taxonomy rather than a generic provisioning error.
if [ -z "${BASH_VERSION:-}" ]; then
  exec bash "$0" "$@"
fi

set -euo pipefail

HOOKS_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

# shellcheck source=lib/common.sh
. "${HOOKS_DIR}/lib/common.sh"

vistara_load_env

run_step() {
  local script="${HOOKS_DIR}/$1"
  local status=0
  [ -f "$script" ] || vistara_die "$VISTARA_EXIT_PROVISION" "missing hook ${script}."
  bash "$script" || status=$?
  if [ "$status" -ne 0 ]; then
    exit "$status"
  fi
}

run_step 'postprovision-app-registration.sh'
run_step 'postprovision-verify-fic.sh'
run_step 'postprovision-secrets.sh'
run_step 'postprovision-database.sh'
run_step 'postprovision-migrate.sh'

# The applications are only running after the activation pass, so the health
# gate is skipped on the first one and runs where it can actually prove
# something.
if [ "$(vistara_env VISTARA_DEPLOY_APPLICATIONS)" = 'true' ]; then
  run_step 'postdeploy-health.sh'
fi
