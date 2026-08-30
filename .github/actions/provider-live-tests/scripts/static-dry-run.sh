#!/usr/bin/env bash
set -euo pipefail

action_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
fixtures="$action_root/fixtures/providers.json"

python3 - "$fixtures" "$action_root/scripts/validate-config.sh" <<'PY'
import json
import os
import subprocess
import sys

fixtures_path, validator = sys.argv[1:]
with open(fixtures_path, encoding="utf-8") as stream:
    fixtures = json.load(stream)

providers = sorted(fixture["provider"] for fixture in fixtures)
if providers != ["aws", "azure", "b2", "r2"]:
    raise SystemExit("dry-run fixtures must cover exactly aws, azure, r2, and b2")

for fixture in fixtures:
    environment = os.environ.copy()
    environment.update(
        PROVIDER=fixture["provider"],
        MODE="dry-run",
        BUCKET=fixture["bucket"],
        REGION=fixture["region"],
        ENDPOINT=fixture["endpoint"],
        AZURE_ACCOUNT=fixture["azureAccount"],
        AZURE_CONTAINER=fixture["azureContainer"],
        PREFIX=fixture["prefix"],
        CAPABILITIES=json.dumps(fixture["capabilities"], separators=(",", ":")),
    )
    subprocess.run([validator], env=environment, check=True)

unsafe = environment.copy()
unsafe["PREFIX"] = f"vistara-live/dry-run/{fixture['provider']}/../../shared"
if subprocess.run(
    [validator],
    env=unsafe,
    stdout=subprocess.DEVNULL,
    stderr=subprocess.DEVNULL,
).returncode == 0:
    raise SystemExit("validator accepted an unsafe cleanup prefix")

drifted = environment.copy()
drifted["CAPABILITIES"] = '{"Name":"drifted"}'
if subprocess.run(
    [validator],
    env=drifted,
    stdout=subprocess.DEVNULL,
    stderr=subprocess.DEVNULL,
).returncode == 0:
    raise SystemExit("validator accepted capability drift")
PY

echo "Provider live-test dry-run fixtures are valid."
