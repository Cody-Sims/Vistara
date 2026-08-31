#!/usr/bin/env bash
# Brings a Compose topology up, requires the declared services to become
# healthy, requires one-shot services to exit successfully, and probes the
# published health endpoints. Only the stack started here is torn down.
set -euo pipefail

docker_cli="${DOCKER:-docker}"
curl_cli="${CURL:-curl}"
compose_file=""
env_file=""
wait_timeout=300
project=""
services=()
completed=()
probes=()

fail() {
  echo "$*" >&2
  exit 1
}

usage() {
  cat <<'EOF'
Usage:
  compose-startup.sh --file COMPOSE_FILE --env-file ENV_FILE
                     [--project NAME] [--wait-timeout SECONDS]
                     [--service NAME]... [--completed NAME]... [--probe URL]...

--service names a container that must report a healthy healthcheck.
--completed names a one-shot container that must exit zero.
--probe names an HTTP endpoint that must answer successfully.

The gate tears its own stack down with volumes. It therefore generates a unique
project name when --project is omitted, and refuses any project that already
has containers so that operator data is never removed.
EOF
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --file) compose_file="${2:-}"; shift 2 ;;
    --env-file) env_file="${2:-}"; shift 2 ;;
    --project) project="${2:-}"; shift 2 ;;
    --wait-timeout) wait_timeout="${2:-}"; shift 2 ;;
    --service) services+=("${2:-}"); shift 2 ;;
    --completed) completed+=("${2:-}"); shift 2 ;;
    --probe) probes+=("${2:-}"); shift 2 ;;
    --help|-h) usage; exit 0 ;;
    *) usage >&2; fail "Unknown argument: $1" ;;
  esac
done

[ -n "$compose_file" ] || fail "--file is required."
[ -n "$env_file" ] || fail "--env-file is required."
[ -f "$compose_file" ] || fail "Compose file $compose_file was not found."
[ -f "$env_file" ] || fail "Environment file $env_file was not found."
[ "${#services[@]}" -gt 0 ] || fail "At least one --service is required."

if [ -z "$project" ]; then
  project="vistara-gate-$$-$(date +%s)"
fi
case "$project" in
  [a-z0-9]*) ;;
  *) fail "Compose project names must start with a lowercase letter or digit." ;;
esac

compose=(
  "$docker_cli" compose
  --env-file "$env_file"
  --file "$compose_file"
  --project-name "$project"
)

# Reusing a populated project would let the teardown below delete containers and
# volumes this gate never created.
existing="$("${compose[@]}" ps --all --quiet 2>/dev/null | tr -d '[:space:]')"
[ -z "$existing" ] ||
  fail "Compose project $project already has containers; refusing to reuse it."

cleanup() {
  "${compose[@]}" down --volumes --remove-orphans --timeout 30 >/dev/null 2>&1 || true
}
trap cleanup EXIT

"${compose[@]}" config --quiet ||
  fail "Compose file $compose_file is not valid."

if ! "${compose[@]}" up --detach --wait --wait-timeout "$wait_timeout"; then
  "${compose[@]}" ps >&2 || true
  "${compose[@]}" logs --tail 50 >&2 || true
  fail "Compose topology $compose_file did not start successfully."
fi

container_id() {
  local service="$1"
  local id
  id="$("${compose[@]}" ps --all --quiet "$service" | head -n 1 | tr -d '[:space:]')"
  [ -n "$id" ] || fail "Service $service has no container in $compose_file."
  printf '%s' "$id"
}

for service in "${services[@]}"; do
  id="$(container_id "$service")"
  status="$("$docker_cli" inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "$id" | tr -d '[:space:]')"
  [ "$status" = "healthy" ] ||
    fail "Service $service reported health status '$status' instead of healthy."
done

for service in ${completed[@]+"${completed[@]}"}; do
  id="$(container_id "$service")"
  exit_code="$("$docker_cli" inspect --format '{{.State.ExitCode}}' "$id" | tr -d '[:space:]')"
  [ "$exit_code" = "0" ] ||
    fail "One-shot service $service exited with $exit_code."
done

for probe in ${probes[@]+"${probes[@]}"}; do
  "$curl_cli" --fail --silent --show-error --max-time 15 --output /dev/null "$probe" ||
    fail "Health probe $probe did not answer successfully."
done

echo "Compose startup gate passed for $compose_file (project $project)."
