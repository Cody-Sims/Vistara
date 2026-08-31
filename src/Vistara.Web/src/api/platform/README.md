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
| `GET /api/v1/capabilities` | — | `Capabilities` | deployment limits |
| `GET /api/v1/admin/policies` | — | `TenantPolicies` | `/admin/policies` |
| `GET /api/v1/admin/storage/validate` | — | `{ supported, providers }` | `/admin/storage` |
| `POST /api/v1/admin/storage/validate` | candidate configuration | `StorageValidationResponse` | `/admin/storage` |
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
- `GET /api/v1/me` names the membership role but publishes no scopes and no
  credential kind. The antiforgery token is the only signal the contract gives:
  an interactive cookie session receives one, a tenant-bound credential such as
  an API key never does. `src/features/session/roles.ts` therefore reads the
  credential kind from `csrfToken`, derives scopes from the membership role for
  a cookie session only, and assumes none for a tenant-bound credential, whose
  key scopes never include `members.manage` or `quotas.manage`. Administration
  screens are offered by scope, not by the reported role.
- Entity tags are `"v{version}"` (`src/api/versionTag.ts`).
- `412` means a stale `If-Match`: reload the record and reapply the edit.
  `409` means a state conflict that repeating the same edit will not fix.
  Because `If-Match` is per version, edits to one record are queued rather than
  sent side by side; `src/features/settings/preferenceSync.ts` is the pattern
  for `me/preferences`.
- `429` carries `Retry-After` in seconds and is surfaced as
  `VistaraThrottledError` so the wait can be shown rather than a bare failure.
- Collections answer with `{ items }`, cursor collections with
  `{ items, nextCursor? }`.
- Job state is lower camel (`pending`, `leased`, `retryScheduled`,
  `completed`, `deadLettered`) and each job publishes
  `actions: { retry, cancel }`, which is what the interface offers rather than
  guessing from the state.
- A quota member is a number or `null`; `null` is no limit at all, which is not
  the same as zero.

## Contracts still required from the API

These screens are specified in `docs/specification.md` §11 but have no route
yet. Each renders an honest "not available in this release" panel instead of
calling an invented endpoint. The exact contract each screen will consume:

### 1. Sign-in providers

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

## Contracts this client matches exactly

### First-run setup

`GET /api/v1/setup` answers `{ available }` or `429 setup.throttled` with
`Retry-After`. `/login` links to `/setup` when it reads `available: true`, and
keeps the link with an explanation when the read is throttled or absent,
because the read is advisory: `POST /api/v1/setup` is the only arbiter and its
`409 setup.already_provisioned` and `409 setup.provisioning_contended` are
handled on `/setup` itself, alongside `422 setup.invalid_request` with a
per-field `errors` map and `422 setup.weak_password`.

### Candidate storage validation

`GET /api/v1/admin/storage/validate` is read before any credential is entered.
The assistant offers only the providers it lists, and disables testing when
`supported` is `false` or the read answers `401` or `403`.

`POST /api/v1/admin/storage/validate` receives exactly one provider object:

- `filesystem` carries a path and no credential, so `authenticated` is
  `skipped`.
- `azureBlob` defaults to `managedIdentity`, which submits no secret at all;
  `accountKey` and `sasToken` are sent only when chosen and are cleared as soon
  as the call returns.
- `s3` sends `accessKeyId` and `secretAccessKey` together or not at all, with
  an optional `sessionToken` that is only valid beside them.

The five checks (`reachable`, `authenticated`, `read`, `write`, `delete`) are
always rendered in that order, including when the answer omits one. A `detail`
is drawn from the closed catalogue, so the timeout detail is shown as the
provider's own words rather than reinterpreted. Statuses are handled
individually: `200` with `valid: false` is a completed answer rather than an
error, `403` explains that owner rights are needed, `413` asks for shorter
values, `422` marks the rejected members, and `429` shows the published wait. A
client-side cancel aborts the request, reports "Test cancelled", and clears the
credential.

### Policies and jobs

`/admin/policies` reads the published policy and shows an absent quota as
unlimited. `/admin/jobs` filters on the lower camel states and offers retry or
cancel only where `actions` allows it, so the known `409 jobs.cancel_unsupported`
is never triggered from the interface.

## Still to build here

`PATCH /api/v1/admin/policies` and `GET /api/v1/admin/audit` are published and
unused: policy editing and the audit screen are the next pieces of Web work,
and need nothing further from the API.
