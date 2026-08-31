# Running Vistara on Microsoft Azure free credits

This guide shows how a developer can stand up a **non-production evaluation**
instance of Vistara on Microsoft Azure using the current free-credit offers,
and how to map what Azure gives you onto the configuration keys this
repository actually reads.

It is a cost-and-setup runbook, not a production architecture. For the
supported topologies see `deploy/README.md`; for the product and architecture
authority see `docs/specification.md`.

## How to read this document

Every non-obvious claim is labelled:

| Label | Meaning |
|---|---|
| **[Verified]** | Quoted or directly restated from the linked Microsoft page, or read out of this repository's own code and deployment files |
| **[Inferred]** | A reasonable choice or derivation by this guide; individually documented parts, combined here. Confirm before relying on it |
| **[Unverified]** | Deliberately not stated, because no primary Microsoft source could be confirmed. Check the linked page yourself |

Microsoft changes offers, quotas, and CLI surfaces frequently. Treat the
inline dates as the freshness of the research, re-check the linked page before
you spend anything, and prefer `az <group> --help` over this document when the
two disagree.

**Research date for all Microsoft citations below: 2026-08-30.**

**No free quantity is hardcoded in this guide.** The per-service monthly free
amounts (blob GB, PostgreSQL vCore-hours, and so on) are published on a
client-rendered pricing grid that could not be retrieved as primary text.
**[Unverified]** — read the current numbers yourself at
<https://azure.microsoft.com/en-us/pricing/free-services/> and in your own
portal's *Free services for 12 months* table before sizing anything.

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
  upgrade to pay-as-you-go.
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
  after you use a resource."
  — <https://learn.microsoft.com/en-us/azure/cost-management-billing/manage/check-free-service-usage>
- **[Verified]** "Free Azure trial subscriptions aren't eligible for limit or
  quota increases."
  — <https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/azure-subscription-service-limits>

Check what you have consumed in the portal: **Subscriptions** → your
free-account subscription → **Top free services by usage** → **View all free
services**. The same blade's tooltip shows the 12-month expiry date.
**[Verified]**, same article.

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

This is the best fit for a recurring Vistara sandbox: the credit renews
monthly instead of expiring after 30 days.

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
A budget is a tripwire, not a circuit breaker.
— <https://learn.microsoft.com/en-us/azure/cost-management-billing/costs/tutorial-acm-create-budgets>

Also **[Verified]** from the same tutorial and
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

