# Azure identity, RBAC, and secrets for Vistara

Companion to [Azure free credits](azure-free-credits.md). That guide is the
linear runbook; this one holds the identity, role-assignment, registry, and
secret-handling detail it points at, so the runbook stays readable.

> **The hosted bootstrap does all of this for you.**
> [`./deploy/azure/up.sh`](azure-hosted-bootstrap.md) creates three
> user-assigned identities, assigns exactly the container- and vault-scoped
> roles listed here, writes the API key pepper into Key Vault without it ever
> reaching a command line, and creates the Entra application registration and
> its federated identity credential. Read this guide when you are doing it by
> hand, reviewing what the template did, or granting access outside the
> deployment's own resource group.

Labels follow the same convention: **[Verified]** is quoted or directly
restated from the linked Microsoft page or read out of this repository's code;
**[Inferred]** is this guide's own choice or derivation; **[Unverified]** means
no primary source was confirmed.

**Research date for all Microsoft citations: 2026-08-30.**

## Contents

- [1. Ordering](#1-ordering)
- [2. Managed identity and blob RBAC](#2-managed-identity-and-blob-rbac)
- [3. Least-privilege fallback when you cannot create role assignments](#3-least-privilege-fallback-when-you-cannot-create-role-assignments)
- [4. Key Vault and secret hygiene](#4-key-vault-and-secret-hygiene)
- [5. Private GHCR registry credentials](#5-private-ghcr-registry-credentials)
- [6. Removing identity and role assignments](#6-removing-identity-and-role-assignments)

---

## 1. Ordering

Vistara binds blob access to a **user-assigned** managed identity, and a
user-assigned identity is a resource of its own: it exists, and can hold role
assignments, before any container app does. That removes the create-then-grant
round trip a system-assigned identity forces, and it is what
[`deploy/azure/infra/modules/identity.bicep`](../../deploy/azure/infra/modules/identity.bicep)
and [`rbac.bicep`](../../deploy/azure/infra/modules/rbac.bicep) do in the
hosted bootstrap, where the API, the worker, and the migration job each get
their own identity and least-privilege role assignments.

The sequence across both documents is:

1. Provision the resource group, storage, PostgreSQL, and the Container Apps
   environment — [azure-free-credits.md §5](azure-free-credits.md#5-provisioning-with-the-azure-cli).
2. Create the two user-assigned identities and grant their blob roles —
   [§2](#2-managed-identity-and-blob-rbac). This does **not** need the apps to
   exist.
3. Create the Key Vault, write the secrets, and grant the same identities
   **Key Vault Secrets User** — [§4](#4-key-vault-and-secret-hygiene).
4. If your GHCR packages are private, prepare registry credentials —
   [§5](#5-private-ghcr-registry-credentials).
5. Create the container apps with the identities attached and their client IDs
   in configuration — [azure-free-credits.md §8](azure-free-credits.md#8-migrations-deployment-and-validation).
6. Return here for the two steps that need an existing app: the Key Vault
   secret references in [§4.4](#44-reference-the-secrets-once-the-apps-exist)
   and, for private images, the registry credentials in
   [§5.2](#52-private-packages-store-the-token-in-key-vault-reference-it-by-name).

Every role assignment is already in place when the apps start, so nothing waits
on permission propagation; only the two app-scoped commands in step 6 come
after deployment.

The shell variables (`$RG`, `$STORAGE`, `$CONTAINER`, `$KV`, `$APIAPP`,
`$WORKERAPP`, `$APIMI`, `$WORKERMI`) are the ones defined in
[azure-free-credits.md §4](azure-free-credits.md#4-naming-and-shell-variables).

---

## 2. Managed identity and blob RBAC

**[Verified from `src/Vistara.Api/Composition/Media/MediaComposition.cs` and
`src/Vistara.Worker/Composition/Media/MediaComposition.cs`]** the supported
production path is a **user-assigned** managed identity named by its client ID:

```text
Media__Storage__Azure__CredentialMode=ManagedIdentity
Media__Storage__Azure__ManagedIdentityClientId=<user-assigned-client-id>
```

`DefaultMediaRuntimeDependencies.CreateAzureCredential(MediaAzureOptions)` then
builds
`new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(...))`
and caches it for the process. Nothing chains to a developer credential such as
the Azure CLI, so a host that cannot reach its identity endpoint fails instead
of borrowing whatever identity happens to be signed in.

**[Verified from `MediaOptionsValidator.ValidateAzure`]** the validator refuses
to start the app when:

- `ManagedIdentity` is selected without `ManagedIdentityClientId`, or the value
  is not a non-empty hyphenated GUID (`8ec1a4d5-42d1-4d84-9d2a-9a9a2f3f9a11`
  form; braces, the 32-character form, and padding are rejected);
- a client ID is supplied for any other credential mode;
- `ManagedIdentity` is combined with `ConnectionString` or
  `AllowSharedKeySas`;
- an identity credential is pointed at anything but a first-party Azure Blob
  endpoint for the configured account, such as
  `https://$STORAGE.blob.core.windows.net`.

**`DefaultCredential` is not a supported deployment mode.** It stays for local
development, where `ASPNETCORE_ENVIRONMENT`/`DOTNET_ENVIRONMENT` is
`Development` and `DefaultAzureCredential` picks up your `az login` session.
Every other environment — including an unnamed, empty, or custom one such as
`Test`, `QA`, or `Preview` — is rejected unless the deployment reviews and sets
`Media__Storage__Azure__AllowDefaultCredentialOutsideDevelopment=true`, which
should be reserved for a deliberate exception rather than a normal rollout.

**[Verified]** "The Azure platform manages the identity, so you don't need to
provision or rotate any secrets."
— <https://learn.microsoft.com/en-us/azure/app-service/overview-managed-identity>

**[Verified]** Microsoft recommends this identity type: "User-assigned managed
identities, which are provisioned independently from compute and can be
assigned to multiple compute resources, are the recommended managed identity
type for Microsoft services." You "may also create a managed identity as a
standalone Azure resource", and its service principal "is managed separately
from the resources that use it" — where a system-assigned service principal "is
tied to the lifecycle of that Azure resource".
— <https://learn.microsoft.com/en-us/entra/identity/managed-identities-azure-resources/overview>

### 2.1 Create one identity per role

**[Inferred]** The API and the worker get separate identities so their role
assignments, and any later revocation, stay independent.

```bash
for MI in "$APIMI" "$WORKERMI"; do
  az identity create --name "$MI" --resource-group "$RG" --location "$LOC"
done

export APIMI_ID="$(az identity show --name "$APIMI" \
  --resource-group "$RG" --query id -o tsv)"
export APIMI_PRINCIPAL="$(az identity show --name "$APIMI" \
  --resource-group "$RG" --query principalId -o tsv)"
export APIMI_CLIENT="$(az identity show --name "$APIMI" \
  --resource-group "$RG" --query clientId -o tsv)"

export WORKERMI_ID="$(az identity show --name "$WORKERMI" \
  --resource-group "$RG" --query id -o tsv)"
export WORKERMI_PRINCIPAL="$(az identity show --name "$WORKERMI" \
  --resource-group "$RG" --query principalId -o tsv)"
export WORKERMI_CLIENT="$(az identity show --name "$WORKERMI" \
  --resource-group "$RG" --query clientId -o tsv)"
```

— <https://learn.microsoft.com/en-us/cli/azure/identity>

**[Inferred]** `clientId` is the value Vistara wants
(`Media__Storage__Azure__ManagedIdentityClientId`); `principalId` is the object
ID that role assignments use; `id` is the resource ID that
`az containerapp create --user-assigned` and `identityref:` need. They are
three different values and are not interchangeable. The client ID is an
identifier rather than a secret, but Vistara still redacts it from its own
startup diagnostics, so read it back from `az identity show` rather than from
application logs.

### 2.2 Grant two roles, not one

```bash
STORAGE_ID="$(az storage account show \
  --name "$STORAGE" --resource-group "$RG" --query id -o tsv)"

for PRINCIPAL in "$APIMI_PRINCIPAL" "$WORKERMI_PRINCIPAL"; do
  # Data plane, scoped to the single container (least privilege).
  az role assignment create \
    --assignee-object-id "$PRINCIPAL" \
    --assignee-principal-type ServicePrincipal \
    --role "Storage Blob Data Contributor" \
    --scope "$STORAGE_ID/blobServices/default/containers/$CONTAINER"

  # Account scope: required to mint user delegation SAS.
  az role assignment create \
    --assignee-object-id "$PRINCIPAL" \
    --assignee-principal-type ServicePrincipal \
    --role "Storage Blob Delegator" \
    --scope "$STORAGE_ID"
done
```

**[Verified]** Container-level scope is Microsoft's documented least-privilege
pattern: "By limiting roles and scopes, you limit the resources that are at
risk if the security principal is ever compromised." The same article notes
that "When you create an Azure Storage account, you aren't automatically
assigned permissions to access data via Microsoft Entra ID. You must
explicitly assign yourself an Azure role" — which also applies to *your own*
account when you create the container.
— <https://learn.microsoft.com/en-us/azure/storage/blobs/assign-azure-role-data-access>

**[Verified]** The second assignment is not optional for Vistara.
`src/Vistara.Storage.Azure/AzureSdkBlobClient.cs` calls
`GetUserDelegationKeyAsync`, and Microsoft states: "Azure RBAC action:
`Microsoft.Storage/storageAccounts/blobServices/generateUserDelegationKey/action`;
Least privileged built-in role: **Storage Blob Delegator**", and "If the
security principal is assigned a role that permits data access but is scoped to
the level of a container, you can additionally assign the Storage Blob
Delegator role to that security principal at the level of the storage account,
resource group, or subscription."
— <https://learn.microsoft.com/en-us/rest/api/storageservices/get-user-delegation-key>

**[Inferred]** Because the key is fetched lazily at SAS-minting time, a missing
`Storage Blob Delegator` assignment does **not** fail startup. It surfaces as
an authorization error the first time a share link or direct upload is
requested.

**[Verified]** Creating role assignments requires
`Microsoft.Authorization/roleAssignments/write` (RBAC Administrator or User
Access Administrator).
— <https://learn.microsoft.com/en-us/azure/app-service/overview-managed-identity>

**[Verified]** Managed identities "don't support cross-directory scenarios":
moving the subscription to another tenant breaks them, and identity
configuration is per deployment slot.

---

## 3. Least-privilege fallback when you cannot create role assignments

If your account cannot write role assignments — common on a tenant you do not
own — Vistara supports a shared-key path.
**[Verified from `MediaComposition.cs` (`MediaOptionsValidator.ValidateAzure`)]**
set all three of:

```text
Media__Storage__Azure__CredentialMode=SharedKey
Media__Storage__Azure__ConnectionString=<secret>
Media__Storage__Azure__AllowSharedKeySas=true
```

The validator rejects the combination unless **both** the connection string and
the explicit `AllowSharedKeySas` opt-in are present, rejects mixing either of
them with an identity credential mode, and rejects a
`ManagedIdentityClientId` here — a shared-key deployment must not also claim an
identity.

**[Inferred]** Treat this as a downgrade of last resort:

- It reintroduces a long-lived account key with full account authority, where
  the RBAC path is scoped to one container.
- It is incompatible with `--allow-shared-key-access false`. **[Verified]**
  with shared-key access disabled, "service SAS and account SAS" are denied and
  only user delegation SAS and Entra-authorized requests succeed.
  — <https://learn.microsoft.com/en-us/azure/storage/common/shared-key-authorization-prevent>
- It replaces user-delegation SAS with shared-key SAS, so revocation means
  rotating the account key rather than removing a role assignment.

Rotate the key when you are finished:

```bash
az storage account keys renew \
  --account-name "$STORAGE" --resource-group "$RG" --key primary
```

— <https://learn.microsoft.com/en-us/cli/azure/storage/account/keys>

---

## 4. Key Vault and secret hygiene

Vistara needs real secrets even when blob access is passwordless: the
PostgreSQL passwords and the API key pepper.
**[Verified from `deploy/generate-env.sh`]** the pepper is
`openssl rand -base64 32` and the database passwords are 36 random URL-safe
bytes. Generate them the same way. **Do not** reuse anything from
`deploy/env.example`; its credential fields are intentionally empty.

### 4.1 Create the vault in RBAC mode and grant yourself write access

```bash
az keyvault create --name "$KV" --resource-group "$RG" --location "$LOC" \
  --enable-rbac-authorization true

KV_ID="$(az keyvault show --name "$KV" --resource-group "$RG" --query id -o tsv)"
ME="$(az ad signed-in-user show --query id -o tsv)"

az role assignment create \
  --assignee-object-id "$ME" --assignee-principal-type User \
  --role "Key Vault Secrets Officer" --scope "$KV_ID"
```

**[Verified]** In RBAC mode, creating a vault does **not** grant you data-plane
access; `az keyvault secret set` fails until you hold a role that can write
secrets. **Key Vault Secrets Officer** is the least-privileged built-in role
that can: "Perform any action on the secrets of a key vault, except manage
permissions. Only works for key vaults that use the 'Azure role-based access
control' permission model." The article shows the same
`az role assignment create --role "Key Vault Secrets Officer" ... --scope
/subscriptions/<subscription-id>/resourcegroups/<resource-group>/providers/Microsoft.KeyVault/vaults/<vault-name>`
pattern used above.
— <https://learn.microsoft.com/en-us/azure/key-vault/general/rbac-guide>

**[Inferred]** Do not give yourself **Key Vault Administrator**; it adds key,
certificate, and permission management you do not need here. If you left the
vault in access-policy mode instead, use
`az keyvault set-policy --secret-permissions set get list` and grant the apps
`get` rather than assigning RBAC roles.

Role assignments can take a short time to propagate; if the first
`secret set` returns `Forbidden`, wait and retry before changing anything.
**[Inferred]**

### 4.2 Write the secrets

```bash
az keyvault secret set --vault-name "$KV" --name vistara-api-pepper \
  --value "$(openssl rand -base64 32 | tr -d '\n')"
az keyvault secret set --vault-name "$KV" --name vistara-api-db-password \
  --value "$(openssl rand -base64 36 | tr -d '\n' | tr '+/' '-_')"
az keyvault secret set --vault-name "$KV" --name vistara-worker-db-password \
  --value "$(openssl rand -base64 36 | tr -d '\n' | tr '+/' '-_')"
az keyvault secret set --vault-name "$KV" --name vistara-migrator-db-password \
  --value "$(openssl rand -base64 36 | tr -d '\n' | tr '+/' '-_')"
```

**[Inferred]** These generate the value inline so no secret is ever typed. For
a value you already hold, prefer the verified `--file` parameter over
`--value`, because a literal `--value` is visible in shell history and in
`ps` output while the command runs:

```bash
az keyvault secret set --vault-name "$KV" --name <secret-name> --file <path>
```

— `--file` and `--value` are both documented parameters of
<https://learn.microsoft.com/en-us/cli/azure/keyvault/secret>

### 4.3 Grant the identities read access

The identities exist as soon as [§2.1](#21-create-one-identity-per-role) created
them, so this runs now, before any container app exists:

```bash
for PRINCIPAL in "$APIMI_PRINCIPAL" "$WORKERMI_PRINCIPAL"; do
  az role assignment create \
    --assignee-object-id "$PRINCIPAL" \
    --assignee-principal-type ServicePrincipal \
    --role "Key Vault Secrets User" --scope "$KV_ID"
done
```

**[Verified]** **Key Vault Secrets User** grants exactly "Read secret contents
including secret portion of a certificate with private key".
— <https://learn.microsoft.com/en-us/azure/key-vault/general/rbac-guide>

### 4.4 Reference the secrets once the apps exist

`az containerapp secret set` targets an app, so it is the one step here that
must wait for
[azure-free-credits.md §8](azure-free-credits.md#8-migrations-deployment-and-validation)
to create `$APIAPP` and `$WORKERAPP`. Come back and run it then:

```bash
az containerapp secret set \
  --name "$APIAPP" --resource-group "$RG" \
  --secrets "api-pepper=keyvaultref:https://$KV.vault.azure.net/secrets/vistara-api-pepper,identityref:$APIMI_ID"
```

**[Verified]** The Container Apps reference syntax is
`keyvaultref:<KEY_VAULT_SECRET_URI>,identityref:<MANAGED_IDENTITY_ID>`.
— <https://learn.microsoft.com/en-us/azure/container-apps/manage-secrets>

**[Inferred]** `identityref:` takes the **resource ID** of the user-assigned
identity (`$APIMI_ID`), which is what the documented worked example uses, and
that identity must already hold **Key Vault Secrets User** ([§4.3](#43-grant-the-identities-read-access))
and be attached to the app, which
[azure-free-credits.md §8](azure-free-credits.md#8-migrations-deployment-and-validation)
does with `az containerapp create --user-assigned`. `identityref:system` is only
meaningful for a system-assigned identity, which this design does not use.

### 4.5 Hygiene rules for this repository

- **[Verified]** App settings "are securely encrypted at rest, but if you need
  capabilities for managing secrets, they should go into a key vault."
  — <https://learn.microsoft.com/en-us/azure/app-service/app-service-key-vault-references>
- **[Verified]** A Key Vault reference without a pinned version refreshes
  "within 24 hours"; the cache is refetched every 24 hours, and any
  configuration change forces an immediate refetch and an app restart. Plan
  rotation around that window.
- **[Verified from `SecurityComposition.cs`]** `Security__RequiredSecretKeys__N`
  makes the API **fail to start** when a named configuration key has no value.
  Use it as a deployment tripwire, for example
  `Security__RequiredSecretKeys__0=Platform__Authentication__ApiKeys__Peppers__v1`.
- **[Verified from `AzureBlobStoreOptions.ToString()`]** the blob adapter
  redacts its connection string in diagnostics, but
  `ConnectionStrings__Vistara` is an ordinary environment variable. Pass it as
  a Container Apps secret reference (`secretref:`), never as an inline
  `--env-vars` value.
- **[Verified]** Resource names and tags are not a hiding place: "Don't include
  any personal, sensitive, or confidential information in resource names ...
  and resource tags."
  — <https://learn.microsoft.com/en-us/azure/postgresql/configure-maintain/quickstart-create-server>
- Never commit a filled-in environment file. `deploy/README.md` states plainly:
  "Do not copy example passwords into a deployment."

---

## 5. Private GHCR registry credentials

**[Verified from `.github/workflows/release-images.yml`]** this repository
publishes `ghcr.io/<namespace>/vistara-api`,
`ghcr.io/<namespace>/vistara-worker`, and
`ghcr.io/<namespace>/vistara-migrations`. Whatever you do below has to cover
all three: a private registry that the migration job cannot pull from fails
before the API is ever deployed.

### 5.1 Preferred: make the packages public

**[Verified]** "You can also access public container images anonymously."
— <https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry>

**[Inferred]** If the packages are public, configure **no registry credentials
at all**: omit `--registry-server`, `--registry-username`, and
`--registry-password`, and there is no token to store, rotate, or leak. For an
evaluation deployment of an open-source project this is both the simplest and
the safest option. Package visibility is managed per package in GitHub —
<https://docs.github.com/en/packages/learn-github-packages/configuring-a-packages-access-control-and-visibility>

### 5.2 Private packages: store the token in Key Vault, reference it by name

**[Verified]** A pull token needs only the "`read:packages` scope to download
container images and read their metadata."
— <https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry>

Write the token to Key Vault **from a file**, never as a command-line
argument:

```bash
umask 077
: > ./ghcr-token.txt                     # created empty, mode 0600
# Paste the read:packages token into ./ghcr-token.txt with your editor.

az keyvault secret set \
  --vault-name "$KV" --name vistara-ghcr-token --file ./ghcr-token.txt

rm -P ./ghcr-token.txt 2>/dev/null || rm -f ./ghcr-token.txt
```

**[Inferred]** Writing to a `0600` file and deleting it keeps the token out of
shell history and out of the process list, which `--value "<token>"` would
expose to every user on the machine via `ps`. If you must type a secret on a
command line, prefix the command with a space in a shell configured with
`HISTCONTROL=ignorespace`, and treat the token as compromised if you forget.

Then, **after** [azure-free-credits.md §8](azure-free-credits.md#8-migrations-deployment-and-validation)
has created the app, reference the token from it by **secret name**. Both
commands below target an existing container app; at creation time pass the
token with `az containerapp create --registry-server/--registry-username/--registry-password`
instead, or create the app from a public image first and switch the registry
afterwards:

```bash
az containerapp secret set \
  --name "$APIAPP" --resource-group "$RG" \
  --secrets "ghcr-token=keyvaultref:https://$KV.vault.azure.net/secrets/vistara-ghcr-token,identityref:$APIMI_ID"

az containerapp registry set \
  --name "$APIAPP" --resource-group "$RG" \
  --server ghcr.io \
  --username "<github-username>" \
  --password ghcr-token
```

**[Verified]** Container Apps stores registry credentials as
`passwordSecretRef`, which "identifies the name of the secret in the secrets
array name where you defined the password" — so the value passed here is a
secret **name**, not the token itself.
— <https://learn.microsoft.com/en-us/azure/container-apps/containers>,
<https://learn.microsoft.com/en-us/cli/azure/containerapp/registry>

**[Verified]** `az containerapp registry set --identity` authenticates with a
managed identity **instead of** username/password, but the documented parameter
describes Azure Container Registry and `acrpull`; it is not a GHCR
substitute.

**[Inferred]** Repeat both commands for `$WORKERAPP` with
`identityref:$WORKERMI_ID`, and repeat them for the migration job's registry
configuration if that image is also private.

---

## 6. Removing identity and role assignments

Deleting the resource group removes the resources inside it, but role
assignments scoped **outside** the group survive as orphans. In this design the
account-scoped `Storage Blob Delegator` assignments are inside the same
resource group as the storage account, so a group delete removes them — but
re-deriving and deleting them explicitly is safe, idempotent, and correct even
if you scoped anything to the subscription.

Run this **before** `az group delete`, while the identities still exist to
resolve their principal IDs:

```bash
STORAGE_ID="$(az storage account show \
  --name "$STORAGE" --resource-group "$RG" --query id -o tsv 2>/dev/null || true)"
KV_ID="$(az keyvault show \
  --name "$KV" --resource-group "$RG" --query id -o tsv 2>/dev/null || true)"

for MI in "$APIMI" "$WORKERMI"; do
  PRINCIPAL="$(az identity show \
    --name "$MI" --resource-group "$RG" --query principalId -o tsv 2>/dev/null || true)"
  if [ -z "$PRINCIPAL" ] || [ "$PRINCIPAL" = "null" ]; then
    echo "skip: no user-assigned identity named $MI"
    continue
  fi

  if [ -n "$STORAGE_ID" ]; then
    az role assignment delete --assignee-object-id "$PRINCIPAL" \
      --role "Storage Blob Delegator" --scope "$STORAGE_ID" --yes || true
    az role assignment delete --assignee-object-id "$PRINCIPAL" \
      --role "Storage Blob Data Contributor" \
      --scope "$STORAGE_ID/blobServices/default/containers/$CONTAINER" --yes || true
  fi

  if [ -n "$KV_ID" ]; then
    az role assignment delete --assignee-object-id "$PRINCIPAL" \
      --role "Key Vault Secrets User" --scope "$KV_ID" --yes || true
  fi
done
```

**[Inferred]** The guards matter: `az identity show` returns nothing when the
identity was never created or is already gone, and
**[Verified]** `az role assignment delete` is documented to "Delete all role
assignments" matching whatever filters it is given — an unguarded empty
assignee would widen the delete instead of narrowing it. `--assignee-object-id`
is used rather than `--assignee` because the value is already an object ID and
needs no directory lookup. The `|| true` keeps a partial teardown from aborting
the loop before the second app is cleaned up.

Verify nothing is left behind, then continue with the teardown in
[azure-free-credits.md §11](azure-free-credits.md#11-teardown):

```bash
az role assignment list --scope "$STORAGE_ID" \
  --query "[].{principal:principalId, role:roleDefinitionName, scope:scope}" -o table
```

— <https://learn.microsoft.com/en-us/cli/azure/role/assignment>
