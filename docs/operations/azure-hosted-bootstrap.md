# Azure hosted bootstrap

One command deploys Vistara to an Azure subscription, migrates the database,
and hands you a browser tab where a single allowlisted Microsoft Entra ID
account claims ownership:

```bash
./deploy/azure/up.sh
```

This is the supported path for a **hosted evaluation** deployment. It is not a
production edge architecture; [§10](#10-what-this-deployment-is-not) says
exactly what it leaves to you.

Two companion guides remain useful and are now background reading rather than
instructions: [Azure free credits](azure-free-credits.md) explains what the
credit offers actually give you and how Azure bills the services this template
creates, and [Azure identity, RBAC, and secrets](azure-identity-and-secrets.md)
explains the identity and secret model by hand. Neither is required to run
`up.sh`.

**Validation status:** every claim below is read out of the scripts and
templates in [`deploy/azure/`](../../deploy/azure/README.md), and the
repository gates listed in [§12](#12-validation-status) run against them on
every change. **No live Azure subscription drill has been run yet.** Treat the
first run as the drill, watch the cost, and report what differs.

## Contents

- [1. Before you start](#1-before-you-start)
- [2. Cost, and what the budget does not do](#2-cost-and-what-the-budget-does-not-do)
- [3. The first run](#3-the-first-run)
- [4. Claiming ownership](#4-claiming-ownership)
- [5. Rerunning, resuming, and previewing](#5-rerunning-resuming-and-previewing)
- [6. Non-interactive runs](#6-non-interactive-runs)
- [7. Custom images and private registries](#7-custom-images-and-private-registries)
- [8. What was created](#8-what-was-created)
- [9. Verifying, and reading logs safely](#9-verifying-and-reading-logs-safely)
- [10. What this deployment is not](#10-what-this-deployment-is-not)
- [11. Teardown](#11-teardown)
- [12. Validation status](#12-validation-status)
- [13. Troubleshooting](#13-troubleshooting)
- [14. Sources](#14-sources)

---

## 1. Before you start

### 1.1 Azure permissions

The deployment is **subscription-scoped**: it creates its own resource group,
a resource-group budget, and role assignments for three managed identities.

| You need | Why |
|---|---|
| **Owner** on the subscription, or **Contributor** plus **User Access Administrator** | The template creates the resource group and role assignments; Contributor alone cannot assign roles |
| Rights to register resource providers | The preflight registers `Microsoft.App`, `Microsoft.DBforPostgreSQL`, `Microsoft.OperationalInsights`, `Microsoft.KeyVault`, `Microsoft.Storage`, and `Microsoft.ManagedIdentity` if they are not already registered |
| Cost Management access on the subscription | The resource-group budget is created by `deploy/azure/infra/modules/budget.bicep`; Owner covers this |
| Microsoft Entra **Application.ReadWrite.OwnedBy** (or Application Administrator) | The application registration, its service principal, and its federated identity credential are created by `deploy/azure/hooks/postprovision-app-registration.sh` |

If you have no directory rights, either an administrator grants them or an
administrator pre-creates the registration and you deploy against it with
`--skip-app-registration --client-id` — both paths, and what each one can and
cannot fill in for you, are in
[§13.6](#136-insufficient-directory-rights-for-the-application-registration-77).
The preflight probes for this **before** anything is created, so you find out
in seconds rather than after a provisioning run.

The Entra sign-in this deployment registers is single tenant
(`signInAudience: AzureADMyOrg`) and asks only for the delegated Microsoft
Graph scopes `openid`, `profile`, and `email`. No application permission is
requested, and both implicit grants are explicitly disabled.

### 1.2 Tools

`up.sh` checks for `az`, `azd`, `curl`, and `openssl` before it does anything at
all, and the preflight that `azd` runs at the start of each provisioning pass
re-checks them with version floors and adds Bicep:

| Tool | Minimum | Install |
|---|---|---|
| Azure CLI (`az`) | 2.89.1 | <https://aka.ms/azure-cli> |
| Azure Developer CLI (`azd`) | 1.32.0 | <https://aka.ms/azd-install> |
| Bicep | 0.36.1 (0.46.1 is the compiler CI validates against) | `az bicep install` |
| `curl` | any | preinstalled on macOS and most Linux |
| `openssl` | any | generates the API key pepper |

**Install a PostgreSQL client, or Docker, before you start.** The three database
principals are created by running `deploy/azure/sql/bootstrap-roles.sql`, and
that step uses `psql` if you have it and otherwise runs the same file inside a
`postgres:17-alpine` container:

```bash
brew install libpq        # macOS; or: apt install postgresql-client
```

Neither is part of the up-front tool check today. The database step runs after
the platform pass, so a machine with neither is turned away there with exit 69,
once resources already exist. Nothing is deleted, nothing is half-configured,
and rerunning the same command after installing one resumes at that step — but
it is a slower way to find out than installing it now. `--what-if`
([§5.2](#52-preview-with---what-if)) never reaches that step and needs neither.

`open` or `xdg-open` is optional. Without one, the sign-in URL is printed
rather than launched, and `up.sh` tells you so at the start instead of at the
end of a twenty-minute run.

Sign in first — the whole run inherits this session:

```bash
az login
az account show --output table
```

### 1.3 Release images

The templates accept **digests only**. A tag can be repointed after it was
reviewed, and a rollback that depends on `:latest` is not a rollback.

With no arguments, `up.sh` resolves the latest published release of this
repository and looks up the digests of three public GHCR images:

- `ghcr.io/<owner>/vistara-api`
- `ghcr.io/<owner>/vistara-worker`
- `ghcr.io/<owner>/vistara-migrations`

The owner comes from the `origin` git remote. The release tag comes from
following `https://github.com/<owner>/<repo>/releases/latest`, over HTTPS on
every hop, and is believed only when the redirect lands on a release tag of
that exact repository.

If no release has been published yet, resolution fails with a usage error and
you must pin the images yourself — see
[§7](#7-custom-images-and-private-registries).

### 1.4 Tenant and subscription must agree

`az ad` has no tenant switch: it answers for whichever directory the CLI is
currently pointed at. If the subscription you are deploying into belongs to a
different tenant than your active sign-in, `up.sh` stops and tells you to point
the CLI yourself rather than silently changing it for every shell you have
open:

```bash
az account set --subscription <subscription-id>
# or, across directories
az login --tenant <tenant-id>
```

---

## 2. Cost, and what the budget does not do

**A budget is a tripwire, not a circuit breaker.** [Verified] "Notifications
are triggered when the budget thresholds are exceeded. Resources aren't
affected, and your consumption isn't stopped."
— <https://learn.microsoft.com/en-us/azure/cost-management-billing/costs/tutorial-acm-create-budgets>

`up.sh` creates a monthly, resource-group-scoped budget at `--budget-amount`
dollars (default **25**) with three notifications: actual above 50%, actual
above 90%, and forecast above 100%. Alerts go to the resource group's **Owner**
role; set `VISTARA_BUDGET_CONTACT_EMAILS` in the `azd` environment to add
explicit recipients. Cost data is typically 8–24 hours behind, budgets are
evaluated every 24 hours, and a brand-new subscription may need up to 48 hours
before budgets can be created at all
([azure-free-credits.md §2](azure-free-credits.md#2-budgets-and-alerts-they-do-not-cap-spend)).

The budget start date is written into the `azd` environment **once** and reused
by every later run. Cost Management accrues against the month a budget starts
in, so recomputing it each deployment would silently rebase the accrual.

What actually costs money here:

| Resource | Shape |
|---|---|
| API container app | 0.5 vCPU / 1 GiB, `minReplicas` 1, `maxReplicas` 2 (`--max-replicas`) |
| Worker container app | 0.5 vCPU / 1 GiB, exactly 1 replica |
| Migration job | 0.5 vCPU / 1 GiB, manual trigger only — it never runs by itself |
| PostgreSQL Flexible Server | `Standard_B1ms` by default (`--db-sku`), 32 GB, 7-day backups, billed **per full hour the server exists** |
| Log Analytics | ingestion is billed outside the Container Apps free grant |
| Storage account | `Standard_LRS`, per GB plus transactions plus egress |

Two always-on replicas at 0.5 vCPU / 1 GiB exhaust the monthly Container Apps
free grant in roughly 100 replica-hours, so **no always-on evaluation
deployment is free**
([azure-free-credits.md §3.2](azure-free-credits.md#32-azure-container-apps-the-recommended-target)).

The cheapest way to pause without losing data is the retaining teardown in
[§11](#11-teardown), followed by stopping the database server.

---

## 3. The first run

```bash
./deploy/azure/up.sh
```

### 3.1 What you are asked

| Prompt | What it means |
|---|---|
| `Azure region (for example eastus, westeurope):` | Only asked when neither `--location` nor `AZURE_LOCATION` is set. Every resource goes here |
| `Create or update this deployment? [y/N]` | Nothing has been created yet. Answering anything but yes exits 0 and changes nothing — not even your selected subscription or the `azd` environment |
| `Confirm <object-id> as the first owner? [y/N]` | This is the **only** directory account that may claim the deployment. Declining exits 64 |

Above the prompts, `up.sh` prints the whole decision: subscription, tenant,
environment name, region, first owner, the three image digests, the monthly
budget, and the database SKU. Read it before answering.

### 3.2 Defaults

| Setting | Default | Flag |
|---|---|---|
| Environment name | `vistara-eval` | `--env-name` |
| Subscription | the current `az account` | `--subscription` |
| Tenant | the tenant of that subscription | `--tenant-id` |
| First owner | the signed-in user's object ID | `--owner-object-id` |
| Image namespace | `ghcr.io/<git remote owner, lowercased>` | `--image-namespace` |
| Release | the latest published release | `--release` |
| Budget | 25 USD per month | `--budget-amount` |
| Database SKU | `Standard_B1ms` | `--db-sku` |
| API max replicas | 2 | `--max-replicas` |
| Custom domain | none | `--custom-domain` |
| Data retention on teardown | retained | see [§11](#11-teardown) |

The environment name must be 3–32 alphanumeric or dash characters: it names
Azure resources. The resource group is `rg-vistara-<env-name>`.

### 3.3 The two passes, and why

The run provisions **twice**, because the dependency order requires it:

1. **Pass 1 — platform, identity, database, and migration.** Everything except
   the API and worker: managed identities, Log Analytics, the Container Apps
   environment, storage, Key Vault, PostgreSQL, role assignments, the budget,
   and the migration job. Then, as post-provision steps:
   - the Entra application registration is created against the real API host
     name and the API identity's principal ID, and its service principal and
     federated identity credential with it;
   - the registration is **verified** — reply URLs byte-compared, no
     `web.logoutUrl`, and the federated credential's issuer, subject, and
     audience byte-compared;
   - the API key pepper is generated by `openssl` straight into a `0600` file,
     written to Key Vault, and the file is shredded;
   - the three PostgreSQL principals are created and granted, with your own
     public IP allowed through the server firewall for exactly that step and
     removed again by an exit trap — including on failure or interrupt;
   - the migration job is started and polled to `Succeeded`.
2. **Pass 2 — API and worker.** Only now do application replicas start, with
   the client ID and the Key Vault pepper reference in place. The health gate
   then polls `/health/live`, `/health/startup`, and `/health/ready`, and
   finally `GET /api/v1/setup` to confirm the deployment actually advertises
   Entra sign-in.

The API refuses to start without the pepper, and a replica that starts against
an un-migrated schema fails readiness. Both passes are why the deployment is
healthy the first time rather than after a manual repair.

Every step is idempotent, and **a failed run never deletes anything.**

### 3.4 The end of the run

```text
Vistara is deployed.

  sign in       https://ca-api-vistara-eval.<region>.azurecontainerapps.io/login
  first owner   <object-id> in tenant <tenant-id>
  resource group rg-vistara-vistara-eval
```

The sign-in URL is printed on stdout and opened in your browser unless you
passed `--no-open`.

---

## 4. Claiming ownership

### 4.1 The Entra path

Open the printed `/login`, choose **Microsoft Entra ID**, and sign in. The API
runs the authorization code flow with PKCE, validates issuer, audience, tenant,
nonce, signature, and expiry, and only then creates the Vistara cookie session.
No Entra token is ever handed to browser JavaScript.

The first sign-in by the allowlisted object ID, in the allowlisted tenant,
claims the database-enforced bootstrap singleton **inside the same transaction**
that creates the tenant, the user, the external identity link, the owner
membership, and the audit record. Concurrent eligible sign-ins produce exactly
one winner; every other attempt fails closed and can sign in only after the
owner invites it. Once setup is closed, changing the allowlist cannot create a
second owner.

The allowlist is exact Entra object IDs (`oid`) in one directory tenant. An
email address, a domain, or a display name is deliberately not accepted: a
mailbox can be renamed or reassigned and must never grant ownership.

### 4.2 The local password path, and the race it creates

Local first-owner setup at `POST /api/v1/setup` stays available — it is the
recovery path when Entra is unavailable or misconfigured, and it is the reason
the deployment is not locked out by a directory problem. It claims the **same**
bootstrap singleton, so whichever path arrives first wins.

**Claim ownership immediately after `up.sh` finishes.** Until the singleton is
claimed, the deployment is publicly reachable over HTTPS and anyone who reaches
`/setup` can claim it. If you cannot sign in straight away, run the retaining
teardown in [§11](#11-teardown) and bring it back when you can.

If Entra sign-in is not advertised for any reason, the health gate says so, the
run still succeeds, and the URL it prints is `/setup` rather than `/login`.

### 4.3 Signing out

Sign-out is **relying-party initiated**: an authenticated, CSRF-protected
`POST /api/v1/auth/oidc/entra/sign-out` revokes the Vistara session and returns
the Entra `end_session_endpoint` URL for the browser to follow. Entra then
returns the browser to the registered `/api/v1/auth/oidc/entra/signed-out`
route.

**No front-channel logout URL is registered, on purpose.** Entra issues
front-channel logout as a cross-site `GET`, and the `__Host-vistara-session`
cookie is `SameSite=Lax`, so the browser never attaches it — the endpoint would
appear to sign the user out while the session stayed valid. The verification
hook fails the deployment if a `web.logoutUrl` ever appears on the
registration. The API keeps `/api/v1/auth/oidc/{providerId}/frontchannel-logout`
only as an inert compatibility path that nothing points at.

The two registered reply URLs are therefore exactly:

```text
https://<api-host>/api/v1/auth/oidc/entra/callback
https://<api-host>/api/v1/auth/oidc/entra/signed-out
```

---

## 5. Rerunning, resuming, and previewing

### 5.1 Rerun to resume

```bash
./deploy/azure/up.sh --env-name vistara-eval
```

Nothing is deleted by a failure, and the same command resumes at the first step
that has not completed. What makes that safe:

- an existing Entra registration is **patched back** into the declared shape
  rather than duplicated;
- an existing API key pepper is **reused**, so API keys hashed with it stay
  valid;
- the database bootstrap records the exact server-and-identity signature it
  completed for, and skips when it matches — so a rerun does not reopen the
  firewall;
- the migration records the digest it completed, and skips when the digest is
  unchanged;
- the budget start date, once written, is never recomputed.

To force a step: `VISTARA_FORCE_DATABASE_BOOTSTRAP=1` or
`VISTARA_FORCE_MIGRATION=1`.

### 5.2 Preview with `--what-if`

```bash
./deploy/azure/up.sh --env-name vistara-eval --what-if
```

A preview **changes nothing**: not your selected subscription, not the `azd`
environment, not the recorded budget start date. It runs
`az deployment sub what-if` against `infra/main.bicep` with
`deployApplications=false`, so it previews the platform pass. It does not
preview the application pass, and it does not preview the hooks — the Entra
registration, the pepper, the database roles, and the migration are not ARM
resources and have no what-if. Because it never reaches a hook, it also needs
none of what they need: no PostgreSQL client, no Docker, and no application
registration rights — the directory probe runs in the preflight, which a
preview never triggers. It still needs a first owner, so pass
`--owner-object-id` if your signed-in object ID cannot be read.

### 5.3 Upgrading images

Rerun with a newer release; the digests are re-resolved and the migration job
runs again because its digest changed:

```bash
./deploy/azure/up.sh --env-name vistara-eval --release v0.2.0
```

Migration compatibility and rollback rules are in the
[release runbook](release-runbook.md); infrastructure rollback means an earlier
set of digests, never restoring an older image against a newer schema.

---

## 6. Non-interactive runs

```bash
./deploy/azure/up.sh \
  --env-name vistara-eval \
  --location eastus \
  --subscription <subscription-id> \
  --owner-object-id <entra-object-id> \
  --client-ip "$(curl -fsS https://api.ipify.org)" \
  --yes --no-open
```

- `--yes` accepts every confirmation. It **requires** `--owner-object-id`: the
  first owner claims the deployment and must be stated, not inferred. It also
  requires `--location` (or `AZURE_LOCATION`).
- `--no-open` suppresses the browser launch; the URL is still printed.
- `--client-ip` avoids the public-IP lookup. Without it, the database step
  calls `https://api.ipify.org` (override with `VISTARA_PUBLIC_IP_URL`) to
  learn the address to allow through the PostgreSQL firewall for the duration
  of that step.

Without a terminal and without `--yes`, a confirmation fails the run with exit
64 rather than assuming an answer.

---

## 7. Custom images and private registries

### 7.1 Pinning digests

```bash
./deploy/azure/up.sh \
  --image-namespace ghcr.io/your-org \
  --api-digest sha256:<64 hex> \
  --worker-digest sha256:<64 hex> \
  --migration-digest sha256:<64 hex>
```

A tag is rejected outright, at the flag, at the preflight, and again in the
template. Any digest you do not pin is resolved from `--release`.

### 7.2 Private GHCR

`up.sh` has no flag for this; set the values in the `azd` environment and they
reach the template through `infra/main.parameters.json`:

```bash
azd env set VISTARA_REGISTRY_SERVER ghcr.io --environment vistara-eval
azd env set VISTARA_REGISTRY_USERNAME <github-username> --environment vistara-eval
azd env set VISTARA_REGISTRY_PASSWORD_SECRET_URI \
  https://<your-vault>.vault.azure.net/secrets/<name> --environment vistara-eval
```

Only a **Key Vault secret URI** is accepted; a password value never reaches a
parameter, a command line, or an `azd` value. Token creation and scoping are in
[azure-identity-and-secrets.md §5](azure-identity-and-secrets.md#5-private-ghcr-registry-credentials).

Two honest caveats:

- The secret must live in a vault that already exists and that the API, worker,
  and migration identities can read. This deployment's own vault is created by
  the same run, and the identities do not exist until it has run once, so
  expect to run `up.sh`, have the first pass fail while the reference cannot
  resolve, grant **Key Vault Secrets User** on your vault to the three
  identities (`az identity show --name id-vistara-{api,worker,migrate}-<env>
  --resource-group rg-vistara-<env> --query principalId`), and rerun.
- **This path is not exercised by any repository gate.** Public images are the
  reviewed path.

---

## 8. What was created

Everything lands in one resource group, `rg-vistara-<env-name>`, tagged
`azd-env-name=<env-name>`:

| Resource | Name | Notes |
|---|---|---|
| User-assigned identities ×3 | `id-vistara-{api,worker,migrate}-<env>` | Separate identities so grants and revocation stay independent |
| Log Analytics workspace | `log-vistara-<env>` | Container Apps logs |
| Container Apps environment | `cae-vistara-<env>` | Consumption |
| API container app | `ca-api-<env>` | External ingress, HTTPS only |
| Worker container app | `ca-worker-<env>` | No ingress |
| Migration job | `cj-migrate-<env>` | Manual trigger |
| PostgreSQL Flexible Server | `psql-vistara-<token>` | Entra authentication only |
| Storage account | `stvistara<token>` | Private `media` and `dataprotection` containers |
| Key Vault | `kv-vistara-<token>` | RBAC-authorized, soft delete on |
| Budget | `budget-vistara-<env>` | Resource-group scope |

The `azd` environment records the public configuration the scripts read back —
resource group, API URI, tenant, identity client and principal IDs, PostgreSQL
host, database, role names, storage and blob endpoints, Key Vault endpoint,
container app and job names, and the application client ID. **No output is a
secret**: no key, token, password, or shared access signature is ever emitted.

```bash
azd env get-values --environment vistara-eval
```

Role assignments the template makes, and nothing more:

| Identity | Role | Scope |
|---|---|---|
| API, worker | Storage Blob Data Contributor | the `media` **container** |
| API | Storage Blob Data Contributor | the `dataprotection` **container** |
| API, worker | Storage Blob Delegator | the storage **account** (user-delegation SAS signing is account-scoped; the role carries no data actions) |
| API | Key Vault Crypto User | the vault (unwraps the Data Protection key) |
| API, worker | Key Vault Secrets User | the vault |
| Migration | Key Vault Secrets User | the vault, **only** for a private registry |

The deploying principal is granted **Key Vault Secrets Officer** on the vault
so it can write the pepper, and `down.sh` removes that assignment again.

---

## 9. Verifying, and reading logs safely

The run already gated on all of this; these are the commands to repeat it.

```bash
RG=rg-vistara-vistara-eval
API=$(azd env get-value SERVICE_API_URI --environment vistara-eval)

curl -fsS "$API/health/live"
curl -fsS "$API/health/startup"
curl -fsS "$API/health/ready"
curl -fsS "$API/api/v1/setup"
```

`/api/v1/setup` is anonymous and throttled, and answers one boolean plus the
advertised provider keys — no identity count, tenant count, or topology.
`"id":"entra"` in that response is what the health gate requires before it
prints a `/login` URL.

Migration status:

```bash
az containerapp job execution list --name cj-migrate-vistara-eval --resource-group "$RG" --output table
az containerapp job logs show --name cj-migrate-vistara-eval --resource-group "$RG" \
  --container migrate --execution <execution-name> --tail 200
```

Application logs:

```bash
az containerapp logs show --name ca-api-vistara-eval --resource-group "$RG" --tail 200
az containerapp logs show --name ca-worker-vistara-eval --resource-group "$RG" --tail 200
```

**Before pasting any of this into an issue,** note that Azure diagnostics can
echo an access token or a connection string back at you. The bootstrap filters
every failure path through a redactor; when you copy output by hand, run it
through the same one:

```bash
az containerapp logs show --name ca-api-vistara-eval --resource-group "$RG" --tail 200 \
  | sed -e 's/eyJ[A-Za-z0-9_=-]\{10,\}\.[A-Za-z0-9_=.-]\{10,\}/[redacted-token]/g' \
        -e 's/sig=[A-Za-z0-9%_+\/=-]\{8,\}/sig=[redacted]/g'
```

The API key pepper, the PostgreSQL access token, and the Graph request bodies
never leave a `0700` scratch directory under `deploy/azure/.azure/<env>/.vistara`
as anything but `0600` files, and each is shredded as soon as the step that
needed it finishes.

---

## 10. What this deployment is not

### 10.1 Public HTTPS, evaluation grade

The API container app has **external ingress**: it is reachable from the public
internet over HTTPS on an `*.azurecontainerapps.io` host name, with plain HTTP
refused at the edge (`allowInsecure: false`). That is deliberate — Entra has to
be able to redirect a browser back to it — and it is enough for an evaluation.

It is **not** a production edge. There is no WAF, no private networking, no
DDoS plan, and no custom domain unless you pass `--custom-domain` with a
certificate resource ID. A production deployment terminates traffic at a
reviewed edge and treats this template as the workload behind it.

### 10.2 The rate limit counts the proxy, not the caller

Container Apps terminates TLS at a shared ingress proxy. Microsoft publishes no
address range for the internal hop that reaches the replica, so nothing here
can populate a reviewed forwarded-header trust list. The API therefore trusts
no forwarded address and discards `X-Forwarded-For`.

The consequence is stated out loud in the configuration rather than hidden:
`Platform__RateLimits__PartitionMode=SharedIngress`. **Every request in the
deployment shares one bucket**, because the only peer the replica can see is
the proxy. The hosted ceilings are raised to match (6000 API, delivery, and
media requests per minute; 600 event-stream requests, because a stream holds a
connection), and the application refuses a raised bucket unless the deployment
has declared whose requests it counts. Anything that needs per-client identity
must use an authenticated principal, not a synthesised address.

### 10.3 PostgreSQL is Entra-only, and publicly reachable

The server is created with `passwordAuth: Disabled` and
`activeDirectoryAuth: Enabled`: **no administrator password exists**. The
migration job, API, and worker each connect as their own role
(`vistara_migrator`, `vistara_api_runtime`, `vistara_worker_runtime`) with an
Entra access token supplied through Npgsql's periodic password provider, so
tokens rotate without a process restart. Only the migrator may change the
schema.

Public network access is **enabled**, with the built-in
`AllowAllAzureServicesAndResourcesWithinAzureIps` rule so the Container Apps
replicas can reach it. That rule admits every Azure-internal source, including
other tenants — an Entra token is what actually authorizes a connection, but
this is a real reduction in defence in depth and is the first thing to replace
with private networking for anything beyond evaluation
([azure-free-credits.md §5.4](azure-free-credits.md#54-postgresql-flexible-server)).

Your own address is allowed through only for the seconds the role bootstrap
takes, and the rule is removed by an exit trap.

### 10.4 Data Protection is durable and wrapped

The ASP.NET Core Data Protection key ring is persisted to the private
`dataprotection` blob container and wrapped with an RSA key in Key Vault, with
an application discriminator of `vistara-<env-name>`. That is what lets a
replica restart, or a second replica start, without invalidating every session
and antiforgery token. Deleting the storage account or the vault orphans every
payload protected by that ring, which is why both carry delete locks.

### 10.5 Identities are user-assigned, always named

All three identities are **user-assigned** and every client is handed an
explicit client ID (`Media__Storage__Azure__ManagedIdentityClientId`,
`Persistence__Azure__ManagedIdentityClientId`,
`Security__DataProtection__ManagedIdentityClientId`, and `AZURE_CLIENT_ID`).
A system-assigned identity is not supported, nothing chains to a developer
credential, and no ambient identity on the replica can be picked up instead.
Blob access uses no account key and no SAS: `allowSharedKeyAccess` is false.

---

## 11. Teardown

### 11.1 Keep the data, stop the bill

```bash
./deploy/azure/down.sh --env-name vistara-eval
```

The default is deliberately partial. It lists the exact resources first, asks
once, and then deletes only compute:

- **deleted:** container apps, the migration job, the Container Apps
  environment, Log Analytics;
- **kept:** the PostgreSQL server, the media storage account, the Key Vault,
  and the budget.

It also removes the operator's **Key Vault Secrets Officer** assignment, and
prints the retained resource IDs plus month-to-date spend against the budget.

The retained resources still cost money. A PostgreSQL flexible server is billed
per full hour it **exists**; stopping it removes only the compute charge, and
provisioned storage and backups keep billing:

```bash
az postgres flexible-server stop --resource-group rg-vistara-vistara-eval --name <server>
```

Bring it all back with `./deploy/azure/up.sh --env-name vistara-eval`, which
recreates the compute against the same data.

### 11.2 Delete everything

```bash
./deploy/azure/down.sh --env-name vistara-eval --delete-data
```

This one requires you to **type the environment name**, not press `y`. It then
removes the `CanNotDelete` locks, runs `azd down --force --purge`, and deletes
the Entra application registration if this deployment created it (keep it with
`--keep-app-registration`).

What that means in practice:

- Every image, every user, and the Data Protection key ring go with it. This
  cannot be undone.
- `--purge` purges the soft-deleted Key Vault, so the vault name is immediately
  reusable. Without purging, a soft-deleted vault would block a redeployment
  under the same name for its 7-day retention.
- The recorded budget start date is cleared **only here**, because the budget
  itself was deleted with the resource group. A start date in a past month is
  rejected by Cost Management, so a stale value would block the next
  deployment. The retaining teardown leaves both the budget and its date alone,
  on purpose.

### 11.3 Do not run `azd down` directly

A bare `azd down` would delete the compute first and only then fail on the data
locks, leaving a half-torn-down environment. The predown hook refuses it and
points you back here. `down.sh` also proves the target before deleting
anything: a resource group whose `azd-env-name` tag does not match the
environment is refused (exit 77), and every destructive call names the
subscription explicitly.

---

## 12. Validation status

| Checked by | What it proves |
|---|---|
| `node --test eng/tests/azure-bootstrap-up.test.mjs eng/tests/azure-bootstrap-down.test.mjs eng/tests/azure-bootstrap-contract.test.mjs` | The wrapper's argument handling, prompts, idempotence, exit codes, and teardown behaviour |
| `node --test eng/tests/azure-bicep-infra.test.mjs` | The template's parameters, identities, role assignments, locks, and configuration surface |
| `node --test eng/tests/azure-graph-registration.test.mjs` | The Entra registration shape, built and linted with the pinned Bicep compiler |
| `.github/workflows/repository-tooling.yml` | All of the above, on every change under `deploy/azure/**` |

Those run against stand-ins: a faked `az`, `azd`, `psql`, and `docker`. They
prove what the scripts do, not what Azure does with it.

**Two things are therefore not covered by ordinary CI.**

The bootstrap SQL is checked against a real PostgreSQL server — the one part of
the bootstrap a stand-in cannot judge, because a grant the Azure administrator
role cannot make is answered with a warning rather than an error and would
otherwise pass silently. That check is **opt-in and local**: it needs a running
Docker daemon, so it is skipped unless you ask for it, including in CI.

```bash
VISTARA_POSTGRES_SQL_CHECK=1 node --test eng/tests/azure-bootstrap-sql.test.mjs
```

Run it locally after changing `deploy/azure/sql/bootstrap-roles.sql`; a green CI
run does not include it.

**Not yet done:** a live deployment drill against a real Azure subscription —
`up.sh`, sign in, upload, `down.sh` — with observed cost and timings. That drill
is the verification the Wave 8 plan attaches to its deployment and operator-guide
work (`CLOUD-07` and `CLOUD-08` in
[`docs/specification.md`](../specification.md)), and it is the last outstanding
item in
[the plan's rollout sequence](../future-plans/hosted-identity-and-azure-bootstrap.md).
Until it reports, treat every timing, cost, and Azure-behaviour claim here as
derived from the sources cited in [§14](#14-sources) rather than observed, and
treat your first run as that drill.

---

## 13. Troubleshooting

### 13.1 Exit codes

`azd` collapses every hook failure into its own status, so the hooks record the
specific code and `up.sh` recovers it. Both scripts draw from one taxonomy;
`down.sh` uses 0, 64, 69, 70, and 77.

| Code | Meaning | Usually |
|---|---|---|
| `0` | Success, **or** a declined confirmation that changed nothing | — |
| `64` | Usage | A bad flag or value, a tenant that does not match the subscription, a first owner that was not confirmed, `--yes` without `--owner-object-id` or `--location`, a prompt with no terminal, an environment name `down.sh` cannot find, or a bare `azd down` blocked by the predown guard |
| `69` | Missing or too-old tool | `az`, `azd`, `curl`, or `openssl` before anything is created; Bicep at the preflight; neither `psql` nor `docker` at the database step, which is after the platform pass |
| `70` | Provisioning or teardown failure | An ARM error, an unresolvable Key Vault reference, a failed database bootstrap, a registration that does not match the deployment, or an `azd down` that did not finish |
| `71` | Migration failure | The migration job execution failed, or did not finish inside `VISTARA_MIGRATION_TIMEOUT_SECONDS` (default 900) |
| `75` | Health timeout | A probe never answered 200/204 inside `VISTARA_HEALTH_TIMEOUT_SECONDS` (default 300), or `/api/v1/setup` neither advertised Entra nor offered first-owner setup |
| `77` | Insufficient permissions, or a refused target | Not signed in, a subscription you cannot read, no directory rights, Key Vault role propagation that never arrived, or (in `down.sh`) a resource group tagged for a different environment |

In every `up.sh` failure: **nothing was deleted**, and the same command
resumes.

### 13.2 `the Azure CLI is not signed in` (77)

```bash
az login
```

If you have several tenants, sign in to the one that owns the subscription —
see [§1.4](#14-tenant-and-subscription-must-agree).

### 13.3 `the active Azure CLI subscription is X but this environment provisions into Y` (77)

The preflight refuses to deploy an environment into a subscription other than
the active one:

```bash
az account set --subscription <the environment's subscription>
```

### 13.4 `could not resolve an immutable digest` (64)

Either no release has been published in the namespace, or the package is
private, or the image name differs. Check that
`https://ghcr.io/v2/<namespace>/vistara-api/manifests/<tag>` is anonymously
pullable, or pin all three digests explicitly
([§7](#7-custom-images-and-private-registries)).

### 13.5 `no release tag could be resolved` (64)

The repository has no published release, or `origin` is not a GitHub remote.
Pass `--release <tag>`, or `--image-namespace` plus the three digests.

### 13.6 `insufficient directory rights for the application registration` (77)

There are two different failures here, at two different points in the run, and
they have different remedies. The message above is the first one.

**Before anything is created — the preflight cannot read the directory.**
`up.sh` probes `az ad app list` and refuses the run when the signed-in
principal cannot read application registrations. No resource group, no API host
name, no managed identity, and no deployment output exists at this point, so
nothing can be filled in for you: the commands `up.sh` prints here are a
**template with placeholders** (`<api fqdn>`, `<appId>`, `<appObjectId>`,
`<tenantId>`, `<api identity principal id>`), and they are addressed to a
directory administrator rather than to you. Two ways forward:

1. **Get the rights.** Ask a directory administrator for
   **Application.ReadWrite.OwnedBy** (or Application Administrator) on your
   account, then rerun `./deploy/azure/up.sh --env-name vistara-eval` normally.
   The bootstrap creates the registration, the service principal, and the
   federated credential itself, with the correct values, because by then it
   knows them. This is the path that needs no manual Entra work at all.
2. **Have the registration pre-created, and skip that step.** An administrator
   creates the application and its service principal:

   ```bash
   az ad app create --display-name "Vistara vistara-eval" \
     --sign-in-audience AzureADMyOrg \
     --enable-id-token-issuance false --enable-access-token-issuance false
   az ad sp create --id <appId>
   ```

   Then rerun against it:

   ```bash
   ./deploy/azure/up.sh --env-name vistara-eval \
     --skip-app-registration --client-id <appId>
   ```

   The reply URLs and the federated credential cannot be set correctly yet:
   both depend on the API host name and the API identity's principal ID, which
   the deployment has not created. So expect the run to provision the platform
   and then stop at the verification step with exit 70 and the **exact,
   filled-in** repair commands for that registration — see
   [§13.7](#137-federated-credential-issuersubjectaudience-mismatch-70) and
   [§13.8](#138-the-registered-reply-urls-do-not-match-or-weblogouturl-must-not-be-registered-70).
   Give those to the administrator, then rerun the same command; the run
   resumes and nothing was deleted.

**After the platform pass — the directory refuses a write.** If the preflight
passed but creating the application, its service principal, or its federated
credential is denied, the failure looks like `creating the application
registration failed`, `could not create the service principal for <appId>`, or
`could not create the federated identity credential`, each with the redacted
Graph error above it. The script does **not** print ready-made `az` commands
here; what it does have, and what you need, is the deployment's own output,
because the platform pass succeeded and recorded it. Read the three values and
hand them to an administrator:

```bash
azd env get-value SERVICE_API_URI --environment vistara-eval
azd env get-value API_IDENTITY_PRINCIPAL_ID --environment vistara-eval
azd env get-value AZURE_TENANT_ID --environment vistara-eval
```

The registration they build from those values is:

```bash
az ad app create --display-name "Vistara vistara-eval" \
  --sign-in-audience AzureADMyOrg \
  --web-redirect-uris \
    "<SERVICE_API_URI>/api/v1/auth/oidc/entra/callback" \
    "<SERVICE_API_URI>/api/v1/auth/oidc/entra/signed-out" \
  --enable-id-token-issuance false --enable-access-token-issuance false
az ad sp create --id <appId>
az ad app federated-credential create --id <appObjectId> --parameters \
  '{"name":"api-managed-identity","issuer":"https://login.microsoftonline.com/<AZURE_TENANT_ID>/v2.0","subject":"<API_IDENTITY_PRINCIPAL_ID>","audiences":["api://AzureADTokenExchange"]}'
```

The federated credential's subject must be the **lowercase** principal ID, and
`<appObjectId>` is `az ad app show --id <appId> --query id --output tsv`. Then
rerun with `--skip-app-registration --client-id <appId>`; the verification step
re-checks every one of those values against the deployment and fails with the
exact difference if any of them is wrong.

### 13.7 `federated credential issuer/subject/audience mismatch` (70)

A credential with a wrong value is accepted by Entra and fails only later, at a
real sign-in, with an unhelpful error — so it is byte-compared during
deployment instead. The verification hook only reads: it never silently
rewrites a registration you supplied. It prints the exact
`az ad app federated-credential create` command to repair it. The subject must
be the **lowercase** API identity principal ID.

### 13.8 `the registered reply URLs do not match` or `web.logoutUrl must not be registered` (70)

Someone edited the registration by hand, or it belongs to a different
deployment. The hook prints the `az ad app update` command that restores
exactly the two reply URLs and clears the logout URL. For why the logout URL
must not exist, see [§4.3](#43-signing-out).

### 13.9 `N Entra applications are named 'Vistara <env>'` (77)

Delete the duplicates, or pin the one you want with
`--skip-app-registration --client-id <appId>`.

### 13.10 `the deploying principal still cannot read secrets in <vault>` (77)

Role assignments are eventually consistent and the hook already polls for three
minutes. If it still fails, grant it explicitly and rerun:

```bash
az role assignment create --role "Key Vault Secrets Officer" \
  --assignee-object-id <your object id> --assignee-principal-type User \
  --scope "$(az keyvault show --name <vault> --resource-group <rg> --query id -o tsv)"
```

### 13.11 `could not determine this machine's public IPv4 address` (70)

The database step needs the address to allow through the server firewall:

```bash
./deploy/azure/up.sh --env-name vistara-eval --client-ip <your public IPv4>
```

An IPv6-only network cannot be allowed through a flexible-server firewall rule;
use a network with IPv4 egress for that step.

### 13.12 `neither psql nor docker is available` (69)

This one arrives **after** the platform pass, because the database step is the
first thing that needs a PostgreSQL client and it runs there rather than in the
up-front tool check ([§1.2](#12-tools)). The resources it created are intact
and nothing was rolled back.

Install a PostgreSQL client (`brew install libpq`, `apt install
postgresql-client`) or Docker, then rerun the same command; it resumes at the
database step. With Docker, the same SQL file runs in `postgres:17-alpine`
(`VISTARA_PSQL_IMAGE` overrides the image).

### 13.13 `role X is mapped to a different directory object` (70)

The database already has a role of that name bound to a previous identity, so
every token exchange would fail. Connect as the Entra administrator of the
server and `DROP ROLE "<name>";`, then rerun.

### 13.14 `this server will not let <you> set default privileges` (70)

The message names the fix: a role that administers `vistara_migrator` must run
`GRANT "vistara_migrator" TO "<your principal>";`, then rerun `up.sh`. Without
default privileges, tables created by a **later** migration would be
unreadable by the API and worker, so this fails the deployment rather than
warning.

### 13.15 Migration failed or timed out (71)

The API and worker were not deployed and nothing was deleted. Read the logs the
hook dumped, or:

```bash
az containerapp job execution show --name cj-migrate-vistara-eval \
  --resource-group rg-vistara-vistara-eval --job-execution-name <execution>
```

Fix the cause and rerun `up.sh`; the job is retried because its digest has not
been recorded as complete.

### 13.16 Health timeout (75)

```bash
az containerapp logs show --name ca-api-vistara-eval --resource-group rg-vistara-vistara-eval --tail 200
az containerapp revision list --name ca-api-vistara-eval --resource-group rg-vistara-vistara-eval --output table
```

Common causes: the Key Vault pepper reference cannot be resolved by the API
identity; the database roles or grants are missing; the image digest is for a
different architecture. Probes send an explicit `Host` header matching
`Security__Hosts__AllowedHosts`, so a custom domain without DNS or a
certificate is **not** a cause — the gate always uses the default ingress host.

### 13.17 A sign-in redirect fails at Entra

Compare the `redirect_uri` in the browser's address bar against the two
registered reply URLs in [§4.3](#43-signing-out). They must match byte for
byte, including scheme and trailing path. If you added `--custom-domain` after
the first deployment, the registration now points at a different host: rerun
`up.sh`, which reconciles the registration with the current API URI.

### 13.18 Sign-in succeeds but ownership is refused

Only the allowlisted object ID may claim the deployment, and only before the
bootstrap singleton is claimed. Check which object ID is allowlisted:

```bash
azd env get-value VISTARA_FIRST_OWNER_OBJECT_ID --environment vistara-eval
az ad signed-in-user show --query id --output tsv
```

If someone else already claimed ownership, no configuration change can create a
second owner — the existing owner must invite you.

---

## 14. Sources

Repository sources of truth: [`deploy/azure/up.sh`](../../deploy/azure/up.sh),
[`deploy/azure/down.sh`](../../deploy/azure/down.sh),
[`deploy/azure/azure.yaml`](../../deploy/azure/azure.yaml),
[`deploy/azure/hooks/`](../../deploy/azure/hooks/),
[`deploy/azure/infra/main.bicep`](../../deploy/azure/infra/main.bicep),
[`deploy/azure/infra/modules/`](../../deploy/azure/infra/modules/),
[`deploy/azure/infra/entra/app-registration.bicep`](../../deploy/azure/infra/entra/app-registration.bicep),
[`deploy/azure/sql/bootstrap-roles.sql`](../../deploy/azure/sql/bootstrap-roles.sql),
and `src/Vistara.Api/Features/Oidc/OidcRoutes.cs`.

Microsoft documentation (research date 2026-08-30, as for the companion
guides):

- Budgets do not stop consumption —
  <https://learn.microsoft.com/en-us/azure/cost-management-billing/costs/tutorial-acm-create-budgets>
- Cost data and evaluation latency —
  <https://learn.microsoft.com/en-us/azure/cost-management-billing/costs/cost-mgt-alerts-monitor-usage-spending>
- Container Apps billing and the monthly free grant —
  <https://learn.microsoft.com/en-us/azure/container-apps/billing>
- Container Apps ingress and outbound addresses —
  <https://learn.microsoft.com/en-us/azure/container-apps/networking>
- Container Apps Key Vault secret references —
  <https://learn.microsoft.com/en-us/azure/container-apps/manage-secrets>
- PostgreSQL Flexible Server billing and stop/start —
  <https://azure.microsoft.com/en-us/pricing/details/postgresql/flexible-server/>
- PostgreSQL Flexible Server access control and the `azure_pg_admin` role —
  <https://learn.microsoft.com/en-us/azure/postgresql/security/security-access-control>
- Microsoft Entra ID OpenID Connect protocol —
  <https://learn.microsoft.com/en-us/entra/identity-platform/v2-protocols-oidc>
- Workload identity federation (federated identity credentials) —
  <https://learn.microsoft.com/en-us/entra/workload-id/workload-identity-federation>
- User-assigned managed identities are the recommended type —
  <https://learn.microsoft.com/en-us/entra/identity/managed-identities-azure-resources/overview>
- Key Vault RBAC roles —
  <https://learn.microsoft.com/en-us/azure/key-vault/general/rbac-guide>
- Key Vault soft delete and purge —
  <https://learn.microsoft.com/en-us/azure/key-vault/general/soft-delete-overview>
- Azure Developer CLI —
  <https://learn.microsoft.com/en-us/azure/developer/azure-developer-cli/>
