# Running Vistara on Microsoft Azure free credits

This guide shows how a developer can stand up a **non-production evaluation**
instance of Vistara on Microsoft Azure using the current free-credit offers,
and how to map what Azure gives you onto the configuration keys this
repository actually reads.

It is a cost-and-setup runbook, not a production architecture. For the
supported topologies see `deploy/README.md`; for the product and architecture
authority see `docs/specification.md`. Identity, RBAC, registry, and secret
detail lives in the companion
[Azure identity, RBAC, and secrets](azure-identity-and-secrets.md) guide so
this runbook stays linear.

> **There is now a one-command path.**
> [`./deploy/azure/up.sh`](../../deploy/azure/README.md) provisions the same
> shape of deployment from checked-in Bicep, with Entra sign-in, passwordless
> PostgreSQL, and a guarded teardown — see
> [Azure hosted bootstrap](azure-hosted-bootstrap.md). Prefer it. This guide
> remains useful for two things the bootstrap does not do: explaining what the
> free offers actually give you and how Azure bills these services, and giving
> you a manual CLI path when you cannot run the bootstrap. Sections that
> described the absence of Azure-native assets are corrected in
> [§13](#13-configuration-gaps-that-need-code-changes).

## How to read this document

Every non-obvious claim is labelled:

| Label | Meaning |
|---|---|
| **[Verified]** | Quoted or directly restated from the linked Microsoft page, or read out of this repository's own code and deployment files |
| **[Inferred]** | A reasonable choice or derivation by this guide; individually documented parts, combined here. Confirm before relying on it |
| **[Unverified]** | Deliberately not stated, because no primary source could be confirmed. Check the linked page yourself |

Microsoft changes offers, quotas, and CLI surfaces frequently. Re-check the
linked page before you spend anything, and prefer `az <group> --help` over this
document when the two disagree.

**Research date for all Microsoft citations below: 2026-08-30.**

**No free quantity is hardcoded in this guide.** The per-service monthly free
amounts (blob GB, PostgreSQL vCore-hours, and so on) are published on a
client-rendered pricing grid that could not be retrieved as primary text.
**[Unverified]** — read the current numbers yourself at
<https://azure.microsoft.com/en-us/pricing/free-services/> and in your own
portal's *Free services for 12 months* table before sizing anything.

## Contents

- [1. What the free offers actually give you](#1-what-the-free-offers-actually-give-you)
- [2. Budgets and alerts](#2-budgets-and-alerts-they-do-not-cap-spend)
- [3. Choosing an architecture Vistara can run on](#3-choosing-an-architecture-vistara-can-run-on)
- [4. Naming and shell variables](#4-naming-and-shell-variables)
- [5. Provisioning with the Azure CLI](#5-provisioning-with-the-azure-cli)
- [6. Mapping Azure outputs to Vistara configuration keys](#6-mapping-azure-outputs-to-vistara-configuration-keys)
- [7. Copy-paste `.env` template](#7-copy-paste-env-template)
- [8. Migrations, deployment, and validation](#8-migrations-deployment-and-validation)
- [9. Stop, start, and actually bound the bill](#9-stop-start-and-actually-bound-the-bill)
- [10. Backup](#10-backup)
- [11. Teardown](#11-teardown)
- [12. Cost traps checklist](#12-cost-traps-checklist)
- [13. Configuration gaps that need code changes](#13-configuration-gaps-that-need-code-changes)
- [14. Sources](#14-sources)

---

## 1. What the free offers actually give you

### 1.1 The Azure free account

**[Verified]** "Eligible new users get $200 Azure credit in your billing
currency for the first 30 days and a limited quantity of free services for 12
months with your Azure free account. As long as you have unexpired credit or
you use only free services within the limits, you're not charged."
— <https://learn.microsoft.com/en-us/azure/cost-management-billing/manage/avoid-charges-free-account>

The caveats matter more than the headline:

- **[Verified]** The subscription and its services **are disabled when the
  credit runs out or expires at the end of 30 days**. To keep going you must
  upgrade to pay-as-you-go, at which point real charges begin.
  — <https://learn.microsoft.com/en-us/azure/cost-management-billing/manage/upgrade-azure-subscription>
- **[Verified]** The 12 months of free services are available "only to new
  customers who have not previously had an Azure account or received 12 months
  of free services", are not currently available for pay-as-you-go in **China
  and India**, and require that you "move to pay as you go within 30 days to
  continue receiving 12 months free services."
  — <https://azure.microsoft.com/en-us/pricing/free-services/>
- **[Verified]** Free monthly quantities **do not roll over**: "The free
  quantity expires at the end of the month and doesn't roll over to the next
  month."
- **[Verified]** Free services apply "only ... for the subscription that was
  created when you signed up for your Azure free account" — a second
  subscription in the same tenant gets nothing.
- **[Verified]** Free-service usage reporting is "delayed for one to two days
  after you use a resource", so you can overspend before you can see it.
  — <https://learn.microsoft.com/en-us/azure/cost-management-billing/manage/check-free-service-usage>
- **[Verified]** "Free Azure trial subscriptions aren't eligible for limit or
  quota increases."
  — <https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/azure-subscription-service-limits>

Check consumption in the portal: **Subscriptions** → your free-account
subscription → **Top free services by usage** → **View all free services**. The
same blade's tooltip shows the 12-month expiry date. **[Verified]**, same
article.

### 1.2 Visual Studio subscription (Azure Dev/Test individual credit)

**[Verified]** "Azure Dev/Test credits are only available with select Visual
Studio Subscription levels", the credits "are intended for development and
testing workloads only and aren't designed for production use", and if the
Visual Studio subscription "expires or is removed, all the subscription
benefits, including the monthly Azure Dev/Test individual credit, are no
longer available."
— <https://learn.microsoft.com/en-us/azure/devtest/offer/quickstart-individual-credit>

Activate at <https://my.visualstudio.com/benefits> → Azure tile → **Activate**.
**[Verified]** The article does **not** publish a dollar amount; it varies by
subscription level, so this guide does not state one.

**[Inferred]** This is the best fit for a recurring Vistara sandbox: the credit
renews monthly instead of expiring after 30 days.

### 1.3 Microsoft for Startups

**[Verified]** "Unlock up to $150K in Startup credits over time as your
startup demonstrates verified progress, service adoption, and sustained Azure
usage." Eligibility requires (all of): a software-based product owned by the
company; privately held and for-profit; HQ in a country where Azure is
available; **not** having received more than $350,000 in lifetime free Azure
credits; **not** having raised Series C or later; not an educational
institution, government body, consultancy, or agency; and not crypto mining.
Applications "are typically reviewed within three business days."
— <https://learn.microsoft.com/en-us/startups/microsoft-for-startups/overview>,
apply at <https://startups.microsoft.com>

**[Verified]** Startup credits explicitly cover the services this guide uses:
Azure App Service / Container Apps, Azure Database for PostgreSQL Flexible
Server, Azure Blob Storage, Key Vault and Entra ID, and Cost Management
budgets. Azure Marketplace purchases and support plans might not be covered.
— <https://learn.microsoft.com/en-us/startups/benefits/azure-credits/use-azure-credits>

**[Unverified]** Whether startup credits can be attached to an existing free
subscription or require a new sponsored subscription. Check
<https://learn.microsoft.com/en-us/startups/benefits/azure-credits/azure-usage-and-billing>.

---

## 2. Budgets and alerts (they do not cap spend)

Set this up **first**, before creating any billable resource.

**[Verified]** "Notifications are triggered when the budget thresholds are
exceeded. Resources aren't affected, and your consumption isn't stopped."
A budget is a tripwire, not a circuit breaker — the only controls that
actually reduce spend are in
[§9](#9-stop-start-and-actually-bound-the-bill).
— <https://learn.microsoft.com/en-us/azure/cost-management-billing/costs/tutorial-acm-create-budgets>

Also **[Verified]**, from that tutorial and
<https://learn.microsoft.com/en-us/azure/cost-management-billing/costs/cost-mgt-alerts-monitor-usage-spending>:

- Budgets can be scoped to a management group, subscription, **or resource
  group** — scope yours to the Vistara resource group so unrelated spend does
  not mask it.
- "Cost and usage data is typically available within 8-24 hours and budgets
  are evaluated against these costs every 24 hours." Email notifications
  normally arrive within an hour of evaluation.
- On a brand-new subscription "you can't immediately create a budget" — "It
  might take up to 48 hours before you can use all Cost Management features."
- **Credit alerts are Enterprise Agreement only.** On a web-direct /
  pay-as-you-go free account you must use **budget** alerts.

CLI, noting the status banner **[Verified]**: "Command group 'consumption' is
in preview and under development."

```bash
az consumption budget create \
  --budget-name "vistara-eval" \
  --category "cost" \
  --amount 25.0 \
  --time-grain "monthly" \
  --start-date 2026-09-01 \
  --end-date 2027-09-01 \
  --resource-group-filter "$RG"
```

— <https://learn.microsoft.com/en-us/cli/azure/consumption/budget>

**[Verified]** This `create` verb has **no notification/recipient parameter**;
`az consumption budget create-with-rg` exposes `--notifications`, and Microsoft
Customer Agreement accounts are directed to the Budgets REST API. Attach alert
recipients in the portal (**Cost Management + Billing** → **Budgets**) unless
you are comfortable hand-writing the notification object.

---

## 3. Choosing an architecture Vistara can run on

Vistara is not a single web process. **[Verified from this repository]**:

- `deploy/containers/api.Dockerfile` builds the API **and** compiles the React
  SPA into `wwwroot`, so no separate static-site service is needed.
- Both `api.Dockerfile` and `deploy/containers/worker.Dockerfile` build and
  install **libvips 8.18.6 from source** into the runtime image, because the
  imaging provider is NetVips (`Media__Imaging__Provider: NetVips`).
- `deploy/containers/migration.Dockerfile` is a **third** image that must run
  to completion before API or worker start (`deploy/README.md`: "The migration
  container must complete successfully before API or worker start.").
- `deploy/README.md`: "The current API executable does **not** register worker
  hosted services", so the worker is a separate process, not a background
  thread in the API.
- Release images are published to `ghcr.io/<namespace>/vistara-api` and
  `ghcr.io/<namespace>/vistara-worker`
  (`.github/workflows/release-images.yml`).

### 3.1 Why App Service Free (F1) does not fit

**[Verified]** F1 limits, from the App Service section of
<https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/azure-subscription-service-limits>:
60 CPU-minutes per day (3 minutes per 5-minute window), 1 GB storage, 165 MB
bandwidth, a single shared instance that cannot scale out, 32-bit application
architecture, **0 custom domains**, and **no custom-domain TLS**.

**[Verified]** "In the Free and Shared tiers, an app receives CPU minutes on a
shared VM instance and can't scale out."
— <https://learn.microsoft.com/en-us/azure/app-service/overview-hosting-plans>

**[Verified]** .NET 10 is a first-class App Service runtime — the quickstart
has a `.NET 10` tab and names ".NET 10.0 (Long Term Support)", and
`az webapp up --sku F1 --name <app-name> --os-type linux` creates a Free-tier
app.
— <https://learn.microsoft.com/en-us/azure/app-service/quickstart-dotnetcore>

**[Inferred]** Even so, F1 is the wrong target for Vistara: a code-only deploy
would not have libvips, so `NetVipsImageProcessor` cannot initialise; 60 CPU
minutes per day is not enough for image derivative generation; and F1 gives
you one process, while Vistara needs API + worker + a migration run. A custom
container needs a paid plan tier, and **[Verified]** "Except for the Free tier,
an App Service plan carries a charge on the compute resources that it uses."

**[Unverified]** The exact `linuxFxVersion` string for .NET 10 on App Service
Linux — the configuration doc only shows `DOTNETCORE|8.0`. Do not hardcode it;
run `az webapp list-runtimes --os linux` and use what it returns.
— <https://learn.microsoft.com/en-us/azure/app-service/configure-language-dotnetcore>

### 3.2 Azure Container Apps: the recommended target

**[Verified]** "The following resources are free during each calendar month,
per subscription: The first 180,000 vCPU-seconds; the first 360,000 GiB-seconds;
the first 2 million HTTP requests. Free usage doesn't appear on your bill."
and "When a revision is scaled to zero replicas, no resource consumption
charges are incurred."
— <https://learn.microsoft.com/en-us/azure/container-apps/billing>

**[Verified]** The documented Consumption allocations start at
**0.25 vCPU / 0.5 GiB** and step through 0.5 vCPU / 1.0 GiB, 0.75 vCPU /
1.5 GiB, and upward; a Consumption-only environment is capped at 2 cores and
4 GiB.
— <https://learn.microsoft.com/en-us/azure/container-apps/containers>

#### Free-grant arithmetic **[Inferred]**

Both grant limits bind at the same replica-hour count for every allocation on
the documented CPU-to-memory ladder, because that ladder is a fixed
1 vCPU : 2 GiB ratio:

| Allocation | vCPU-s budget | GiB-s budget | Free replica-hours per month |
|---|---|---|---|
| 0.25 vCPU / 0.5 GiB | 180,000 / 0.25 = 720,000 s | 360,000 / 0.5 = 720,000 s | **≈ 200 h** |
| 0.5 vCPU / 1.0 GiB | 180,000 / 0.5 = 360,000 s | 360,000 / 1.0 = 360,000 s | **≈ 100 h** |

A calendar month is roughly 730 hours, so **no always-on replica fits inside
the free grant**, and two always-on replicas burn through it two to three times
faster.

**This guide sizes both apps at 0.5 vCPU / 1.0 GiB, which is ≈ 100 free
replica-hours per month, not 200.** **[Inferred]** That is the honest trade:
`deploy/compose.postgres.yml` sets a **1 GB memory limit** on both the API and
worker services, and both images run libvips-backed image transforms whose
peak resident set scales with decoded pixel dimensions. Halving memory to
0.5 GiB would double the free hours but leave almost no headroom above the
runtime plus a single decoded image, so an upload of a large photograph is
likely to be OOM-killed. If your workload is browsing-only — no uploads, no
derivative generation — 0.25 vCPU / 0.5 GiB and ≈ 200 hours is a reasonable
downgrade for the API; keep the worker at 0.5/1.0 GiB whenever it will
actually process images.

Budget your session time accordingly: at 0.5/1.0 GiB, roughly 100 hours of
combined API and worker replica time per calendar month is free, and
everything beyond that is billed. The grant is per subscription per calendar
month and is **not** tied to the 12-month free account, so it survives both the
30-day and 12-month cliffs.

### 3.3 Comparison

| Option | Fits Vistara? | Cost shape | Main catch |
|---|---|---|---|
| App Service **F1** | **No** | Free | No libvips on code-only deploy; 60 CPU-min/day; one process; no custom domain or TLS **[Verified limits / Inferred fit]** |
| App Service custom container (B1+) | Yes | Hourly per plan, per process | Every non-Free plan is charged **[Verified]** |
| **Container Apps Consumption** | **Yes — recommended** | Free grant, then per vCPU-s / GiB-s; zero when scaled to zero | ≈ 100 free replica-hours/month at 0.5 vCPU / 1.0 GiB **[Inferred]**; worker cannot scale to zero without a scale rule |
| PostgreSQL Flexible Server, **Burstable B1ms**, LRS backup | Yes | Billed per full hour the server **exists** | Stop it when idle; several settings are immutable **[Verified]** |
| Blob Storage `StorageV2`, `Standard_LRS` | Yes (native adapter) | Per GB + transactions + egress | Free monthly GB **[Unverified]** — check the grid |
| SQLite starter topology | Not on Azure | — | `deploy/README.md` requires a single host and a local volume; do not put it on Azure Files |

---

## 4. Naming and shell variables

**[Verified from `src/Vistara.Storage.Azure/AzureBlobStoreOptions.cs`]** the
application itself enforces the following, so pick names that satisfy them:

- Storage account name: **3–24 characters, lowercase ASCII letters and digits
  only**.
- Blob container name: **3–63 characters, lowercase letters, digits, and
  single non-leading, non-trailing hyphens** (`--` is rejected).
- The blob service URI must be **HTTPS, default port, no path, no query, no
  fragment**, and its host must be exactly
  `<accountname>.blob.core.windows.net` (or another trusted Azure cloud or
  private-link suffix). Anything else is rejected unless you are in emulator
  mode.

**[Verified]** Microsoft's own warning applies too: "Don't include any
personal, sensitive, or confidential information in resource names ... and
resource tags."
— <https://learn.microsoft.com/en-us/azure/postgresql/configure-maintain/quickstart-create-server>

**[Inferred]** A workable convention. Replace `abcd` with your own short
suffix; the storage account name must be globally unique.

```bash
export LOC="eastus"
export SUFFIX="abcd"                       # your own 4-8 lowercase chars
export RG="rg-vistara-eval-$SUFFIX"
export STORAGE="stvistara$SUFFIX"          # <=24 chars, lowercase+digits only
export CONTAINER="vistara-media"
export PG="pg-vistara-$SUFFIX"
export PGDB="vistara"
export PGADMIN="vistaraadmin"              # server admin login, not the app
export KV="kv-vistara-$SUFFIX"
export ACAENV="cae-vistara-$SUFFIX"
export APIAPP="ca-vistara-api"
export WORKERAPP="ca-vistara-worker"
export APIMI="id-vistara-api-$SUFFIX"      # user-assigned identity, API
export WORKERMI="id-vistara-worker-$SUFFIX"  # user-assigned identity, worker
export MIGJOB="caj-vistara-migrate"
export GHCR_NS="<your-github-namespace>"   # ghcr.io/<ns>/vistara-api
export IMAGE_TAG="<release-tag>"
```

---

## 5. Provisioning with the Azure CLI

Everything below runs from your workstation. Where a value is this guide's
choice rather than a documented example, it is labelled.

### 5.1 Sign in and select the subscription

```bash
az login
az account set -s "<subscriptionId>"
az account show --query "{name:name, id:id, state:state}" -o table
az account list-locations --query "[].{Region:name}" -o table
```

— <https://learn.microsoft.com/en-us/cli/azure/reference-index#az-login>,
<https://learn.microsoft.com/en-us/cli/azure/account>

**[Verified]** Free services only apply to the subscription created at
signup — confirm you selected that one.

### 5.2 Resource group

```bash
az group create --name "$RG" --location "$LOC"
```

— <https://learn.microsoft.com/en-us/azure/storage/common/storage-account-create>

Create the budget from [§2](#2-budgets-and-alerts-they-do-not-cap-spend) now,
scoped to `$RG`.

### 5.3 Storage account and media container

```bash
az storage account create \
  --name "$STORAGE" \
  --resource-group "$RG" \
  --location "$LOC" \
  --sku Standard_LRS \
  --kind StorageV2 \
  --min-tls-version TLS1_2 \
  --allow-blob-public-access false
```

**[Verified]** `--min-tls-version TLS1_2` and `--allow-blob-public-access false`
are taken from the article's own CLI example; `Standard_LRS` is a documented
redundancy value for `StorageV2`. **[Inferred]** substituting `Standard_LRS`
for the doc's `Standard_RAGRS` example, because LRS is the cheapest redundancy.
— <https://learn.microsoft.com/en-us/azure/storage/common/storage-account-create>

Data-protection settings Microsoft recommends — **[Verified]** the article
recommends blob soft delete and container soft delete with "a minimum retention
period of seven days" and blob versioning "for optimal data protection":

```bash
az storage account blob-service-properties update \
  --account-name "$STORAGE" --resource-group "$RG" \
  --enable-delete-retention true --delete-retention-days 7 \
  --enable-container-delete-retention true --container-delete-retention-days 7 \
  --enable-versioning true
```

Create the container using Entra credentials rather than an account key:

```bash
az storage container create \
  --name "$CONTAINER" \
  --account-name "$STORAGE" \
  --auth-mode login
```

**[Verified]** "Set the `--auth-mode` parameter to `login` to sign in using a
Microsoft Entra security principal (recommended) ... If you omit the
`--auth-mode` parameter, then the Azure CLI also attempts to retrieve the
access key."
— <https://learn.microsoft.com/en-us/azure/storage/blobs/authorize-data-operations-cli>

If this fails with an authorization error, assign yourself **Storage Blob Data
Contributor** first; see
[azure-identity-and-secrets.md §2](azure-identity-and-secrets.md#2-managed-identity-and-blob-rbac).

Optional hardening — `az storage account update --name "$STORAGE"
--resource-group "$RG" --allow-shared-key-access false` — is compatible with
Vistara's managed-identity path but **not** with its shared-key fallback, and
it also breaks `az storage cors add`. The trade-offs are in
[azure-identity-and-secrets.md §3](azure-identity-and-secrets.md#3-least-privilege-fallback-when-you-cannot-create-role-assignments).

### 5.4 PostgreSQL Flexible Server

> **Read this before creating the server.** This manual path uses **password
> authentication**, because it wires the application up with a connection
> string and nothing here refreshes an expiring token. Create the server with
> password authentication left **enabled** (the default) and do **not** pass
> `--password-auth Disabled`.
>
> Vistara itself is no longer limited to passwords: `Persistence:Azure`
> supplies an Entra access token through an `NpgsqlDataSource` periodic
> password provider, and the hosted bootstrap uses it to create an
> Entra-only server with no administrator password at all. If you want that,
> use [Azure hosted bootstrap](azure-hosted-bootstrap.md) rather than this
> section. See [§13](#13-configuration-gaps-that-need-code-changes).

```bash
az postgres flexible-server create \
  --resource-group "$RG" \
  --name "$PG" \
  --location "$LOC" \
  --tier Burstable \
  --sku-name Standard_B1ms \
  --storage-size 32 \
  --version 18 \
  --zonal-resiliency Disabled \
  --backup-retention 7 \
  --geo-redundant-backup Disabled \
  --public-access None \
  --admin-user "$PGADMIN" \
  --admin-password "<generated-strong-password>" \
  --yes
```

**[Verified]** Every parameter above appears in the current
`az postgres flexible-server create` synopsis, which lists `--tier`,
`--sku-name`, `--storage-size`, `--version`, `--zonal-resiliency
{Disabled, Enabled}`, `--backup-retention`, `--geo-redundant-backup
{Disabled, Enabled}`, `--public-access`, `--admin-user`, `--admin-password`,
and `--yes`. The synopsis has **no `--high-availability` parameter**;
high availability is expressed through `--zonal-resiliency` (with `--zone`,
`--standby-zone`, and `--allow-same-zone`), and `Disabled` is the
single-server, lowest-cost choice. PostgreSQL **18** is a supported version.
— <https://learn.microsoft.com/en-us/cli/azure/postgres/flexible-server>,
<https://learn.microsoft.com/en-us/azure/postgresql/configure-maintain/quickstart-create-server>

**[Verified]** `Standard_B1ms` appears literally in a doc example, `Burstable`
is a documented `--tier` value, and "Workload type 'Development' uses Burstable
SKUs". **[Inferred]** the exact pairing of the two: the doc's `Standard_B1ms`
example pairs it with `--tier GeneralPurpose`. Confirm regional availability
first:

```bash
az postgres flexible-server list-skus --location "$LOC" -o table
```

**[Verified]** Decisions you cannot change after creation: **storage type**,
**backup redundancy / geo-redundancy**, **networking mode (public vs.
private)**, and the **data encryption key**. Storage size can only be
increased, never shrunk. Backup retention (7–35 days) can be changed.

**[Verified]** Version 18 matches `postgres:18.0-bookworm` in
`deploy/compose.postgres.yml`, so migrations and behaviour match the
repository's reference topology.

**[Verified]** `--public-access None` keeps the public endpoint but adds no
firewall rules — the accepted values are "'Disabled', 'Enabled', 'All',
'None', `<startIpAddress>`, or `<startIpAddress>-<endIpAddress>`". Add rules
next.

#### Firewall

```bash
MYIP="$(curl -s https://api.ipify.org)"
az postgres flexible-server firewall-rule create \
  --resource-group "$RG" --server-name "$PG" \
  --name allow-workstation \
  --start-ip-address "$MYIP" --end-ip-address "$MYIP"
```

**[Verified]** For the "allow Azure services" rule, "`--end-ip-address` ... Use
value '0.0.0.0' to represent all Azure-internal IP addresses."
— <https://learn.microsoft.com/en-us/cli/azure/postgres/flexible-server/firewall-rule>

```bash
# Broad: allows every Azure-internal source, including other tenants.
az postgres flexible-server firewall-rule create \
  --resource-group "$RG" --server-name "$PG" \
  --name allow-azure-services \
  --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0
```

**[Inferred]** For a Consumption-only Container Apps environment this broad
rule is usually the only practical option, because **[Verified]** "Outbound
IPs might change over time"
(<https://learn.microsoft.com/en-us/azure/container-apps/networking>). It is a
real weakening: any Azure resource in any tenant can reach the listener, and
only your passwords stand between it and the database. Use long random
passwords, delete the rule when you are done, and prefer a VNet-integrated
environment with private access if you keep the instance around.

#### Create Vistara's least-privilege database roles

**[Verified from `deploy/postgres/init-runtime-roles.sh`]** the Compose
topology creates a database-owning migrator plus DDL-free API and worker logins.
Azure cannot run that init script, and Flexible Server is **not** a vanilla
PostgreSQL install, so the ordering below matters.

**[Verified]** what is different on Azure:

- "In cloud-based PaaS environments, access to an Azure Database for PostgreSQL
  superuser account is restricted to control plane operations only by cloud
  operators. Therefore, the `azure_pg_admin` account exists as a
  pseudo-superuser account. **Your administrator role is a member of the
  `azure_pg_admin` role.**" The admin is explicitly **not** a superuser.
- "In PostgreSQL 15 and later, the ownership of the public schema changed to
  the new `pg_database_owner` role ... **However, in Azure Database for
  PostgreSQL, this change doesn't apply. The public schema is owned by the
  `azure_pg_admin` role across all supported PostgreSQL versions.**" So schema
  -level `REVOKE`/`GRANT` must run as the **admin login**, not as the migrator,
  even though the migrator owns the database.
- "Newly created databases in Azure Database for PostgreSQL include a default
  set of privileges in the database's public schema that grant all database
  users and roles the ability to create objects ... consider revoking these
  default public privileges." The `REVOKE CREATE ON SCHEMA public` below is
  therefore load-bearing on Azure, not merely defensive.
- "Don't use the administrator role for the application."

— <https://learn.microsoft.com/en-us/azure/postgresql/security/security-access-control>

**Step 1 — as the admin login, connected to `postgres`.** Connect with
`psql "host=$PG.postgres.database.azure.com user=$PGADMIN dbname=postgres
sslmode=require"`. Do not use `az postgres flexible-server db create`; it would
create the database owned by the admin. Substitute your `$PGADMIN` value for
`<admin-login>` and Key Vault passwords for the placeholders.

```sql
CREATE ROLE vistara_migrator
  LOGIN PASSWORD '<migrator-password>'
  NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;
CREATE ROLE vistara_api_runtime
  LOGIN PASSWORD '<api-password>'
  NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;
CREATE ROLE vistara_worker
  NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;
CREATE ROLE vistara_worker_runtime
  LOGIN PASSWORD '<worker-password>'
  NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION
  IN ROLE vistara_worker;

-- Required before the next two statements: PostgreSQL states "To create a
-- database owned by another role, you must be able to SET ROLE to that role",
-- and ALTER DEFAULT PRIVILEGES can only change "the defaults of roles that
-- you are a member of".
GRANT vistara_migrator TO "<admin-login>";

CREATE DATABASE vistara OWNER vistara_migrator;
REVOKE ALL ON DATABASE vistara FROM PUBLIC;
GRANT CONNECT ON DATABASE vistara
  TO vistara_migrator, vistara_api_runtime, vistara_worker;
```

**[Verified]** the two membership requirements are PostgreSQL's own: "To create
a database owned by another role, you must be able to `SET ROLE` to that role"
(<https://www.postgresql.org/docs/18/sql-createdatabase.html>) and "you can
change your own default privileges and the defaults of roles that you are a
member of" (<https://www.postgresql.org/docs/18/sql-alterdefaultprivileges.html>).

**Step 2 — still as the admin login, now connected to `vistara`.** The admin
is a member of `azure_pg_admin`, which owns `public`, so these succeed here and
would fail as `vistara_migrator`.

```sql
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO vistara_api_runtime, vistara_worker;

-- Required on Azure: public is owned by azure_pg_admin, not by the database
-- owner, so the migrator held CREATE only through PUBLIC. The revoke above
-- would otherwise leave it unable to create any table, and migrations fail.
GRANT CREATE ON SCHEMA public TO vistara_migrator;

ALTER DEFAULT PRIVILEGES FOR ROLE vistara_migrator IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES
  TO vistara_api_runtime, vistara_worker;
ALTER DEFAULT PRIVILEGES FOR ROLE vistara_migrator IN SCHEMA public
  GRANT USAGE, SELECT, UPDATE ON SEQUENCES
  TO vistara_api_runtime, vistara_worker;
```

**[Inferred]** Keep the `GRANT vistara_migrator TO "<admin-login>"` membership
in place. Revoking it would block any future `ALTER DEFAULT PRIVILEGES FOR ROLE
vistara_migrator`, which you need whenever the runtime role set changes. The
membership does not give the application anything: the app connects as
`vistara_api_runtime` / `vistara_worker_runtime`, never as the admin.

**[Verified]** Also note the PostgreSQL 16 change Azure calls out: "users with
the `CREATEROLE` attribute no longer have the ability to hand out membership in
any role to anyone. Instead ... they can only hand out memberships in roles for
which they possess `ADMIN OPTION`." Because the admin login **creates** all four
roles above, it holds admin option on them and the `GRANT` succeeds. Creating
the roles some other way may not behave the same.

**[Verified]** Trying to work around ownership by granting into the managed
role fails by design: `GRANT <db_user> TO azure_pg_admin;` returns
`ERROR: permission denied to alter restricted role "azure_pg_admin"`.

### 5.5 Container Apps environment

```bash
az provider register -n Microsoft.App --wait
az provider register -n Microsoft.OperationalInsights --wait

az containerapp env create \
  --name "$ACAENV" --resource-group "$RG" --location "$LOC"
```

— <https://learn.microsoft.com/en-us/cli/azure/containerapp/env>,
<https://learn.microsoft.com/en-us/cli/azure/provider>

**[Inferred]** Omitting `--logs-workspace-id` lets Azure create a Log Analytics
workspace. Log Analytics ingestion is billed separately from the Container Apps
free grant and is a common surprise on a "free" subscription; pass
`--logs-destination none` if you do not want it, and re-check your budget after
the first day.

### 5.6 Identity, secrets, and registry credentials

These steps live in the companion guide:

- [Managed identity and blob RBAC](azure-identity-and-secrets.md#2-managed-identity-and-blob-rbac)
  — create one **user-assigned** identity per role, capture each `clientId`,
  and make the **two** role assignments Vistara needs, one of which is only
  discovered at runtime if you miss it.
- [Key Vault and secret hygiene](azure-identity-and-secrets.md#4-key-vault-and-secret-hygiene)
  — create the vault, grant yourself **Key Vault Secrets Officer** so
  `secret set` works, write the pepper and database passwords, then grant both
  identities **Key Vault Secrets User**.
- [Private GHCR registry credentials](azure-identity-and-secrets.md#5-private-ghcr-registry-credentials)
  — public packages need no credentials at all; private ones should be
  referenced by secret name, never pasted on a command line.

Create the two user-assigned identities, the Key Vault, and its secrets now,
and grant every role while you are there. A user-assigned identity is a
standalone resource, so it and its role assignments exist before
[§8](#8-migrations-deployment-and-validation) creates the apps — which is why
that section attaches the identities with `--user-assigned` and passes their
client IDs straight into configuration.

The only companion steps that must wait for `az containerapp` to exist are the
two that target an app by name: the Key Vault secret references in
[azure-identity-and-secrets.md §4.4](azure-identity-and-secrets.md#44-reference-the-secrets-once-the-apps-exist)
and the private-registry credentials in
[§5.2](azure-identity-and-secrets.md#52-private-packages-store-the-token-in-key-vault-reference-it-by-name).
[§8.3](#83-deploy-the-worker) sends you back for both.

---

## 6. Mapping Azure outputs to Vistara configuration keys

All keys below were read from this repository's source. ASP.NET Core maps the
`__` separator to configuration section nesting, which is why the Compose files
use exactly these names.

| Vistara configuration key | Value from Azure | Source of truth |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` (already the image default) | `deploy/containers/api.Dockerfile` |
| `DOTNET_ENVIRONMENT` | `Production` (worker image default) | `deploy/containers/worker.Dockerfile` |
| `ASPNETCORE_HTTP_PORTS` | `8080` (image default) → `--target-port 8080` | `deploy/containers/api.Dockerfile` |
| `Persistence__Provider` | `PostgreSql` | `GalleryComposition.cs`, `PlatformComposition.cs`, `WorkerPlatformComposition.cs` |
| `ConnectionStrings__Vistara` | `Host=<server>.postgres.database.azure.com;Port=5432;Database=vistara;Username=vistara_api_runtime;Password=<secret>;SSL Mode=Require;Include Error Detail=false` | `deploy/compose.postgres.yml`; read via `GetConnectionString("Vistara")` |
| `MIGRATION_PROVIDER` | `PostgreSql` (migration image only) | `deploy/containers/migration-entrypoint.sh` |
| `Media__Storage__Provider` | `Azure` | `MediaComposition.cs` (`MediaStorageProvider`) |
| `Media__Storage__Azure__AccountName` | `$STORAGE` | `MediaComposition.cs` |
| `Media__Storage__Azure__ContainerName` | `$CONTAINER` | `MediaComposition.cs` |
| `Media__Storage__Azure__ServiceUri` | `https://$STORAGE.blob.core.windows.net` | `AzureBlobStoreOptions.cs` trusted-suffix check |
| `Media__Storage__Azure__CredentialMode` | `ManagedIdentity` (supported deployment path) or `SharedKey` (fallback) | `MediaComposition.cs` (`MediaAzureCredentialMode`) |
| `Media__Storage__Azure__ManagedIdentityClientId` | `clientId` of the app's user-assigned identity; **required** with `ManagedIdentity`, rejected otherwise | `MediaOptionsValidator.ValidateAzure` |
| `Media__Storage__Azure__ConnectionString` | **only** with `SharedKey` | `MediaOptionsValidator.ValidateAzure` |
| `Media__Storage__Azure__AllowSharedKeySas` | `true` **only** with `SharedKey` | `MediaOptionsValidator.ValidateAzure` |
| `Media__Storage__Azure__AllowDefaultCredentialOutsideDevelopment` | leave unset; reviewed exception that keeps `DefaultCredential` usable outside a `Development` environment | `MediaOptionsValidator.ValidateAzure` |
| `Media__Storage__Azure__MaximumGrantLifetime` | optional; must be > 0 and ≤ 7 days | `AzureBlobStoreOptions.Validate` |
| `Media__Imaging__Provider` | `NetVips` (the only accepted value) | `MediaOptionsValidator` |
| `Security__Hosts__AllowedHosts__0` | your container app FQDN | `SecurityComposition.cs` |
| `Security__Transport__RedirectHttpToHttps` | `false` (Container Apps ingress terminates TLS) | `SecurityComposition.cs`; mirrors `deploy/compose.postgres.yml` |
| `Security__Proxy__ForwardLimit` | `1` | `SecurityComposition.cs` (valid range 1–10) |
| `Security__Proxy__KnownProxies__N` / `KnownNetworks__N` | leave unset on Consumption; see [§13](#13-configuration-gaps-that-need-code-changes) | `SecurityComposition.ValidateProxy` |
| `Security__RequiredSecretKeys__N` | startup tripwire for required secrets | `SecurityComposition.cs` |
| `Platform__Authentication__ApiKeys__CurrentPepperVersion` | `v1` | `PlatformOptions.cs` |
| `Platform__Authentication__ApiKeys__Peppers__v1` | Key Vault secret | `PlatformOptions.cs` |
| `Platform__Authentication__Jwt__Issuers__0__ProfileId` | e.g. `entra` | `PlatformOptions.cs` |
| `Platform__Authentication__Jwt__Issuers__0__Issuer` | `https://login.microsoftonline.com/<tenant-id>/v2.0` | `PlatformOptions.cs` |
| `Platform__Authentication__Jwt__Issuers__0__Audience` | your app registration's audience | `PlatformOptions.cs` |
| `Platform__Authentication__Jwt__Issuers__0__MetadataAddress` | `https://login.microsoftonline.com/<tenant-id>/v2.0/.well-known/openid-configuration` | `PlatformOptions.cs` |
| `Platform__Authentication__Jwt__Issuers__0__AllowedAlgorithms__0` | `RS256` | `deploy/compose.postgres.yml` |
| `Worker__InstanceId` | e.g. `azure-worker` | `WorkerPlatformComposition.cs` |
| `Worker__Jobs__MaximumConcurrency` | `1` | `deploy/compose.postgres.yml` |
| `Worker__ImagingLimits__MaximumConcurrentTransforms` | `1` | `deploy/compose.postgres.yml` |
| `Worker__ImagingLimits__ScratchDirectory` | `/var/lib/vistara/scratch` (created in the worker image) | `deploy/containers/worker.Dockerfile` |

**[Verified]** `PlatformOptionsValidator` fails startup without "a valid API
key pepper and current pepper version" and "at least one valid, explicitly
configured JWT issuer" — using Microsoft Entra ID as that issuer keeps
everything in one tenant.
— <https://learn.microsoft.com/en-us/entra/identity-platform/v2-protocols-oidc>

**[Verified]** `MediaOptionsValidator` requires that **exactly one** storage
provider section be configured and that it match `Media__Storage__Provider`.
Do not leave `Media__Storage__S3__*` or `Media__Storage__Local__RootPath` set
alongside the Azure section — the API will refuse to start.

---

## 7. Copy-paste `.env` template

Placeholders only. **Never commit a filled-in copy**; `deploy/.env` is already
git-ignored, and `deploy/generate-env.sh` writes with mode `0600`. This file is
for driving the CLI and for `--env-vars` values; put every value marked
`<secret>` into Key Vault and reference it as a Container Apps secret rather
than inlining it.

```dotenv
# ---------------------------------------------------------------------------
# Azure resource identifiers (not secret)
# ---------------------------------------------------------------------------
AZURE_LOCATION=<azure-region>
AZURE_RESOURCE_GROUP=<resource-group-name>
AZURE_STORAGE_ACCOUNT=<3-24-lowercase-alphanumeric>
AZURE_BLOB_CONTAINER=<3-63-lowercase-with-single-hyphens>
AZURE_POSTGRES_SERVER=<postgres-flexible-server-name>
AZURE_POSTGRES_DATABASE=vistara
AZURE_KEY_VAULT=<key-vault-name>
AZURE_CONTAINERAPPS_ENV=<container-apps-environment-name>
AZURE_API_IDENTITY=<user-assigned-identity-name-for-the-api>
AZURE_WORKER_IDENTITY=<user-assigned-identity-name-for-the-worker>
AZURE_TENANT_ID=<entra-tenant-id>

# ---------------------------------------------------------------------------
# Vistara runtime configuration (API and worker)
# ---------------------------------------------------------------------------
ASPNETCORE_ENVIRONMENT=Production
DOTNET_ENVIRONMENT=Production

Persistence__Provider=PostgreSql
ConnectionStrings__Vistara=Host=<postgres-flexible-server-name>.postgres.database.azure.com;Port=5432;Database=vistara;Username=<runtime-role>;Password=<secret-from-key-vault>;SSL Mode=Require;Include Error Detail=false

Media__Storage__Provider=Azure
Media__Storage__Azure__AccountName=<3-24-lowercase-alphanumeric>
Media__Storage__Azure__ContainerName=<3-63-lowercase-with-single-hyphens>
Media__Storage__Azure__ServiceUri=https://<3-24-lowercase-alphanumeric>.blob.core.windows.net
Media__Storage__Azure__CredentialMode=ManagedIdentity
Media__Storage__Azure__ManagedIdentityClientId=<user-assigned-identity-client-id>
Media__Imaging__Provider=NetVips

Security__Hosts__AllowedHosts__0=<container-app-fqdn>
Security__Transport__RedirectHttpToHttps=false
Security__Proxy__ForwardLimit=1
Security__RequiredSecretKeys__0=Platform__Authentication__ApiKeys__Peppers__v1

Platform__Authentication__ApiKeys__CurrentPepperVersion=v1
Platform__Authentication__ApiKeys__Peppers__v1=<secret-from-key-vault>
Platform__Authentication__Jwt__Issuers__0__ProfileId=entra
Platform__Authentication__Jwt__Issuers__0__Issuer=https://login.microsoftonline.com/<entra-tenant-id>/v2.0
Platform__Authentication__Jwt__Issuers__0__Audience=<api-audience>
Platform__Authentication__Jwt__Issuers__0__MetadataAddress=https://login.microsoftonline.com/<entra-tenant-id>/v2.0/.well-known/openid-configuration
Platform__Authentication__Jwt__Issuers__0__AllowedAlgorithms__0=RS256

# Worker only
Worker__InstanceId=azure-worker
Worker__Jobs__MaximumConcurrency=1
Worker__ImagingLimits__MaximumConcurrentTransforms=1
Worker__ImagingLimits__ScratchDirectory=/var/lib/vistara/scratch

# Migration job only
MIGRATION_PROVIDER=PostgreSql
```

---

## 8. Migrations, deployment, and validation

### 8.1 Run migrations first

**[Verified from `deploy/README.md`]** "The migration container must complete
successfully before API or worker start." Model it as a manually triggered
Container Apps job.

```bash
az containerapp job create \
  --name "$MIGJOB" --resource-group "$RG" --environment "$ACAENV" \
  --trigger-type Manual \
  --replica-timeout 900 --replica-retry-limit 0 \
  --cpu 0.5 --memory 1.0Gi \
  --image "<registry>/vistara-migrations:$IMAGE_TAG" \
  --secrets "migrator-connection=<secret>" \
  --env-vars "MIGRATION_PROVIDER=PostgreSql" \
             "ConnectionStrings__Vistara=secretref:migrator-connection"

az containerapp job start --name "$MIGJOB" --resource-group "$RG"
az containerapp job execution list --name "$MIGJOB" --resource-group "$RG" -o table
```

— <https://learn.microsoft.com/en-us/cli/azure/containerapp/job>,
<https://learn.microsoft.com/en-us/cli/azure/containerapp/job/execution>

**[Verified from `.github/workflows/release-images.yml`]** the migration image
**is** published: the release workflow pushes
`ghcr.io/<namespace>/vistara-migrations` alongside the API and worker images,
each with a provenance attestation. Pull it at the release tag like the other
two; building `deploy/containers/migration.Dockerfile` yourself is only needed
for an unreleased commit.

**[Verified from `migration-entrypoint.sh`]** the entrypoint requires
`ConnectionStrings__Vistara` (or `Persistence__ConnectionString`) and a
`MIGRATION_PROVIDER` of `Sqlite` or `PostgreSql`, exiting `64` otherwise. Use
the **`vistara_migrator`** role here and the runtime roles everywhere else.
Setting `MIGRATION_MANAGED_IDENTITY_CLIENT_ID` switches the same entrypoint to
Entra mode, where it builds the connection string from discrete, individually
validated `MIGRATION_POSTGRES_*` variables and refuses any connection string at
all; that is the mode the hosted bootstrap uses.

### 8.2 Deploy the API

```bash
az containerapp create \
  --name "$APIAPP" --resource-group "$RG" --environment "$ACAENV" \
  --image "ghcr.io/$GHCR_NS/vistara-api:$IMAGE_TAG" \
  --target-port 8080 --ingress external --transport auto \
  --cpu 0.5 --memory 1.0Gi \
  --min-replicas 0 --max-replicas 1 \
  --user-assigned "$APIMI_ID" \
  --secrets "api-db-connection=<secret>" "api-pepper=<secret>" \
  --env-vars "Persistence__Provider=PostgreSql" \
             "ConnectionStrings__Vistara=secretref:api-db-connection" \
             "Media__Storage__Provider=Azure" \
             "Media__Storage__Azure__AccountName=$STORAGE" \
             "Media__Storage__Azure__ContainerName=$CONTAINER" \
             "Media__Storage__Azure__ServiceUri=https://$STORAGE.blob.core.windows.net" \
             "Media__Storage__Azure__CredentialMode=ManagedIdentity" \
             "Media__Storage__Azure__ManagedIdentityClientId=$APIMI_CLIENT" \
             "Media__Imaging__Provider=NetVips" \
             "Security__Transport__RedirectHttpToHttps=false" \
             "Platform__Authentication__ApiKeys__CurrentPepperVersion=v1" \
             "Platform__Authentication__ApiKeys__Peppers__v1=secretref:api-pepper"
```

**[Verified]** `--ingress` accepts only `external` or `internal`,
`--transport` accepts `auto`, `http`, `http2`, or `tcp`, and `--user-assigned`
takes "Space-separated user identities to be assigned" — the identity
**resource IDs**, not their client IDs.
— <https://learn.microsoft.com/en-us/cli/azure/containerapp>

**[Verified from `MediaComposition.cs`]** the app needs both halves of the
identity: `--user-assigned` attaches it to the container app, and
`Media__Storage__Azure__ManagedIdentityClientId` tells Vistara which identity
to request tokens for. With the identity attached but the client ID missing,
startup fails validation rather than falling back to any other identity.

**[Verified]** `--min-replicas 0` with the default HTTP scale rule means "You
aren't billed usage charges if your container app scales to zero." The
documented default scale rule is HTTP with min 0 / max 10.
— <https://learn.microsoft.com/en-us/azure/container-apps/scale-app>

Set the allowed host to the FQDN Azure assigned:

```bash
FQDN="$(az containerapp show --name "$APIAPP" --resource-group "$RG" \
  --query properties.configuration.ingress.fqdn -o tsv)"
az containerapp update --name "$APIAPP" --resource-group "$RG" \
  --set-env-vars "Security__Hosts__AllowedHosts__0=$FQDN"
```

**[Verified from `SecurityComposition.cs`]** `Security__Hosts__AllowedHosts`
must contain at least one exact DNS name or IP address, entries must be
unique, and configured values replace rather than append to the defaults.
Requests with any other `Host` header are rejected by host filtering.

### 8.3 Deploy the worker

Omit `--ingress` entirely: **[Verified]** it accepts only `external` or
`internal`, and a container app created without it has ingress disabled
(`az containerapp ingress disable` turns it off later).

```bash
az containerapp create \
  --name "$WORKERAPP" --resource-group "$RG" --environment "$ACAENV" \
  --image "ghcr.io/$GHCR_NS/vistara-worker:$IMAGE_TAG" \
  --cpu 0.5 --memory 1.0Gi \
  --min-replicas 1 --max-replicas 1 \
  --user-assigned "$WORKERMI_ID" \
  --secrets "worker-db-connection=<secret>" \
  --env-vars "Persistence__Provider=PostgreSql" \
             "ConnectionStrings__Vistara=secretref:worker-db-connection" \
             "Media__Storage__Provider=Azure" \
             "Media__Storage__Azure__AccountName=$STORAGE" \
             "Media__Storage__Azure__ContainerName=$CONTAINER" \
             "Media__Storage__Azure__ServiceUri=https://$STORAGE.blob.core.windows.net" \
             "Media__Storage__Azure__CredentialMode=ManagedIdentity" \
             "Media__Storage__Azure__ManagedIdentityClientId=$WORKERMI_CLIENT" \
             "Media__Imaging__Provider=NetVips" \
             "Worker__InstanceId=azure-worker" \
             "Worker__Jobs__MaximumConcurrency=1" \
             "Worker__ImagingLimits__MaximumConcurrentTransforms=1" \
             "Worker__ImagingLimits__ScratchDirectory=/var/lib/vistara/scratch"
```

> **[Verified]** The worker must not be left at `--min-replicas 0`. Microsoft
> is explicit: "Make sure you create a scale rule or set minReplicas to 1 or
> more if you don't enable ingress. **If ingress is disabled and you don't
> define a minReplicas or a custom scale rule, your container app scales to
> zero and has no way of starting back up.**"
> — <https://learn.microsoft.com/en-us/azure/container-apps/scale-app>

**[Inferred]** So the worker is `--min-replicas 1` while you are testing, and
you scale it to `0` deliberately when you stop ([§9](#9-stop-start-and-actually-bound-the-bill)),
accepting that it will not restart on its own. At 0.5 vCPU / 1.0 GiB a
continuously running worker consumes the entire ≈ 100-hour free grant in about
four days.

Both identities already hold their blob and Key Vault roles from
[azure-identity-and-secrets.md §2](azure-identity-and-secrets.md#2-managed-identity-and-blob-rbac)
and [§4.3](azure-identity-and-secrets.md#43-grant-the-identities-read-access),
so nothing here waits on permission propagation. Now that the apps exist, go
back for the two app-scoped steps: the Key Vault secret references in
[§4.4](azure-identity-and-secrets.md#44-reference-the-secrets-once-the-apps-exist)
and, for private images, the registry credentials in
[§5.2](azure-identity-and-secrets.md#52-private-packages-store-the-token-in-key-vault-reference-it-by-name).

### 8.4 Validate

```bash
curl -i "https://$FQDN/health/live"     # expect 204 No Content
curl -i "https://$FQDN/health/ready"

az containerapp logs show --name "$APIAPP" --resource-group "$RG" --tail 100
az containerapp logs show --name "$WORKERAPP" --resource-group "$RG" --tail 100
```

**[Verified from `src/Vistara.Api/Composition/Platform/PlatformComposition.cs`
and `src/Vistara.Api/Health/ApiHealth.cs`]** `/health/live` returns
`204 No Content`; `/health/ready` is the readiness probe. The Compose
healthchecks in `deploy/compose.postgres.yml` assert exactly this.

Startup failures are informative because the options validators run with
`ValidateOnStart()`:

| Startup error contains | Fix |
|---|---|
| "Exactly one media storage provider section must be configured" | Remove leftover `Media__Storage__S3__*` / `Local__RootPath` values |
| "The Azure Blob service endpoint is invalid" | `ServiceUri` must be `https://<account>.blob.core.windows.net`, no path or query |
| "Azure managed-identity mode requires an explicit user-assigned client ID" | Set `Media__Storage__Azure__ManagedIdentityClientId` to the identity's `clientId` |
| "The Azure user-assigned managed identity client ID must be a non-empty hyphenated GUID" | Pass the `clientId` unquoted and unbraced, not the `principalId` or the resource ID |
| "Azure default credentials are limited to local development" | Use `CredentialMode=ManagedIdentity` in every deployed environment; `DefaultCredential` needs `ASPNETCORE_ENVIRONMENT=Development` or the reviewed `AllowDefaultCredentialOutsideDevelopment=true` |
| "Azure shared-key settings cannot be combined with managed-identity credentials" | Drop `ConnectionString` / `AllowSharedKeySas` when using `ManagedIdentity` |
| "Azure identity credentials are limited to a first-party Azure Blob endpoint" | `ServiceUri` must be `https://$STORAGE.blob.core.windows.net` for the configured account |
| "A valid API key pepper and current pepper version are required" | Set `Peppers__v1` and `CurrentPepperVersion` |
| "At least one valid, explicitly configured JWT issuer is required" | Set all four `Jwt__Issuers__0__*` keys |
| "Required secret configuration '<key>' is missing" | A `Security__RequiredSecretKeys__N` key has no value |
| "Gallery sharing requires the configured Vistara persistence provider" | `Persistence__Provider` or `ConnectionStrings__Vistara` is unset |

Authorization errors that appear only when a share link or direct upload is
requested — rather than at startup — usually mean the missing
`Storage Blob Delegator` assignment described in
[azure-identity-and-secrets.md §2.2](azure-identity-and-secrets.md#22-grant-two-roles-not-one).

**[Inferred]** If uploads work from the API but fail from the browser, check
storage CORS. **[Verified]** `az storage cors add` has **no `--auth-mode`
parameter**, so it needs an account key, connection string, or SAS — which
conflicts with `--allow-shared-key-access false`. Configure CORS from the
portal's **Resource sharing (CORS)** blade in that case.
— <https://learn.microsoft.com/en-us/cli/azure/storage/cors>

---

## 9. Stop, start, and actually bound the bill

**[Verified]** PostgreSQL Flexible Server is billed "for each full hour that
your server exists regardless of whether the server was active for the full
hour ... If you create a server and delete it after five minutes, you are
charged for one full hour." But: "While your server is stopped, you will only
be billed for the storage you have provisioned and any backup storage ...
While your server is stopped, you will not be billed for compute." Backup
storage is free up to 100% of provisioned storage; beyond that it is billed per
GiB-month, and "Standard networking charges apply for network egress."
— <https://azure.microsoft.com/en-us/pricing/details/postgresql/flexible-server/>

```bash
# End of a work session.
az containerapp update --name "$WORKERAPP" --resource-group "$RG" --min-replicas 0
az containerapp update --name "$APIAPP"    --resource-group "$RG" --min-replicas 0
az postgres flexible-server stop  --resource-group "$RG" --name "$PG"

# Start of the next one.
az postgres flexible-server start --resource-group "$RG" --name "$PG"
az containerapp update --name "$WORKERAPP" --resource-group "$RG" --min-replicas 1
```

— <https://learn.microsoft.com/en-us/cli/azure/postgres/flexible-server>

**[Inferred]** Make this a habit, not an intention. Create the database server
once and stop it; do not delete and recreate it, because you are charged a full
hour every time it exists. Restoring the worker to `--min-replicas 1` is part
of starting up again — it will not wake by itself.

---

## 10. Backup

`docs/operations/backup-and-restore.md` is the authority for what a Vistara
backup must contain and how to restore it, and its tooling
(`pg_dump`/`pg_restore`/`psql`) works unchanged against Flexible Server —
connect with `PGHOST=<server>.postgres.database.azure.com`,
`PGSSLMODE=require`, and the `vistara_migrator` role. Azure's own backups are a
complement, not a replacement:

- **[Verified]** Backup retention is 7–35 days and is changeable after
  creation; backup **redundancy** is not.
  — <https://learn.microsoft.com/en-us/azure/postgresql/configure-maintain/quickstart-create-server>
- **[Verified]** Blob soft delete, container soft delete, and versioning
  ([§5.3](#53-storage-account-and-media-container)) are Microsoft's
  recommended blob-side protection; they are not a substitute for an
  off-Azure copy of the media container.

**[Inferred]** Free-account credits make it tempting to skip the restore drill.
Do not: if the subscription is disabled at the 30-day cliff you lose access to
the database and the blobs at the same moment.

---

## 11. Teardown

First remove the identity role assignments, using the guarded, re-derived
script in
[azure-identity-and-secrets.md §6](azure-identity-and-secrets.md#6-removing-identity-and-role-assignments).
It must run **while the container apps still exist**, because it resolves their
principal IDs.

Then delete the group:

```bash
az group delete --name "$RG" --yes --no-wait
```

**[Inferred]** These `az group delete` flags are standard CLI usage rather than
a quoted example; see <https://learn.microsoft.com/en-us/cli/azure/group>.
Deleting the group removes the resources inside it, but any role assignment
scoped **outside** it survives and must be deleted separately.

Also delete the workstation and `allow-azure-services` firewall rules if you
are keeping the server, and revoke the GHCR token if you created one.

**[Verified]** For a total wind-down, "If you don't intend to use any Azure
service, you can cancel your subscription."
— <https://learn.microsoft.com/en-us/azure/cost-management-billing/manage/cancel-azure-subscription>

---

## 12. Cost traps checklist

A short recall list. Each item is stated in full, with its citation, in the
section linked beside it.

| Trap | Detail |
|---|---|
| Budgets never stop spend | [§2](#2-budgets-and-alerts-they-do-not-cap-spend) |
| 48-hour budget delay; 8–24 h cost data; 1–2 day free-usage lag | [§1.1](#11-the-azure-free-account), [§2](#2-budgets-and-alerts-they-do-not-cap-spend) |
| The 30-day credit cliff and the 12-month, once-per-customer limit | [§1.1](#11-the-azure-free-account) |
| Free monthly quantities do not roll over | [§1.1](#11-the-azure-free-account) |
| No always-on replica fits the Container Apps free grant | [§3.2](#32-azure-container-apps-the-recommended-target) |
| Log Analytics ingestion is billed outside that grant | [§5.5](#55-container-apps-environment) |
| PostgreSQL is billed per full hour of **existence** | [§9](#9-stop-start-and-actually-bound-the-bill) |
| Storage type, backup redundancy, networking mode, and encryption key are immutable | [§5.4](#54-postgresql-flexible-server) |
| `0.0.0.0` firewall rules admit every Azure tenant | [§5.4](#54-postgresql-flexible-server) |
| Disabling shared key access breaks service/account SAS and `az storage cors add` | [§5.3](#53-storage-account-and-media-container), [§8.4](#84-validate) |
| Missing `Storage Blob Delegator` fails at SAS time, not startup | [azure-identity-and-secrets.md §2.2](azure-identity-and-secrets.md#22-grant-two-roles-not-one) |
| A no-ingress worker at `minReplicas 0` can never restart | [§8.3](#83-deploy-the-worker) |
| Egress is billable | [§9](#9-stop-start-and-actually-bound-the-bill) |
| Managed identities break across tenant or subscription moves | [azure-identity-and-secrets.md §2.2](azure-identity-and-secrets.md#22-grant-two-roles-not-one) |
| `az consumption budget` is preview and cannot attach recipients | [§2](#2-budgets-and-alerts-they-do-not-cap-spend) |
| Dev/Test credits are not for production; free trials get no quota increases | [§1.2](#12-visual-studio-subscription-azure-devtest-individual-credit), [§1.1](#11-the-azure-free-account) |

---

## 13. Configuration gaps that need code changes

Limitations of the current codebase, not of Azure. Recorded here so the guide
does not document a capability that does not exist.

1. **Entra (passwordless) PostgreSQL authentication now exists, but not on this
   manual path.** **[Verified from
   `src/Vistara.Persistence/Azure/PersistenceAzureOptions.cs` and
   `VistaraNpgsqlDataSourceProvider.cs`]** setting
   `Persistence__Azure__EntraTokenEnabled=true` with a user-assigned
   `ManagedIdentityClientId` and a token scope builds an `NpgsqlDataSource`
   with a periodic password provider, so tokens rotate without a process
   restart, and `deploy/containers/migration-entrypoint.sh` acquires its own
   token from discrete variables. Microsoft's guidance is that ".NET ... can
   get an access token for the managed identity ... Then you can use the access
   token as the password"
   (<https://learn.microsoft.com/en-us/azure/service-connector/how-to-integrate-postgres>).
   [`./deploy/azure/up.sh`](azure-hosted-bootstrap.md) uses exactly that and
   creates a server with `passwordAuth: Disabled`. The imperative path in
   [§5.4](#54-postgresql-flexible-server) does **not** wire it up, so it still
   needs password authentication and real secrets in Key Vault.
2. **No configuration key for `AzureBlobStoreOptions.AllowedEndpointOrigins`.**
   **[Verified]** the adapter supports an endpoint allowlist, but
   `MediaAzureOptions` in `MediaComposition.cs` does not expose it. Only the
   built-in trusted Azure and private-link suffixes are reachable from
   configuration. That is fine for this guide and blocks Azurite-over-network
   or custom-endpoint scenarios.
3. **No safe way to trust Container Apps' ingress forwarded headers.**
   **[Verified]** `Security__Proxy__KnownProxies` requires literal IP addresses
   and `KnownNetworks` requires CIDR blocks, but **[Verified]** Container Apps
   states "Outbound IPs might change over time" and does not publish a stable
   ingress source range for Consumption-only environments. With neither set,
   `UseForwardedHeaders` does not trust `X-Forwarded-For`, so client-IP
   rate-limit partitioning sees the ingress rather than the caller. A
   VNet-integrated environment with a known infrastructure subnet CIDR is the
   current workaround. The hosted bootstrap does not work around it either: it
   declares `Platform__RateLimits__PartitionMode=SharedIngress` so the raised
   hosted ceilings are honest about counting one shared bucket.
4. **The migration image is published.** **[Verified]**
   `.github/workflows/release-images.yml` builds and pushes `vistara-api`,
   `vistara-worker`, **and** `vistara-migrations` to GHCR, each with a
   provenance attestation. Pull `ghcr.io/<namespace>/vistara-migrations` at the
   release tag rather than building
   `deploy/containers/migration.Dockerfile` yourself.
5. **Azure-native deployment assets exist.** `deploy/azure/` holds the `azd`
   project, the Bicep templates, the provisioning hooks, and the role bootstrap
   SQL, and `./deploy/azure/up.sh` is the supported entry point — see
   [Azure hosted bootstrap](azure-hosted-bootstrap.md). Everything in *this*
   guide remains imperative CLI, which is why it is now the fallback rather
   than the recommendation.

---

## 14. Sources

Azure offers, credits, and cost control:

- <https://learn.microsoft.com/en-us/azure/cost-management-billing/manage/avoid-charges-free-account>
- <https://learn.microsoft.com/en-us/azure/cost-management-billing/manage/check-free-service-usage>
- <https://learn.microsoft.com/en-us/azure/cost-management-billing/manage/upgrade-azure-subscription>
- <https://learn.microsoft.com/en-us/azure/cost-management-billing/manage/cancel-azure-subscription>
- <https://learn.microsoft.com/en-us/azure/cost-management-billing/costs/tutorial-acm-create-budgets>
- <https://learn.microsoft.com/en-us/azure/cost-management-billing/costs/cost-mgt-alerts-monitor-usage-spending>
- <https://azure.microsoft.com/en-us/pricing/free-services/>
- <https://learn.microsoft.com/en-us/azure/devtest/offer/quickstart-individual-credit>
- <https://learn.microsoft.com/en-us/startups/microsoft-for-startups/overview>
- <https://learn.microsoft.com/en-us/startups/benefits/azure-credits/use-azure-credits>
- <https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/azure-subscription-service-limits>

Compute:

- <https://learn.microsoft.com/en-us/azure/app-service/overview-hosting-plans>
- <https://learn.microsoft.com/en-us/azure/app-service/quickstart-dotnetcore>
- <https://learn.microsoft.com/en-us/azure/app-service/configure-language-dotnetcore>
- <https://learn.microsoft.com/en-us/azure/container-apps/billing>
- <https://learn.microsoft.com/en-us/azure/container-apps/containers>
- <https://learn.microsoft.com/en-us/azure/container-apps/scale-app>
- <https://learn.microsoft.com/en-us/azure/container-apps/networking>

Data and storage:

- <https://learn.microsoft.com/en-us/azure/postgresql/configure-maintain/quickstart-create-server>
- <https://learn.microsoft.com/en-us/azure/postgresql/security/security-access-control>
- <https://azure.microsoft.com/en-us/pricing/details/postgresql/flexible-server/>
- <https://www.postgresql.org/docs/18/sql-createdatabase.html>
- <https://www.postgresql.org/docs/18/sql-alterdefaultprivileges.html>
- <https://learn.microsoft.com/en-us/azure/storage/common/storage-account-create>
- <https://learn.microsoft.com/en-us/azure/storage/blobs/authorize-data-operations-cli>
- <https://learn.microsoft.com/en-us/azure/service-connector/how-to-integrate-postgres>

CLI reference:

- <https://learn.microsoft.com/en-us/cli/azure/reference-index>
- <https://learn.microsoft.com/en-us/cli/azure/account>
- <https://learn.microsoft.com/en-us/cli/azure/group>
- <https://learn.microsoft.com/en-us/cli/azure/provider>
- <https://learn.microsoft.com/en-us/cli/azure/consumption/budget>
- <https://learn.microsoft.com/en-us/cli/azure/postgres/flexible-server>
- <https://learn.microsoft.com/en-us/cli/azure/postgres/flexible-server/firewall-rule>
- <https://learn.microsoft.com/en-us/cli/azure/storage/cors>
- <https://learn.microsoft.com/en-us/cli/azure/storage/account/blob-service-properties>
- <https://learn.microsoft.com/en-us/cli/azure/containerapp>
- <https://learn.microsoft.com/en-us/cli/azure/containerapp/env>
- <https://learn.microsoft.com/en-us/cli/azure/containerapp/job>
- <https://learn.microsoft.com/en-us/cli/azure/containerapp/job/execution>
- <https://learn.microsoft.com/en-us/entra/identity-platform/v2-protocols-oidc>

Identity, RBAC, and secret sources are listed in
[azure-identity-and-secrets.md](azure-identity-and-secrets.md).

Repository sources of truth: `docs/specification.md`, `deploy/README.md`,
`deploy/compose.postgres.yml`, `deploy/env.example`, `deploy/generate-env.sh`,
`deploy/postgres/init-runtime-roles.sh`, `deploy/containers/*.Dockerfile`,
`deploy/containers/migration-entrypoint.sh`,
`src/Vistara.Api/Composition/**`, `src/Vistara.Storage.Azure/**`,
`src/Vistara.Worker/Composition/**`, `.github/workflows/release-images.yml`.
