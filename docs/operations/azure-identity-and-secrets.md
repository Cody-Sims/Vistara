# Azure identity, RBAC, and secrets for Vistara

Companion to [Azure free credits](azure-free-credits.md). That guide is the
linear runbook; this one holds the identity, role-assignment, registry, and
secret-handling detail it points at, so the runbook stays readable.

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

A system-assigned identity does not exist until the container app does, so the
sequence across both documents is:

1. Provision the resource group, storage, PostgreSQL, and the Container Apps
   environment — [azure-free-credits.md §5](azure-free-credits.md#5-provisioning-with-the-azure-cli).
2. Create the Key Vault and write the secrets — [§4](#4-key-vault-and-secret-hygiene)
   below. This does **not** need the apps to exist.
3. If your GHCR packages are private, prepare registry credentials —
   [§5](#5-private-ghcr-registry-credentials).
4. Create the container apps — [azure-free-credits.md §8](azure-free-credits.md#8-migrations-deployment-and-validation).
5. Grant the resulting identities their roles — [§2](#2-managed-identity-and-blob-rbac)
   and [§4](#4-key-vault-and-secret-hygiene).
6. Restart or update the apps so they pick up the new permissions.

The shell variables (`$RG`, `$STORAGE`, `$CONTAINER`, `$KV`, `$APIAPP`,
`$WORKERAPP`) are the ones defined in
[azure-free-credits.md §4](azure-free-credits.md#4-naming-and-shell-variables).

---

## 2. Managed identity and blob RBAC

**[Verified from `src/Vistara.Api/Composition/Media/MediaComposition.cs`]**
`DefaultMediaRuntimeDependencies.CreateAzureCredential()` returns
`new DefaultAzureCredential()`, so a system-assigned managed identity is picked
up with no secret at all when
`Media__Storage__Azure__CredentialMode=DefaultCredential`.

**[Verified]** "The Azure platform manages the identity, so you don't need to
provision or rotate any secrets."
— <https://learn.microsoft.com/en-us/azure/app-service/overview-managed-identity>

### 2.1 Assign the identities

```bash
for APP in "$APIAPP" "$WORKERAPP"; do
  az containerapp identity assign \
    --name "$APP" --resource-group "$RG" --system-assigned
done
```

— <https://learn.microsoft.com/en-us/cli/azure/containerapp/identity>

### 2.2 Grant two roles, not one

```bash
STORAGE_ID="$(az storage account show \
  --name "$STORAGE" --resource-group "$RG" --query id -o tsv)"

for APP in "$APIAPP" "$WORKERAPP"; do
  PRINCIPAL="$(az containerapp identity show \
    --name "$APP" --resource-group "$RG" --query principalId -o tsv)"

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
the explicit `AllowSharedKeySas` opt-in are present, and rejects mixing either
of them with `DefaultCredential`.

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

### 4.3 Grant the apps read access and reference the secrets

```bash
for APP in "$APIAPP" "$WORKERAPP"; do
  PRINCIPAL="$(az containerapp identity show \
    --name "$APP" --resource-group "$RG" --query principalId -o tsv)"
  az role assignment create \
    --assignee-object-id "$PRINCIPAL" \
    --assignee-principal-type ServicePrincipal \
    --role "Key Vault Secrets User" --scope "$KV_ID"
done

az containerapp secret set \
  --name "$APIAPP" --resource-group "$RG" \
  --secrets "api-pepper=keyvaultref:https://$KV.vault.azure.net/secrets/vistara-api-pepper,identityref:system"
```

**[Verified]** **Key Vault Secrets User** grants exactly "Read secret contents
including secret portion of a certificate with private key", and the Container
Apps reference syntax is
`keyvaultref:<KEY_VAULT_SECRET_URI>,identityref:<MANAGED_IDENTITY_ID>`.
— <https://learn.microsoft.com/en-us/azure/key-vault/general/rbac-guide>,
<https://learn.microsoft.com/en-us/azure/container-apps/manage-secrets>

**[Inferred]** `identityref:system` for a system-assigned identity — the
documented worked example uses a user-assigned identity resource ID. If it is
rejected, create a user-assigned identity, grant **Key Vault Secrets User** to
it, attach it with `az containerapp identity assign --user-assigned`, and pass
its resource ID.

### 4.4 Hygiene rules for this repository

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
publishes `ghcr.io/<namespace>/vistara-api` and
`ghcr.io/<namespace>/vistara-worker`.

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

Then reference it from the container app by **secret name**:

```bash
az containerapp secret set \
  --name "$APIAPP" --resource-group "$RG" \
  --secrets "ghcr-token=keyvaultref:https://$KV.vault.azure.net/secrets/vistara-ghcr-token,identityref:system"

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

**[Inferred]** Repeat both commands for `$WORKERAPP`, and repeat them for the
migration job's registry configuration if that image is also private.

---

## 6. Removing identity and role assignments

Deleting the resource group removes the resources inside it, but role
assignments scoped **outside** the group survive as orphans. In this design the
account-scoped `Storage Blob Delegator` assignments are inside the same
resource group as the storage account, so a group delete removes them — but
re-deriving and deleting them explicitly is safe, idempotent, and correct even
if you scoped anything to the subscription.

Run this **before** `az group delete`, while the apps still exist to resolve
their principal IDs:

```bash
STORAGE_ID="$(az storage account show \
  --name "$STORAGE" --resource-group "$RG" --query id -o tsv 2>/dev/null || true)"
KV_ID="$(az keyvault show \
  --name "$KV" --resource-group "$RG" --query id -o tsv 2>/dev/null || true)"

for APP in "$APIAPP" "$WORKERAPP"; do
  PRINCIPAL="$(az containerapp identity show \
    --name "$APP" --resource-group "$RG" --query principalId -o tsv 2>/dev/null || true)"
  if [ -z "$PRINCIPAL" ] || [ "$PRINCIPAL" = "null" ]; then
    echo "skip: no system-assigned identity for $APP"
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

**[Inferred]** The guards matter: `az containerapp identity show` returns an
empty or `null` `principalId` when no identity was ever assigned, and
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
