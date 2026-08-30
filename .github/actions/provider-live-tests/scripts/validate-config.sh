#!/usr/bin/env bash
set -euo pipefail

action_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

python3 - "$action_root/fixtures/providers.json" <<'PY'
import json
import os
import re
import sys
from urllib.parse import urlsplit


def fail(message):
    raise SystemExit(f"provider live-test configuration error: {message}")


fixtures_path = sys.argv[1]
with open(fixtures_path, encoding="utf-8") as stream:
    profiles = {
        fixture["provider"]: fixture["capabilities"]
        for fixture in json.load(stream)
    }

provider = os.environ.get("PROVIDER", "")
mode = os.environ.get("MODE", "")
bucket = os.environ.get("BUCKET", "")
region = os.environ.get("REGION", "")
endpoint = os.environ.get("ENDPOINT", "")
azure_account = os.environ.get("AZURE_ACCOUNT", "")
azure_container = os.environ.get("AZURE_CONTAINER", "")
prefix = os.environ.get("PREFIX", "")

if provider not in profiles:
    fail("provider must be one of aws, azure, r2, or b2")
if mode not in {"dry-run", "live"}:
    fail("mode must be dry-run or live")

try:
    capabilities = json.loads(os.environ.get("CAPABILITIES", ""))
except json.JSONDecodeError as error:
    fail(f"capabilities must be valid JSON ({error.msg})")
if capabilities != profiles[provider]:
    fail(f"capabilities do not match the checked-in {provider} profile")

if mode == "dry-run":
    prefix_pattern = rf"vistara-live/dry-run/{provider}/[0-9a-f]{{16}}"
else:
    prefix_pattern = rf"vistara-live/{provider}/[1-9][0-9]*-[1-9][0-9]*-[0-9a-f]{{16}}"
if re.fullmatch(prefix_pattern, prefix) is None:
    fail("prefix is not an isolated provider/run scope")


def validate_bucket(value):
    if re.fullmatch(r"[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]", value) is None:
        fail("bucket name is invalid")
    if ".." in value or ".-" in value or "-." in value:
        fail("bucket name is invalid")


def validate_endpoint(value):
    parsed = urlsplit(value)
    if (
        parsed.scheme != "https"
        or not parsed.hostname
        or parsed.username is not None
        or parsed.password is not None
        or parsed.query
        or parsed.fragment
        or parsed.path not in {"", "/"}
    ):
        fail("endpoint must be an HTTPS origin without credentials, path, query, or fragment")
    return parsed.hostname.lower()


if provider == "azure":
    if bucket or region or endpoint:
        fail("Azure configuration must not set S3 bucket, region, or endpoint")
    if re.fullmatch(r"[a-z0-9]{3,24}", azure_account) is None:
        fail("Azure account name is invalid")
    if (
        re.fullmatch(r"[a-z0-9](?:[a-z0-9-]{1,61}[a-z0-9])?", azure_container)
        is None
        or "--" in azure_container
    ):
        fail("Azure container name is invalid")
else:
    if azure_account or azure_container:
        fail("S3-compatible configuration must not set Azure account or container")
    validate_bucket(bucket)
    if provider == "aws":
        if endpoint:
            fail("AWS must use the SDK service endpoint")
        if re.fullmatch(r"[a-z]{2}(?:-gov)?-[a-z]+-[1-9][0-9]*", region) is None:
            fail("AWS region is invalid")
    elif provider == "r2":
        if region != "auto":
            fail("R2 region must be auto")
        host = validate_endpoint(endpoint)
        if re.fullmatch(r"[0-9a-f]{32}\.r2\.cloudflarestorage\.com", host) is None:
            fail("R2 endpoint must be the account S3 API origin")
    else:
        if re.fullmatch(r"[a-z]{2}-[a-z]+-[0-9]{3}", region) is None:
            fail("B2 region is invalid")
        host = validate_endpoint(endpoint)
        if host != f"s3.{region}.backblazeb2.com":
            fail("B2 endpoint must match the configured region")

print(f"Validated {mode} configuration for {provider}.")
PY
