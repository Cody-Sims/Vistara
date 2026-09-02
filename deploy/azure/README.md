# Azure hosted bootstrap

One command deploys Vistara to an Azure subscription, migrates the database,
and opens a browser tab where one allowlisted Microsoft Entra ID account claims
ownership:

```bash
./deploy/azure/up.sh
```

```bash
./deploy/azure/down.sh                 # remove the compute, keep every byte of data
./deploy/azure/down.sh --delete-data   # remove everything, after a typed confirmation
```

**The operator runbook is
[`docs/operations/azure-hosted-bootstrap.md`](../../docs/operations/azure-hosted-bootstrap.md).**
It covers prerequisites, prompts, cost, ownership, reruns, `--what-if`,
non-interactive runs, private registries, verification, teardown, and every
exit code. This file only says what lives here.

`up.sh --help` and `down.sh --help` list every flag.

## Why a wrapper rather than bare `azd`

`azure.yaml` is a normal `azd` project, and `up.sh` is a wrapper around it
rather than a replacement for it. The wrapper resolves immutable image digests,
confirms the first owner, and runs the **two** provisioning passes the
dependency order requires: the Entra registration needs the API host name and
the API identity, which do not exist until the platform is deployed, and the
API refuses to start without the Key Vault pepper, which does not exist either.

Everything that happens *during* a pass lives in the hooks, so a direct
`azd provision` on an already-bootstrapped environment still runs the same
preflight, the same registration verification, and the same health gate.

`azd down` on its own is refused by the predown hook: the data resources carry
`CanNotDelete` locks, and a bare teardown would delete the compute first and
then fail on the locks.

## Layout

| Path | What it is |
|---|---|
| `up.sh` | The supported entry point: tool and account checks, digest resolution, confirmation, `--what-if`, two provisioning passes, sign-in URL |
| `down.sh` | Teardown: retains data by default; `--delete-data` needs the environment name typed |
| `azure.yaml` | The `azd` project: prebuilt images only, and the four hook bindings |
| `hooks/lib/common.sh` | Shared exit codes, redaction, `0600` scratch files, prompts, and `azd` environment access |
| `hooks/preprovision-preflight.sh` | Tool versions, sign-in, subscription match, digest-pinned images, resource providers, directory rights |
| `hooks/postprovision.sh` | Runs the ordered post-provision chain below and keeps the first failure's exit code |
| `hooks/postprovision-app-registration.sh` | Creates or reconciles the Entra application, service principal, and federated identity credential |
| `hooks/postprovision-verify-fic.sh` | Byte-compares reply URLs and the federated credential; read-only, never repairs |
| `hooks/postprovision-secrets.sh` | Generates the API key pepper into Key Vault without it ever reaching a command line |
| `hooks/postprovision-database.sh` | Opens the firewall for one step, runs `sql/bootstrap-roles.sql`, closes it again on any exit |
| `hooks/postprovision-migrate.sh` | Starts the migration job and polls the execution it started |
| `hooks/postdeploy-health.sh` | Polls `/health/live`, `/health/startup`, `/health/ready`, then `/api/v1/setup` |
| `hooks/predown-retention.sh` | Refuses an `azd down` that did not come through `down.sh` |
| `infra/main.bicep` | Subscription-scoped template: resource group, identities, monitoring, environment, storage, Key Vault, PostgreSQL, RBAC, budget, job, apps |
| `infra/modules/` | One module per resource group of concerns |
| `infra/entra/app-registration.bicep` | The declared registration shape the CLI path reproduces and the verification asserts |
| `infra/main.parameters.json` | Maps `azd` environment values onto template parameters |
| `bicepconfig.json` | Pins the Microsoft Graph Bicep extension and the linter rules the templates are built with |
| `sql/bootstrap-roles.sql` | Creates the three Entra database principals and their grants; safe to run again |

`.azure/` is created here by `azd` and is git-ignored. It holds environment
values and a `0700` scratch directory; nothing in it should be committed.

## Validation

The gates that cover this directory run in
`.github/workflows/repository-tooling.yml` and can be run locally:

```bash
npm ci --prefix eng
node --test eng/tests/azure-bootstrap-up.test.mjs
node --test eng/tests/azure-bootstrap-down.test.mjs
node --test eng/tests/azure-bootstrap-contract.test.mjs
node --test eng/tests/azure-bicep-infra.test.mjs
node --test eng/tests/azure-bootstrap-sql.test.mjs
node --test eng/tests/azure-graph-registration.test.mjs
```

`azure-graph-registration.test.mjs` builds and lints the Graph Bicep with a
pinned compiler; set `VISTARA_BICEP_CLI` to a Bicep 0.46.1 binary to run it
locally.

A live deployment drill against a real Azure subscription has not been run yet;
see the runbook's validation status section.

## Compose

The Compose topologies for self-hosting are documented in
[`deploy/README.md`](../README.md) and are unaffected by anything here.
