# Platform API boundary

`src/api/generated` is produced from the reviewed gallery OpenAPI manifest and
must not be edited. The account, tenant, capability, API key, and job routes are
not in that manifest yet, so this folder holds a small hand-written client that
follows the same conventions: `fetch` injection, `same-origin` credentials, and
`VistaraApiError` problem details.

Every route below is implemented on the API branch
(`feature/fc-capabilities-surface`). The client calls nothing else.

## Routes used

| Route | Request | Response | Used by |
|---|---|---|---|
| `GET /api/v1/me` | — | `CurrentUser` | session bootstrap |
| `POST /api/v1/auth/login` | `{ login, password, tenantId? }` | `{ user, csrfToken }` | `/login` |
| `POST /api/v1/auth/logout` | — | `204` | account menu, `/settings` |
| `GET /api/v1/capabilities` | — | `Capabilities` | `/admin/policies` |
| `GET /api/v1/admin/storage` | — | `StorageSummary` | `/admin/storage` |
| `POST /api/v1/setup` | `{ tenantSlug, tenantName, email, displayName, password }` | `ProvisionedOwner` | `/setup` |
| `GET /api/v1/tenants` | — | `{ items: TenantSummary[] }` | `/settings` |
| `GET /api/v1/tenants/{tenantId}/members` | — | `{ items: TenantMember[] }` | `/admin/users` |
| `POST /api/v1/tenants/{tenantId}/members` | `{ email, role }` | `TenantMember` | `/admin/users` |
| `GET /api/v1/api-keys` | — | `{ items: ApiKeySummary[] }` | `/settings` |
| `POST /api/v1/api-keys` | `{ scopes, expiresAt? }` | `{ key, secret }` | `/settings` |
| `DELETE /api/v1/api-keys/{keyId}` | — | `204` | `/settings` |
| `GET /api/v1/jobs/{jobId}` | — | `JobStatus` | job lookup |
| `GET /api/v1/jobs` | `states`, `type`, `limit`, `cursor` | `{ items, nextCursor? }` | `/admin/jobs` |
| `POST /api/v1/jobs/{jobId}/retry` | `If-Match` | `JobStatus` | `/admin/jobs` |
| `POST /api/v1/jobs/{jobId}/cancel` | `If-Match` | `JobStatus` | `/admin/jobs` |
| `GET /api/v1/me/preferences` | — | `UserPreferences` | `/settings` |
| `PATCH /api/v1/me/preferences` | `If-Match`, merge patch | `UserPreferences` | `/settings` |
| `PATCH /api/v1/tenants/{tenantId}/members/{userId}` | `If-Match`, `{ role?, status? }` | `TenantMember` | `/admin/users` |

## Conventions the client relies on

- The antiforgery token is returned by `POST /api/v1/auth/login` and by
  `GET /api/v1/me` (for cookie sessions) as `csrfToken`, and is sent on unsafe
  requests in the header named by `csrfHeaderName` (`X-Vistara-CSRF` by
  default). A reloaded browser therefore reads the session once before its
  first unsafe request. The token is held in memory only and dropped on
  sign-out; nothing is persisted.
- Entity tags are `"v{version}"` (`src/api/versionTag.ts`).
- `412` means a stale `If-Match`: reload the record and reapply the edit.
  `409` means a state conflict that repeating the same edit will not fix.
- Collections answer with `{ items }`, cursor collections with
  `{ items, nextCursor? }`.

## Contracts still required from the API

These screens are specified in `docs/specification.md` §11 but have no route
yet. Each renders an honest "not available in this release" panel instead of
calling an invented endpoint. The exact contract each screen will consume:

### 1. Storage connection validation

`/admin/storage` composes a candidate provider configuration and offers an
explicit "Test connection". The credential is held in memory for that one call
and cleared immediately afterwards. Until this route exists the assistant says
the deployment cannot test connections and offers the deploy template instead.

```jsonc
// POST /api/v1/admin/storage/validate      (platform administrator only)
// The body is never persisted, logged, or echoed back.
{
  "provider": "filesystem" | "azureBlob" | "s3",

  "filesystem": { "rootPath": "/var/lib/vistara/media" },

  "azureBlob": {
    "accountName": "vistaramedia",
    "container": "originals",
    "endpointSuffix": "core.windows.net",      // optional
    "credentialKind": "accountKey" | "sasToken",
    "accountKey": "<secret>",                  // when credentialKind is accountKey
    "sasToken": "<secret>"                     // when credentialKind is sasToken
  },

  "s3": {
    "endpoint": "https://s3.eu-central-1.example",
    "region": "eu-central-1",
    "bucket": "vistara-media",
    "accessKeyId": "<secret>",
    "secretAccessKey": "<secret>",
    "forcePathStyle": true
  }
}

// 200
{
  "valid": true,
  "provider": "s3",
  "checks": [
    { "id": "reachable" | "authenticated" | "read" | "write" | "delete",
      "status": "passed" | "failed" | "skipped",
      "detail": "Redacted, human-readable outcome" }
  ],
  "message": "Optional summary shown when valid is false"
}
```

Required behaviour: perform a write and delete of a small probe object under a
reserved prefix, never mutate existing data, never return any part of a
submitted credential in `detail`, `message`, or problem details, never write the
credential to logs or storage, and answer `403` for non-administrators and
`422` for a malformed body.

### 2. First-run setup discovery

`/login` links to `/setup` only when the deployment says provisioning is still
open, and there is no anonymous route for that today.

```jsonc
// GET /api/v1/setup      (anonymous)
{ "available": true }     // false once an owner exists
```

`POST /api/v1/setup` is already published and is used as-is: `201` with the
provisioned owner, `409 setup.already_provisioned`, `409
setup.provisioning_contended`, `422 setup.invalid_request` with a per-field
`errors` map, and `422 setup.weak_password`.

### 3. Sign-in providers

`/login` renders only the local form because the capability document has no
authentication section. An optional single sign-on button needs:

```jsonc
// GET /api/v1/capabilities  ->  additional member
"authentication": { "localAccounts": true,
                    "oidc": { "displayName": "Corp SSO",
                              "startPath": "/api/v1/auth/oidc" } }
```

When a route lands in the reviewed manifest, move its calls to the generated
client and delete the matching model here.

## Published, not yet consumed here

The API also publishes tenant policy administration and the audit log:

```text
GET   /api/v1/admin/policies   ->  { retention, sharing, quotas, version }, ETag "v{version}"
PATCH /api/v1/admin/policies   ->  If-Match, merge patch; 412 stale, 409 conflict
GET   /api/v1/admin/audit      ->  { items: [ { id, occurredAt, actor { kind, id, displayName },
                                                action, outcome, resourceType, resourceId } ],
                                     nextCursor? }
```

`/admin/policies` still shows the enforced limits from the capability document
and `/admin/audit` still states that it reads nothing. Moving both onto these
routes is the next piece of Web work; the contracts above need nothing further
from the API.