**[Inferred]** Because budgets cannot stop spend, pair them with the real
controls in [§9](#9-stop-start-and-actually-bound-the-bill): stop the database
when idle, and let the container app scale to zero.

---

## 3. Choosing the cheapest architecture that Vistara can actually run on

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

That rules out the obvious "cheapest" option:

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
you one process, while Vistara needs API + worker + a migration run. If you
still want App Service, you need a **custom container** plan tier and one plan
per process, which is no longer free.

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

**[Verified]** The smallest documented Consumption allocation is
**0.25 vCPU / 0.5 GiB**.
— <https://learn.microsoft.com/en-us/azure/container-apps/containers>

**[Inferred] Free-grant arithmetic.** One always-on 0.25 vCPU / 0.5 GiB
replica consumes 0.25 vCPU-s and 0.5 GiB-s per second, so the free grant
covers `180,000 / 0.25 = 720,000` seconds of vCPU and `360,000 / 0.5 = 720,000`
seconds of memory — **about 200 hours per calendar month for one minimal
replica**, both limits binding at the same point. A month is ~730 hours, so
**two always-on minimal replicas (API + worker) will exceed the grant.** Run
the API with `--min-replicas 0` and start the worker only when you need it.

This grant is per subscription per calendar month and is **not** tied to the
12-month free account, so it survives the 30-day and 12-month cliffs.

### 3.3 Comparison

| Option | Fits Vistara? | Cost shape | Main catch |
|---|---|---|---|
| App Service **F1** | **No** | Free | No libvips on code-only deploy; 60 CPU-min/day; one process; no custom domain or TLS **[Verified limits / Inferred fit]** |
| App Service custom container (B1+) | Yes | Hourly per plan, per process | "Except for the Free tier, an App Service plan carries a charge on the compute resources that it uses" **[Verified]** |
| **Container Apps Consumption** | **Yes — recommended** | Free grant, then per vCPU-s / GiB-s; zero when scaled to zero | ~200 minimal-replica hours/month free **[Inferred]**; worker cannot scale to zero on HTTP |
| PostgreSQL Flexible Server, **Burstable B1ms**, no HA, LRS backup | Yes | Billed per full hour the server **exists** | Stop it when idle; several settings are immutable **[Verified]** |
| Blob Storage `StandardV2`, `Standard_LRS` | Yes (native adapter) | Per GB + transactions + egress | Free monthly GB **[Unverified]** — check the grid |
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
export KV="kv-vistara-$SUFFIX"
export ACAENV="cae-vistara-$SUFFIX"
export APIAPP="ca-vistara-api"
export WORKERAPP="ca-vistara-worker"
export MIGJOB="caj-vistara-migrate"
export GHCR_NS="<your-github-namespace>"   # ghcr.io/<ns>/vistara-api
export IMAGE_TAG="<release-tag>"
```

---

## 5. Provisioning with the Azure CLI

Everything below is run from your workstation. Commands are shown with the
parameters confirmed to exist in the current CLI reference; where a value is
this guide's choice rather than a documented example, it is labelled.

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
redundancy value for `StorageV2` (**[Inferred]** substitution for the doc's
`Standard_RAGRS`, chosen because LRS is the cheapest redundancy).
— <https://learn.microsoft.com/en-us/azure/storage/common/storage-account-create>

Data-protection settings Microsoft recommends, and which are worth having even
in a sandbox — **[Verified]** the article recommends blob soft delete and
container soft delete with "a minimum retention period of seven days" and blob
versioning "for optimal data protection":

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

**[Verified]** "When you create an Azure Storage account, you aren't
automatically assigned permissions to access data via Microsoft Entra ID. You
must explicitly assign yourself an Azure role." If the container create fails
with an authorization error, assign yourself **Storage Blob Data Contributor**
first.
— <https://learn.microsoft.com/en-us/azure/storage/blobs/assign-azure-role-data-access>

#### Optional hardening, with a real consequence

```bash
az storage account update --name "$STORAGE" --resource-group "$RG" \
  --allow-shared-key-access false
```

**[Verified]** With shared-key access disabled, "service SAS and account SAS"
are denied; only **user delegation SAS** and Entra-authorized requests
succeed.
— <https://learn.microsoft.com/en-us/azure/storage/common/shared-key-authorization-prevent>

**[Verified from `src/Vistara.Api/Composition/Media/MediaComposition.cs` and
`src/Vistara.Storage.Azure/`]** This is compatible with Vistara's
`CredentialMode: DefaultCredential` path, which sets `SasMode` to
`UserDelegation`. It is **not** compatible with `CredentialMode: SharedKey`,
which requires a connection string and an explicit `AllowSharedKeySas: true`
opt-in. Use `DefaultCredential`; see [§5.5](#55-managed-identity-and-least-privilege-rbac).

### 5.4 PostgreSQL Flexible Server

```bash
az postgres flexible-server create \
  --resource-group "$RG" \
  --name "$PG" \
  --location "$LOC" \
  --tier Burstable \
  --sku-name Standard_B1ms \
  --storage-size 32 \
  --version 18 \
  --high-availability Disabled \
  --backup-retention 7 \
  --geo-redundant-backup Disabled \
  --public-access None \
  --admin-user "<admin-login>" \
  --admin-password "<generated-strong-password>" \
  --yes
```

**[Verified]** Every parameter above appears in the `az postgres
flexible-server create` synopsis; `Standard_B1ms` appears literally in a doc
example; `Burstable` is a documented `--tier` value; PostgreSQL **18** is in
the supported version list; and "Workload type 'Development' uses Burstable
SKUs".
— <https://learn.microsoft.com/en-us/cli/azure/postgres/flexible-server>,
<https://learn.microsoft.com/en-us/azure/postgresql/configure-maintain/quickstart-create-server>

**[Inferred]** The exact pairing of `--tier Burstable` with
`--sku-name Standard_B1ms` is assembled from two separately documented values;
no single official example shows them together. Run
`az postgres flexible-server list-skus --location "$LOC" -o table` to confirm
availability in your region before relying on it.

**[Verified]** Decisions you cannot change after creation: **storage type**,
**backup redundancy / geo-redundancy**, **networking mode (public vs.
private)**, and the **data encryption key**. Storage size can only be
increased, never shrunk. Backup retention (7–35 days) can be changed. HA
Disabled carries a 99.9% SLA and is the right cost choice here.

**[Verified]** Version 18 matches `postgres:18.0-bookworm` in
`deploy/compose.postgres.yml`, so migrations and behaviour match the
repository's reference topology.

Create the database and open the firewall:

```bash
az postgres flexible-server db create \
  --resource-group "$RG" --server-name "$PG" --database-name "$PGDB"

# Your workstation, for migrations and role setup.
MYIP="$(curl -s https://api.ipify.org)"
az postgres flexible-server firewall-rule create \
  --resource-group "$RG" --server-name "$PG" \
  --name allow-workstation \
  --start-ip-address "$MYIP" --end-ip-address "$MYIP"
```

**[Verified]** `--end-ip-address` "Use value '0.0.0.0' to represent all
Azure-internal IP addresses", which is how the "allow Azure services" rule is
expressed:
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
passwords, delete the rule when you are done testing, and prefer a VNet
-integrated environment with private access if you keep the instance around.

#### Create Vistara's least-privilege database roles

**[Verified from `deploy/postgres/init-runtime-roles.sh`]** the Compose
topology creates a schema-owning migrator plus DDL-free API and worker logins.
Azure cannot run that init script, so apply the equivalent by hand. Connect as
the admin login you created above and run:

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

REVOKE ALL ON DATABASE vistara FROM PUBLIC;
GRANT CONNECT ON DATABASE vistara
  TO vistara_migrator, vistara_api_runtime, vistara_worker;
```

Then, connected to the `vistara` database:

```sql
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO vistara_api_runtime, vistara_worker;

ALTER DEFAULT PRIVILEGES FOR ROLE vistara_migrator IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES
  TO vistara_api_runtime, vistara_worker;
ALTER DEFAULT PRIVILEGES FOR ROLE vistara_migrator IN SCHEMA public
  GRANT USAGE, SELECT, UPDATE ON SEQUENCES
  TO vistara_api_runtime, vistara_worker;
```

**[Inferred]** The Compose script creates the database with
`OWNER vistara_migrator`. On Flexible Server the database is created by
`az postgres flexible-server db create` and owned by the admin login, so also
run `ALTER DATABASE vistara OWNER TO vistara_migrator;` (or create the
database with `CREATE DATABASE vistara OWNER vistara_migrator;` instead of the
CLI command) before applying migrations, so that `ALTER DEFAULT PRIVILEGES FOR
ROLE vistara_migrator` covers the tables the migrator actually creates.

### 5.5 Managed identity and least-privilege RBAC

**Ordering note [Inferred]:** a system-assigned identity does not exist until
the container app does. Read this section now, but run its commands **after**
[§8.2](#82-deploy-the-api) and [§8.3](#83-deploy-the-worker) create the apps.
Sections 5.6 and 5.7 are ordered the same way.

**[Verified from `src/Vistara.Api/Composition/Media/MediaComposition.cs`]**
`CreateAzureCredential()` returns `new DefaultAzureCredential()`, so a
system-assigned managed identity on the container app is picked up with no
secret at all. **[Verified]** "The Azure platform manages the identity, so you
don't need to provision or rotate any secrets."
— <https://learn.microsoft.com/en-us/azure/app-service/overview-managed-identity>

Assign the identity, then grant it two roles:

```bash
az containerapp identity assign \
  --name "$APIAPP" --resource-group "$RG" --system-assigned

API_PRINCIPAL="$(az containerapp identity show \
  --name "$APIAPP" --resource-group "$RG" --query principalId -o tsv)"

STORAGE_ID="$(az storage account show \
  --name "$STORAGE" --resource-group "$RG" --query id -o tsv)"

# Data plane, scoped to the single container (least privilege).
az role assignment create \
  --assignee-object-id "$API_PRINCIPAL" --assignee-principal-type ServicePrincipal \
  --role "Storage Blob Data Contributor" \
  --scope "$STORAGE_ID/blobServices/default/containers/$CONTAINER"

# Required so the app can mint user delegation SAS.
az role assignment create \
  --assignee-object-id "$API_PRINCIPAL" --assignee-principal-type ServicePrincipal \
  --role "Storage Blob Delegator" \
  --scope "$STORAGE_ID"
```

**[Verified]** Container-level scope is Microsoft's documented least-privilege
pattern: "By limiting roles and scopes, you limit the resources that are at
risk if the security principal is ever compromised."
— <https://learn.microsoft.com/en-us/azure/storage/blobs/assign-azure-role-data-access>

**[Verified]** The second assignment is not optional for Vistara.
`src/Vistara.Storage.Azure/AzureSdkBlobClient.cs` calls
`GetUserDelegationKeyAsync`, and Microsoft states: "Azure RBAC action:
`Microsoft.Storage/storageAccounts/blobServices/generateUserDelegationKey/action`;
Least privileged built-in role: **Storage Blob Delegator**" and "If the
security principal is assigned a role that permits data access but is scoped to
the level of a container, you can additionally assign the Storage Blob
Delegator role to that security principal at the level of the storage account,
resource group, or subscription."
— <https://learn.microsoft.com/en-us/rest/api/storageservices/get-user-delegation-key>

Repeat the identity assignment and both role assignments for `$WORKERAPP`.

**[Verified]** Creating role assignments requires
`Microsoft.Authorization/roleAssignments/write` (RBAC Administrator or User
Access Administrator).

#### Least-privilege fallback when you cannot create role assignments

If your account cannot write role assignments — common on a tenant you do not
own — Vistara supports a shared-key fallback
(**[Verified from `MediaComposition.cs`]**): set
`Media__Storage__Azure__CredentialMode=SharedKey`, supply
`Media__Storage__Azure__ConnectionString`, and set
`Media__Storage__Azure__AllowSharedKeySas=true`. The validator rejects the
combination unless **both** the connection string and the explicit opt-in are
present, and rejects mixing either with `DefaultCredential`.

**[Inferred]** Treat this as a downgrade of last resort: it reintroduces a
long-lived account key, it is incompatible with
`--allow-shared-key-access false`, and it replaces user-delegation SAS with
shared-key SAS. Rotate the key when you are done
(`az storage account keys renew --account-name "$STORAGE" --resource-group
"$RG" --key primary`).

**[Verified]** Managed identities "don't support cross-directory scenarios":
moving the subscription to another tenant breaks them, and identity
configuration is per deployment slot.

### 5.6 Key Vault and secret hygiene

Vistara still needs real secrets even with managed identity for blobs: the
PostgreSQL passwords and the API key pepper.
**[Verified from `deploy/generate-env.sh`]** the pepper is
`openssl rand -base64 32` and the database passwords are 36 random URL-safe
bytes. Generate them the same way; **do not** reuse the values in
`deploy/env.example`, which are intentionally empty.

```bash
az keyvault create --name "$KV" --resource-group "$RG" --location "$LOC" \
  --enable-rbac-authorization true

az keyvault secret set --vault-name "$KV" --name vistara-api-pepper \
  --value "$(openssl rand -base64 32 | tr -d '\n')"
az keyvault secret set --vault-name "$KV" --name vistara-api-db-password \
  --value "$(openssl rand -base64 36 | tr -d '\n' | tr '+/' '-_')"
az keyvault secret set --vault-name "$KV" --name vistara-worker-db-password \
  --value "$(openssl rand -base64 36 | tr -d '\n' | tr '+/' '-_')"
az keyvault secret set --vault-name "$KV" --name vistara-migrator-db-password \
  --value "$(openssl rand -base64 36 | tr -d '\n' | tr '+/' '-_')"
```

Container Apps can reference a vault secret directly. **[Verified]** the
syntax is
`keyvaultref:<KEY_VAULT_SECRET_URI>,identityref:<MANAGED_IDENTITY_ID>`, and
the identity needs the **Key Vault Secrets User** role.
— <https://learn.microsoft.com/en-us/azure/container-apps/manage-secrets>

**[Inferred]** `--enable-rbac-authorization true` above is required for the
**Key Vault Secrets User** role to mean anything; a vault left in
access-policy mode needs `az keyvault set-policy --secret-permissions get`
instead. Grant the role, then wire the reference:

```bash
KV_ID="$(az keyvault show --name "$KV" --resource-group "$RG" --query id -o tsv)"
az role assignment create \
  --assignee-object-id "$API_PRINCIPAL" --assignee-principal-type ServicePrincipal \
  --role "Key Vault Secrets User" --scope "$KV_ID"
```

```bash
az containerapp secret set \
  --name "$APIAPP" --resource-group "$RG" \
  --secrets "api-db-password=keyvaultref:https://$KV.vault.azure.net/secrets/vistara-api-db-password,identityref:system"
```

**[Inferred]** `identityref:system` for a system-assigned identity — the doc's
worked example uses a user-assigned identity resource ID. If it is rejected,
create a user-assigned identity, grant it **Key Vault Secrets User**, attach
it with `az containerapp identity assign --user-assigned`, and pass its
resource ID.

Hygiene rules for this repository:

- **[Verified]** App settings "are securely encrypted at rest, but if you need
  capabilities for managing secrets, they should go into a key vault."
  — <https://learn.microsoft.com/en-us/azure/app-service/app-service-key-vault-references>
- **[Verified]** App Service Key Vault references without a pinned version
  refresh "within 24 hours"; the cache is refetched every 24 hours and any
  configuration change forces an immediate refetch and app restart.
- **[Verified from `SecurityComposition.cs`]** `Security__RequiredSecretKeys__N`
  makes the API **fail to start** if a named configuration key is missing. Use
  it as a deployment tripwire, for example
  `Security__RequiredSecretKeys__0=Platform__Authentication__ApiKeys__Peppers__v1`.
- Never put a secret in a resource name, a tag, a container image, or this
  repository. `deploy/README.md`: "Do not copy example passwords into a
  deployment."
- **[Verified from `AzureBlobStoreOptions.ToString()`]** the adapter redacts
  the connection string in diagnostics, but the `ConnectionStrings__Vistara`
  value is a plain environment variable — reference it as a Container Apps
  secret, not an inline `--env-vars` value.

### 5.7 Container Apps environment

```bash
az provider register -n Microsoft.App --wait
az provider register -n Microsoft.OperationalInsights --wait

az containerapp env create \
  --name "$ACAENV" --resource-group "$RG" --location "$LOC"
```

— <https://learn.microsoft.com/en-us/cli/azure/containerapp/env>

**[Inferred]** Omitting `--logs-workspace-id` lets Azure create a Log
Analytics workspace. Log Analytics ingestion is billed separately and is a
common surprise on a "free" subscription; pass `--logs-destination none` if
you do not want it, and re-check your budget after the first day.

---

## 6. Mapping Azure outputs to Vistara configuration keys

All keys below were read from this repository's source. ASP.NET Core maps the
`__` separator to configuration section nesting, which is why the Compose
files use exactly these names.

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
| `Media__Storage__Azure__CredentialMode` | `DefaultCredential` (managed identity) or `SharedKey` (fallback) | `MediaComposition.cs` (`MediaAzureCredentialMode`) |
| `Media__Storage__Azure__ConnectionString` | **only** with `SharedKey` | `MediaOptionsValidator.ValidateAzure` |
| `Media__Storage__Azure__AllowSharedKeySas` | `true` **only** with `SharedKey` | `MediaOptionsValidator.ValidateAzure` |
| `Media__Storage__Azure__MaximumGrantLifetime` | optional; must be > 0 and ≤ 7 days | `AzureBlobStoreOptions.Validate` |
| `Media__Imaging__Provider` | `NetVips` (the only accepted value) | `MediaOptionsValidator` |
| `Security__Hosts__AllowedHosts__0` | your container app FQDN | `SecurityComposition.cs` |
| `Security__Transport__RedirectHttpToHttps` | `false` (Container Apps ingress terminates TLS) | `SecurityComposition.cs`; mirrors `deploy/compose.postgres.yml` |
| `Security__Proxy__ForwardLimit` | `1` | `SecurityComposition.cs` (valid range 1–10) |
| `Security__Proxy__KnownProxies__N` / `KnownNetworks__N` | leave unset on Consumption; see the gap in [§13](#13-configuration-gaps-that-need-code-changes) | `SecurityComposition.ValidateProxy` |
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
Media__Storage__Azure__CredentialMode=DefaultCredential
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
  --image "ghcr.io/$GHCR_NS/vistara-migrations:$IMAGE_TAG" \
  --secrets "migrator-connection=<secret>" \
  --env-vars "MIGRATION_PROVIDER=PostgreSql" \
             "ConnectionStrings__Vistara=secretref:migrator-connection"

az containerapp job start --name "$MIGJOB" --resource-group "$RG"
az containerapp job execution list --name "$MIGJOB" --resource-group "$RG" -o table
```

— <https://learn.microsoft.com/en-us/cli/azure/containerapp/job>,
<https://learn.microsoft.com/en-us/cli/azure/containerapp/job/execution>

**[Inferred]** The migration image is built locally by
`deploy/containers/migration.Dockerfile`; unlike the API and worker images it
is **not** published by `.github/workflows/release-images.yml`. You must build
and push it to a registry the environment can pull from before this job will
run. **[Verified from `migration-entrypoint.sh`]** the entrypoint requires
`ConnectionStrings__Vistara` (or `Persistence__ConnectionString`) and a
`MIGRATION_PROVIDER` of `Sqlite` or `PostgreSql`, exiting `64` otherwise.

Use the **migrator** role in the job's connection string and the runtime roles
everywhere else.

### 8.2 Deploy the API

```bash
az containerapp create \
  --name "$APIAPP" --resource-group "$RG" --environment "$ACAENV" \
  --image "ghcr.io/$GHCR_NS/vistara-api:$IMAGE_TAG" \
  --target-port 8080 --ingress external --transport auto \
  --cpu 0.5 --memory 1.0Gi \
  --min-replicas 0 --max-replicas 1 \
  --system-assigned \
  --secrets "api-db-connection=<secret>" "api-pepper=<secret>" \
  --env-vars "Persistence__Provider=PostgreSql" \
             "ConnectionStrings__Vistara=secretref:api-db-connection" \
             "Media__Storage__Provider=Azure" \
             "Media__Storage__Azure__AccountName=$STORAGE" \
             "Media__Storage__Azure__ContainerName=$CONTAINER" \
             "Media__Storage__Azure__ServiceUri=https://$STORAGE.blob.core.windows.net" \
             "Media__Storage__Azure__CredentialMode=DefaultCredential" \
             "Media__Imaging__Provider=NetVips" \
             "Security__Transport__RedirectHttpToHttps=false" \
             "Platform__Authentication__ApiKeys__CurrentPepperVersion=v1" \
             "Platform__Authentication__ApiKeys__Peppers__v1=secretref:api-pepper"
```

Then set the allowed host to the FQDN Azure assigned, and only then grant the
identity its storage roles ([§5.5](#55-managed-identity-and-least-privilege-rbac)):

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

**[Verified]** `--min-replicas 0` with the default HTTP scale rule means "You
aren't billed usage charges if your container app scales to zero."
— <https://learn.microsoft.com/en-us/azure/container-apps/scale-app>

### 8.3 Deploy the worker

Same image family, no ingress, and the worker-specific settings from
[§6](#6-mapping-azure-outputs-to-vistara-configuration-keys):

```bash
az containerapp create \
  --name "$WORKERAPP" --resource-group "$RG" --environment "$ACAENV" \
  --image "ghcr.io/$GHCR_NS/vistara-worker:$IMAGE_TAG" \
  --cpu 0.5 --memory 1.0Gi \
  --min-replicas 0 --max-replicas 1 \
  --system-assigned \
  --secrets "worker-db-connection=<secret>" \
  --env-vars "Persistence__Provider=PostgreSql" \
             "ConnectionStrings__Vistara=secretref:worker-db-connection" \
             "Media__Storage__Provider=Azure" \
             "Media__Storage__Azure__AccountName=$STORAGE" \
             "Media__Storage__Azure__ContainerName=$CONTAINER" \
             "Media__Storage__Azure__ServiceUri=https://$STORAGE.blob.core.windows.net" \
             "Media__Storage__Azure__CredentialMode=DefaultCredential" \
             "Media__Imaging__Provider=NetVips" \
             "Worker__InstanceId=azure-worker" \
             "Worker__Jobs__MaximumConcurrency=1" \
             "Worker__ImagingLimits__MaximumConcurrentTransforms=1" \
             "Worker__ImagingLimits__ScratchDirectory=/var/lib/vistara/scratch"
```

**[Verified]** `--ingress` only accepts `external` or `internal`, so omit it
entirely: a container app created without `--ingress` has ingress disabled
(`az containerapp ingress disable` turns it off later).
— <https://learn.microsoft.com/en-us/cli/azure/containerapp>

**[Inferred]** With no ingress and no scale rule, a worker at
`--min-replicas 0` will never start. Set `--min-replicas 1` while you are
actively testing and scale it back to `0` afterwards; see the free-grant
arithmetic in [§3.2](#32-azure-container-apps-the-recommended-target).

### 8.4 Validate

```bash
# Liveness (204 No Content) and readiness.
curl -i "https://$FQDN/health/live"
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
| "Azure shared-key settings cannot be combined with default credentials" | Drop `ConnectionString` / `AllowSharedKeySas` when using `DefaultCredential` |
| "A valid API key pepper and current pepper version are required" | Set `Peppers__v1` and `CurrentPepperVersion` |
| "At least one valid, explicitly configured JWT issuer is required" | Set all four `Jwt__Issuers__0__*` keys |
| "Required secret configuration '<key>' is missing" | A `Security__RequiredSecretKeys__N` key has no value |
| "Gallery sharing requires the configured Vistara persistence provider" | `Persistence__Provider` or `ConnectionStrings__Vistara` is unset |

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
While your server is stopped, you will not be billed for compute."
— <https://azure.microsoft.com/en-us/pricing/details/postgresql/flexible-server/>

```bash
# End of a work session.
az containerapp update --name "$WORKERAPP" --resource-group "$RG" --min-replicas 0
az containerapp update --name "$APIAPP"    --resource-group "$RG" --min-replicas 0
az postgres flexible-server stop  --resource-group "$RG" --name "$PG"

# Start of the next one.
az postgres flexible-server start --resource-group "$RG" --name "$PG"
```

— <https://learn.microsoft.com/en-us/cli/azure/postgres/flexible-server>

**[Inferred]** Make this a habit, not an intention. Stopping the database and
scaling both apps to zero is the only thing in this guide that reliably
reduces spend; the budget from [§2](#2-budgets-and-alerts-they-do-not-cap-spend)
only tells you afterwards.

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
- **[Verified]** Backup storage is free up to 100% of provisioned storage;
  beyond that it is billed per GiB-month.
- **[Verified]** Blob soft delete, container soft delete, and versioning
  ([§5.3](#53-storage-account-and-media-container)) are Microsoft's
  recommended blob-side protection; they are not a substitute for an
  off-Azure copy of the media container.

**[Inferred]** Free-account credits make it tempting to skip the restore
drill. Do not: if the subscription is disabled at the 30-day cliff you lose
access to both the database and the blobs at the same moment.

---

## 11. Teardown

```bash
# Remove role assignments scoped outside the resource group first.
az role assignment delete \
  --assignee-object-id "$API_PRINCIPAL" \
  --role "Storage Blob Delegator" --scope "$STORAGE_ID"

az group delete --name "$RG" --yes --no-wait
```

**[Inferred]** The `az group delete` flags are standard CLI usage rather than
a quoted example; see <https://learn.microsoft.com/en-us/cli/azure/group>.
Deleting the group removes the resources inside it, but role assignments
scoped **outside** it (subscription or another resource group) survive and
must be deleted separately.

**[Verified]** For a total wind-down, "If you don't intend to use any Azure
service, you can cancel your subscription."
— <https://learn.microsoft.com/en-us/azure/cost-management-billing/manage/cancel-azure-subscription>

Also delete the workstation firewall rule and any `allow-azure-services` rule
before you stop watching the subscription.

---

## 12. Cost and correctness traps

1. **Budgets never stop spend.** **[Verified]** "Resources aren't affected, and
   your consumption isn't stopped."
2. **Cost Management lags.** **[Verified]** New subscriptions may need 48 hours
   before budgets work; cost data lands in 8–24 hours; free-service usage lags
   1–2 days. You can overspend a day before you see it.
3. **The 30-day cliff.** **[Verified]** Credit expires after 30 days and the
   subscription is disabled unless you move to pay-as-you-go — at which point
   real charges begin.
4. **The 12-month free services are once per customer, ever**, and are called
   out as unavailable for pay-as-you-go in China and India. **[Verified]**
5. **Free quantities do not roll over.** **[Verified]**
6. **PostgreSQL is billed per full hour of existence**, even for a server that
   lived five minutes. **[Verified]** Create it once; stop it, do not
   recreate it.
7. **Immutable PostgreSQL choices**: storage type, backup redundancy,
   networking mode, encryption key. Storage can only grow. **[Verified]**
8. **`0.0.0.0` firewall rules allow every Azure tenant**, not just yours.
   **[Verified]** phrasing: "all Azure-internal IP addresses."
9. **Disabling shared key access breaks service and account SAS** and
   `az storage cors add`. **[Verified]**
10. **Missing `Storage Blob Delegator`** produces authorization failures only
    at SAS-minting time, not at startup, because Vistara calls
    `GetUserDelegationKeyAsync` lazily. **[Verified from
    `AzureSdkBlobClient.cs`]**
11. **Log Analytics ingestion is billed separately** from the Container Apps
    free grant. **[Inferred]**
12. **Egress is billable** for PostgreSQL and generally. **[Verified]**
13. **Two always-on minimal Container Apps replicas exceed the free monthly
    grant.** **[Inferred]** — see [§3.2](#32-azure-container-apps-the-recommended-target).
14. **Managed identities break across tenant or subscription moves.**
    **[Verified]**
15. **`az consumption budget` is in preview** and its simple `create` verb
    cannot attach alert recipients. **[Verified]**
16. **Visual Studio Dev/Test credits are explicitly not for production** and
    disappear when the Visual Studio subscription lapses. **[Verified]**
17. **Free-trial subscriptions cannot get quota increases.** **[Verified]**

---

## 13. Configuration gaps that need code changes

These are limitations of the current codebase, not of Azure. They are recorded
here so the guide does not document a capability that does not exist.

1. **No Entra (passwordless) authentication to PostgreSQL.**
   **[Verified]** every persistence path calls `options.UseNpgsql(connectionString)`
   directly (`PersistenceServiceCollectionExtensions.cs`,
   `GalleryComposition.cs`, `WorkerPlatformComposition.cs`,
   `JobPersistenceServiceCollectionExtensions.cs`,
   `TenantDbContextFactory.cs`, `VistaraDbContextFactory.cs`) with no
   `NpgsqlDataSource` password provider. Microsoft's guidance is that ".NET ...
   can get an access token for the managed identity ... Then you can use the
   access token as the password"
   (<https://learn.microsoft.com/en-us/azure/service-connector/how-to-integrate-postgres>),
   and tokens expire, so a periodic password provider is required. **Until
   that exists, Vistara on Azure must use PostgreSQL password
   authentication**, which means `--password-auth Enabled` and real secrets in
   Key Vault. Do not create the server with `--password-auth Disabled`.
2. **No configuration key for `AzureBlobStoreOptions.AllowedEndpointOrigins`.**
   **[Verified]** the adapter supports an endpoint allowlist, but
   `MediaAzureOptions` in `MediaComposition.cs` does not expose it. Only the
   built-in trusted Azure and private-link suffixes are reachable from
   configuration. That is fine for this guide and blocks Azurite-over-network
   or custom-endpoint scenarios.
3. **No safe way to trust Container Apps' ingress forwarded headers.**
   **[Verified]** `Security__Proxy__KnownProxies` requires literal IP
   addresses and `KnownNetworks` requires CIDR blocks, but **[Verified]**
   Container Apps states "Outbound IPs might change over time" and does not
   publish a stable ingress source range for Consumption-only environments.
   With neither set, `UseForwardedHeaders` does not trust `X-Forwarded-For`,
   so client-IP rate-limit partitioning sees the ingress rather than the
   caller. A VNet-integrated environment with a known infrastructure subnet
   CIDR is the current workaround.
4. **The migration image is not published.**
   **[Verified]** `.github/workflows/release-images.yml` pushes only
   `vistara-api` and `vistara-worker` to GHCR, while `deploy/README.md`
   requires the migration container to run first. Any registry-based
   deployment must build and push `deploy/containers/migration.Dockerfile`
   itself.
5. **No Azure-native deployment assets in this repository.** There is no
   Bicep, ARM, `azd`, or Container Apps YAML under `deploy/`; the supported
   artifacts are Compose topologies. Everything in this guide is imperative
   CLI, and should be replaced by checked-in infrastructure-as-code before it
   is used for anything beyond evaluation.

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
- <https://learn.microsoft.com/en-us/azure/app-service/overview-managed-identity>
- <https://learn.microsoft.com/en-us/azure/app-service/app-service-key-vault-references>
- <https://learn.microsoft.com/en-us/azure/container-apps/billing>
- <https://learn.microsoft.com/en-us/azure/container-apps/containers>
- <https://learn.microsoft.com/en-us/azure/container-apps/scale-app>
- <https://learn.microsoft.com/en-us/azure/container-apps/networking>
- <https://learn.microsoft.com/en-us/azure/container-apps/manage-secrets>

Data and storage:

- <https://learn.microsoft.com/en-us/azure/postgresql/configure-maintain/quickstart-create-server>
- <https://learn.microsoft.com/en-us/azure/postgresql/security/security-entra-configure>
- <https://azure.microsoft.com/en-us/pricing/details/postgresql/flexible-server/>
- <https://learn.microsoft.com/en-us/azure/storage/common/storage-account-create>
- <https://learn.microsoft.com/en-us/azure/storage/common/shared-key-authorization-prevent>
- <https://learn.microsoft.com/en-us/azure/storage/blobs/authorize-data-operations-cli>
- <https://learn.microsoft.com/en-us/azure/storage/blobs/assign-azure-role-data-access>
- <https://learn.microsoft.com/en-us/rest/api/storageservices/get-user-delegation-key>
- <https://learn.microsoft.com/en-us/azure/service-connector/how-to-integrate-postgres>

CLI reference:

- <https://learn.microsoft.com/en-us/cli/azure/reference-index>
- <https://learn.microsoft.com/en-us/cli/azure/account>
- <https://learn.microsoft.com/en-us/cli/azure/group>
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

Repository sources of truth: `docs/specification.md`, `deploy/README.md`,
`deploy/compose.postgres.yml`, `deploy/env.example`, `deploy/generate-env.sh`,
`deploy/postgres/init-runtime-roles.sh`, `deploy/containers/*.Dockerfile`,
`deploy/containers/migration-entrypoint.sh`,
`src/Vistara.Api/Composition/**`, `src/Vistara.Storage.Azure/**`,
`src/Vistara.Worker/Composition/**`, `.github/workflows/release-images.yml`.
