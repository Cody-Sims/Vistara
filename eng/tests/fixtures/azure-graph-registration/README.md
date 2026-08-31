# `azure-graph-registration` fixtures

Task-owned fixtures for `eng/tests/azure-graph-registration.test.mjs`, which
verifies `deploy/azure/infra/entra/app-registration.bicep` (HB-09).

| File | Role |
|---|---|
| `microsoft-graph-delegated-permissions.json` | Cited source of truth for the Microsoft Graph app ID and the `openid` / `profile` / `email` delegated permission IDs. The test asserts the Bicep module matches this file, so a wrong GUID fails in one place with its citation next to it. |
| `hosted-oidc-routes.json` | The frozen callback, front-channel-logout, and signed-out route contract shared by HB-09 (Entra registration), HB-11 (API routes), and HB-12 (`postprovision-verify-fic.sh`). |
| `app-registration.build.json` | `bicep build` output for the module, committed so the generated ARM and Graph type surface can be asserted without a Bicep CLI or network access. |

## Deterministic commands

The test resolves a Bicep CLI from `$VISTARA_BICEP_CLI`, then `bicep` on `PATH`.
When one is found it rebuilds and re-lints the module and fails on any drift
from `app-registration.build.json`; when none is found those two checks report
as skipped and every other assertion still runs against the committed build.

```bash
# Full run, including the Bicep 0.46.1 build, lint, and drift checks.
VISTARA_BICEP_CLI="$(command -v bicep)" node --test eng/tests/azure-graph-registration.test.mjs

# Fixture-only run, no Bicep CLI and no network required.
node --test eng/tests/azure-graph-registration.test.mjs
```

Install the pinned CLI with either of:

```bash
az bicep install --version v0.46.1 && export VISTARA_BICEP_CLI="$HOME/.azure/bin/bicep"
# or
curl -sSLo bicep https://github.com/Azure/bicep/releases/download/v0.46.1/bicep-osx-arm64 \
  && chmod +x bicep && export VISTARA_BICEP_CLI="$PWD/bicep"
```

## Regenerating `app-registration.build.json`

Run after any change to the module or to `deploy/azure/bicepconfig.json`, then
commit the result alongside it:

```bash
"$VISTARA_BICEP_CLI" build --stdout deploy/azure/infra/entra/app-registration.bicep \
  > eng/tests/fixtures/azure-graph-registration/app-registration.build.json
```

The comparison ignores `metadata._generator`, so a newer Bicep CLI does not
cause spurious drift; the test asserts the pinned generator version separately.
