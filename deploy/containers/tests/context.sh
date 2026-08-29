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
!deploy/containers/build-libvips-runtime.sh
!deploy/licenses/
!deploy/licenses/NetVips-MIT.txt
!deploy/licenses/THIRD-PARTY-NOTICES.md
!src/
EOF

  if [[ "$role" == "api" ]]; then
    cat <<'EOF'
!src/Vistara.Api/
!src/Vistara.Api/**
!src/Vistara.Contracts/
!src/Vistara.Contracts/**
EOF
  else
    cat <<'EOF'
!src/Vistara.Worker/
!src/Vistara.Worker/**
EOF
  fi

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

for role in api worker; do
  ignore_file="$repository_root/deploy/containers/${role}.Dockerfile.dockerignore"
  if ! diff --unified <(expected_ignore "$role") "$ignore_file"; then
    echo "$role Docker build context does not match its audited allowlist" >&2
    exit 1
  fi
done

echo "Docker build context allowlists passed."
