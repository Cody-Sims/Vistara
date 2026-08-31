#!/usr/bin/env bash
# Fails when any restored .NET or npm dependency has a known vulnerability.
# Deprecated packages are reported without failing so that upgrades can be
# scheduled. The audit needs no repository secrets.
set -uo pipefail

dotnet_cli="${DOTNET:-dotnet}"
npm_cli="${NPM:-npm}"
solution="${SOLUTION:-Vistara.slnx}"
npm_prefixes=(src/Vistara.Web tests/Vistara.E2E eng)
audit_level="${NPM_AUDIT_LEVEL:-high}"
status=0

section() {
  printf '\n== %s ==\n' "$1"
}

section ".NET vulnerable packages"
dotnet_report="$("$dotnet_cli" list "$solution" package --vulnerable --include-transitive 2>&1)"
dotnet_status=$?
printf '%s\n' "$dotnet_report"
if [ "$dotnet_status" -ne 0 ]; then
  echo "dotnet list package --vulnerable failed with exit $dotnet_status." >&2
  status=1
elif printf '%s\n' "$dotnet_report" | grep -q "has the following vulnerable packages"; then
  echo "Vulnerable .NET packages were reported." >&2
  status=1
fi

section ".NET deprecated packages"
deprecated_report="$("$dotnet_cli" list "$solution" package --deprecated --include-transitive 2>&1)" || true
printf '%s\n' "$deprecated_report"
if printf '%s\n' "$deprecated_report" | grep -q "has the following deprecated packages"; then
  echo "::warning::Deprecated .NET packages are in use; schedule replacements."
fi

for prefix in "${npm_prefixes[@]}"; do
  section "npm audit ($prefix)"
  if [ ! -f "$prefix/package-lock.json" ]; then
    echo "$prefix has no package-lock.json to audit." >&2
    status=1
    continue
  fi
  audit_report="$("$npm_cli" --prefix "$prefix" audit --audit-level "$audit_level" 2>&1)"
  audit_status=$?
  printf '%s\n' "$audit_report"
  if [ "$audit_status" -ne 0 ]; then
    echo "npm audit reported $audit_level or higher findings in $prefix." >&2
    status=1
  fi
done

if [ "$status" -ne 0 ]; then
  echo "Dependency audit failed." >&2
  exit 1
fi

echo "Dependency audit passed: no vulnerable .NET or npm dependencies."
