#!/usr/bin/env bash
set -euo pipefail

output="${1:-deploy/.env}"
if [[ -e "$output" && "${2:-}" != "--force" ]]; then
  printf 'Refusing to overwrite %s; pass --force as the second argument.\n' "$output" >&2
  exit 64
fi

command -v openssl >/dev/null 2>&1 || {
  echo "openssl is required to generate deployment credentials." >&2
  exit 69
}

random_urlsafe() {
  openssl rand -base64 "$1" | tr -d '\n' | tr '+/' '-_'
}

umask 077
cat >"$output" <<EOF
VISTARA_IMAGE_TAG=local
VISTARA_HTTP_PORT=8080
VISTARA_API_PEPPER=$(openssl rand -base64 32 | tr -d '\n')
VISTARA_OIDC_PROFILE=local
VISTARA_OIDC_ISSUER=https://issuer.example.invalid
VISTARA_OIDC_AUDIENCE=vistara-api
VISTARA_OIDC_METADATA_ADDRESS=https://issuer.example.invalid/.well-known/openid-configuration
VISTARA_POSTGRES_BOOTSTRAP_PASSWORD=$(random_urlsafe 36)
VISTARA_MIGRATOR_DB_PASSWORD=$(random_urlsafe 36)
VISTARA_API_DB_PASSWORD=$(random_urlsafe 36)
VISTARA_WORKER_DB_PASSWORD=$(random_urlsafe 36)
VISTARA_MINIO_ROOT_USER=vistara-admin
VISTARA_MINIO_ROOT_PASSWORD=$(random_urlsafe 36)
VISTARA_S3_ACCESS_KEY=vistara-app-$(random_urlsafe 12)
VISTARA_S3_SECRET_KEY=$(random_urlsafe 36)
EOF

printf 'Wrote %s with mode 0600. Replace the example OIDC values before enabling login.\n' "$output"
