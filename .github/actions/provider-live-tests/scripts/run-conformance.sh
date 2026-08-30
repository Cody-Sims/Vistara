#!/usr/bin/env bash
set -euo pipefail

results_directory="artifacts/provider-live/$PROVIDER"
result_file="$results_directory/$PROVIDER.trx"
mkdir -p "$results_directory"

export VISTARA_LIVE_STORAGE="true"
export VISTARA_LIVE_STORAGE_PROVIDER="$PROVIDER"
export VISTARA_LIVE_STORAGE_PROFILE="common"
export VISTARA_LIVE_STORAGE_CAPABILITIES="$CAPABILITIES"
export VISTARA_LIVE_STORAGE_PREFIX="$PREFIX"
export VISTARA_LIVE_STORAGE_BUCKET="$BUCKET"
export VISTARA_LIVE_STORAGE_REGION="$REGION"
export VISTARA_LIVE_STORAGE_ENDPOINT="$ENDPOINT"
export VISTARA_LIVE_STORAGE_AZURE_ACCOUNT="$AZURE_ACCOUNT"
export VISTARA_LIVE_STORAGE_AZURE_CONTAINER="$AZURE_CONTAINER"

set +e
dotnet test tests/Vistara.Storage.ConformanceTests \
  --configuration Release \
  --no-restore \
  --filter "Category=Live&Profile=Common&Provider=$PROVIDER" \
  --logger "trx;LogFileName=$PROVIDER.trx" \
  --results-directory "$results_directory" \
  --verbosity minimal
test_status=$?
set -e

if [[ "$test_status" -ne 0 ]]; then
  exit "$test_status"
fi

python3 - "$result_file" <<'PY'
import sys
import xml.etree.ElementTree as ET

root = ET.parse(sys.argv[1]).getroot()
counters = next(
    (element for element in root.iter() if element.tag.endswith("Counters")),
    None,
)
if counters is None or int(counters.attrib.get("total", "0")) < 1:
    raise SystemExit(
        "the live common conformance profile selected no tests; "
        "the provider fixture is missing or its traits do not match"
    )
PY
