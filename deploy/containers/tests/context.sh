#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"

expected_ignore() {
  local role="$1"

  cat <<'EOF'
**
!global.json
!Directory.Build.props
!Directory.Packages.props
!Vistara.slnx
!deploy/
!deploy/containers/
EOF

  if [[ "$role" == "migration" ]]; then
    cat <<'EOF'
!deploy/containers/migration-entrypoint.sh
EOF
  else
    cat <<'EOF'
!deploy/containers/build-libvips-runtime.sh
!deploy/licenses/
!deploy/licenses/NetVips-MIT.txt
!deploy/licenses/THIRD-PARTY-NOTICES.md
EOF
  fi

  cat <<'EOF'
!src/
EOF

  if [[ "$role" == "api" ]]; then
    cat <<'EOF'
!src/Vistara.Api/
!src/Vistara.Api/**
!src/Vistara.Contracts/
!src/Vistara.Contracts/**
EOF
  elif [[ "$role" == "worker" ]]; then
    cat <<'EOF'
!src/Vistara.Worker/
!src/Vistara.Worker/**
EOF
  else
    cat <<'EOF'
!src/Vistara.Migrations.Postgres/
!src/Vistara.Migrations.Postgres/**
!src/Vistara.Migrations.Sqlite/
!src/Vistara.Migrations.Sqlite/**
EOF
  fi

  if [[ "$role" != "migration" ]]; then
    cat <<'EOF'
!src/Vistara.Application/
!src/Vistara.Application/**
!src/Vistara.Auth/
!src/Vistara.Auth/**
!src/Vistara.Domain/
!src/Vistara.Domain/**
!src/Vistara.Imaging.NetVips/
!src/Vistara.Imaging.NetVips/**
!src/Vistara.Observability/
!src/Vistara.Observability/**
!src/Vistara.Persistence/
!src/Vistara.Persistence/**
!src/Vistara.Storage.Azure/
!src/Vistara.Storage.Azure/**
!src/Vistara.Storage.Local/
!src/Vistara.Storage.Local/**
!src/Vistara.Storage.S3/
!src/Vistara.Storage.S3/**
EOF
  else
    cat <<'EOF'
!src/Vistara.Application/
!src/Vistara.Application/**
!src/Vistara.Auth/
!src/Vistara.Auth/**
!src/Vistara.Domain/
!src/Vistara.Domain/**
!src/Vistara.Persistence/
!src/Vistara.Persistence/**
EOF
  fi

  if [[ "$role" == "api" ]]; then
    cat <<'EOF'
!src/Vistara.Web/
!src/Vistara.Web/**
EOF
  fi

  cat <<'EOF'
**/bin
**/bin/**
**/obj
**/obj/**
**/node_modules
**/node_modules/**
**/dist
**/dist/**
**/dist-pages
**/dist-pages/**
**/.vite
**/.vite/**
**/coverage
**/coverage/**
**/playwright-report
**/playwright-report/**
**/test-results
**/test-results/**
**/artifacts
**/artifacts/**
**/TestResults
**/TestResults/**
**/*.trx
**/*.coverage
**/*.coveragexml
**/.env
**/.env.*
**/.git
**/.git/**
**/.github
**/.github/**
**/appsettings.*.local.json
**/secrets.json
**/*.key
**/*.pem
**/*.pfx
EOF
}

for role in api worker migration; do
  dockerfile="$repository_root/deploy/containers/${role}.Dockerfile"
  ignore_file="$repository_root/deploy/containers/${role}.Dockerfile.dockerignore"

  if grep -E '^[[:space:]]*COPY[[:space:]].*--exclude(=|[[:space:]])' "$dockerfile" >/dev/null; then
    echo "$role Dockerfile uses COPY --exclude, which is not supported by the CI Dockerfile frontend" >&2
    exit 1
  fi

  if ! diff --unified <(expected_ignore "$role") "$ignore_file"; then
    echo "$role Docker build context does not match its audited allowlist" >&2
    exit 1
  fi
done

echo "Docker build context allowlists passed."
