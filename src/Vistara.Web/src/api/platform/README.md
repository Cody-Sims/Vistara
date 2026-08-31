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
| `GET /api/v1/capabilities` | — | `Capabilities` | `/admin/storage`, `/admin/policies` |
| `GET /api/v1/tenants` | — | `{ items: TenantSummary[] }` | `/settings` |
| `GET /api/v1/tenants/{tenantId}/members` | — | `{ items: TenantMember[] }` | `/admin/users` |
| `POST /api/v1/tenants/{tenantId}/members` | `{ email, role }` | `TenantMember` | `/admin/users` |
| `GET /api/v1/api-keys` | — | `{ items: ApiKeySummary[] }` | `/settings` |
| `POST /api/v1/api-keys` | `{ scopes?, expiresAt? }` | `{ key, secret }` | `/settings` |
| `DELETE /api/v1/api-keys/{keyId}` | — | `204` | `/settings` |
| `GET /api/v1/jobs/{jobId}` | — | `JobStatus` | `/admin/jobs` |

## Conventions the client relies on

- The antiforgery token is returned by `POST /api/v1/auth/login` as `csrfToken`
  and is sent on unsafe requests in the header named by `csrfHeaderName`
  (`X-Vistara-CSRF` by default). The token is held in memory only and dropped on
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

### 1. Antiforgery token for a restored session

`GET /api/v1/me` must also return the antiforgery token for the existing cookie
session; otherwise a browser that reloads the page cannot make any unsafe
request until the user signs in again.

```jsonc
// GET /api/v1/me  200
{
  "userId": "...", "email": "...", "displayName": "...",
  "tenantId": "...", "role": "TenantAdmin",
  "tenants": [ { "id": "...", "slug": "...", "name": "...",
                 "role": "TenantAdmin", "membershipStatus": "Active" } ],
  "csrfHeaderName": "X-Vistara-CSRF",
  "csrfToken": "<token bound to the current session>"   // required addition
}
```

### 2. Account preferences

Density, reduced motion, and paged reading mode are stored on the device today.
To follow an account they need:

```jsonc
// GET /api/v1/me/preferences  200   ETag: "v3"
{ "density": "comfortable" | "compact",
  "reducedMotion": false,
  "screenReaderPagedMode": false,
  "locale": "en-US",            // optional, BCP 47
  "timeZone": "Europe/Berlin",  // optional, IANA
  "version": 3 }

// PATCH /api/v1/me/preferences   If-Match: "v3"
// body: any subset of the fields above (merge patch)
// 200 -> the full document with the new version and ETag
// 412 -> stale If-Match          409 -> state conflict
```

### 3. Tenant member role and status changes

`/admin/users` can list and invite. Changing a role or suspending a member
needs:

```jsonc
// PATCH /api/v1/tenants/{tenantId}/members/{userId}
// If-Match: "v{version}"   body: { "role"?: TenantRole, "status"?: MembershipStatus }
// 200 -> TenantMember with the new version and ETag "v{version}"
// 412 -> stale If-Match    409 -> state conflict (for example the last owner)
```

### 4. Job collection and operator actions

`GET /api/v1/jobs/{jobId}` reads one job. `/admin/jobs` needs a tenant-scoped
list plus the two operator actions:

```jsonc
// GET /api/v1/jobs?states=Failed&states=Dead&type=derivatives&limit=50&cursor=...
// 200 -> { "items": [ JobStatus ], "nextCursor": "..." }

// POST /api/v1/jobs/{jobId}/retry    If-Match: "v{version}"  -> 200 JobStatus
// POST /api/v1/jobs/{jobId}/cancel   If-Match: "v{version}"  -> 200 JobStatus
```

### 5. Storage usage

`GET /api/v1/capabilities` describes configured limits, not consumption.
`/admin/storage` needs:

```jsonc
// GET /api/v1/admin/storage  200
{ "buckets": [ { "id": "originals", "kind": "s3" | "filesystem" | "azure" | "gcs",
                 "status": "healthy" | "degraded" | "unavailable",
                 "usedBytes": 0, "quotaBytes": 0, "objectCount": 0,
                 "lastCheckedAt": "2026-01-01T00:00:00Z", "message": null } ],
  "originalBytes": 0, "derivativeBytes": 0, "stagingBytes": 0,
  "quotaBytes": 0, "pendingUploadBytes": 0 }
```

### 6. Tenant policies

```jsonc
// GET /api/v1/admin/policies  200  ETag: "v7"
{ "retention": { "trashRetentionDays": 30, "purgeGraceDays": 7 },
  "sharing": { "publicLinksEnabled": true, "maxLinkLifetimeDays": 30,
               "requirePasswordForPublicLinks": false },
  "quotas": { "storageBytes": 0, "dailyTransformPixels": 0,
              "concurrentUploads": 4 },
  "version": 7 }

// PATCH /api/v1/admin/policies  If-Match: "v7"  (merge patch of the groups above)
// 200 -> the document above   412 -> stale If-Match   409 -> state conflict
```

### 7. Audit events

```jsonc
// GET /api/v1/admin/audit?outcome=denied&action=share.created&limit=50&cursor=...
// 200 -> { "items": [ { "id": "...", "occurredAt": "2026-01-01T00:00:00Z",
//                       "actor": { "kind": "user" | "apiKey" | "system",
//                                  "id": "...", "displayName": "..." },
//                       "action": "share.created",
//                       "outcome": "succeeded" | "denied" | "failed",
//                       "resourceType": "share", "resourceId": "...",
//                       "requestId": "..." } ],
//           "nextCursor": "..." }
```

### 8. Sign-in providers

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
