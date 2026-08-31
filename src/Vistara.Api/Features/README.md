# Platform and account API surface

These routes are owned by `src/Vistara.Api/Features` and are not part of the
reviewed gallery OpenAPI manifest. `src/Vistara.Web/src/api/platform` holds the
matching hand-written client; this file is the API side of that contract.

A composition root wires the whole surface with
`services.AddVistaraPlatformSurface()` and
`endpoints.MapVistaraPlatformSurface()`.

## Conventions

- JSON is camelCase and timestamps are RFC 3339 UTC.
- Errors are RFC 9457 `application/problem+json` with a stable `code`.
- Mutable aggregates publish `ETag: "v{version}"`. Single-resource mutations
  require `If-Match`: `428` when absent, `400` when malformed, `412` when
  stale, `409` for a state conflict that repeating will not fix.
- Collections answer with `{ items }`; paged collections add `nextCursor`.
  A cursor is bound to the tenant and the normalized query, so replaying it
  elsewhere answers `409`.
- Every response is `Cache-Control: no-store` except the capability document,
  which is privately cacheable.
- Cross-tenant requests are concealed as `404`.

## Routes

| Method and route | Purpose | Concurrency |
|---|---|---|
| `GET /api/v1/capabilities` | Deployment capabilities and configured limits | `ETag`, `304` |
| `POST /api/v1/auth/login` | Local cookie sign-in; returns `csrfToken` | — |
| `POST /api/v1/auth/logout` | Ends the session; always clears the cookie | — |
| `POST /api/v1/setup` | One-time first-owner provisioning | `409` once claimed |
| `GET /api/v1/me` | Current principal, memberships, `csrfToken` | — |
| `GET /api/v1/me/preferences` | Account preferences | `ETag` |
| `PATCH /api/v1/me/preferences` | Merge patch of preferences | `If-Match` |
| `GET /api/v1/tenants` | Tenants the principal belongs to | — |
| `GET /api/v1/tenants/{id}/members` | Member roster | — |
| `POST /api/v1/tenants/{id}/members` | Invite a member | — |
| `PATCH /api/v1/tenants/{id}/members/{userId}` | Role and status change | `If-Match` |
| `GET /api/v1/api-keys` | Tenant API keys, never secrets | — |
| `POST /api/v1/api-keys` | Create a key; secret returned once | — |
| `DELETE /api/v1/api-keys/{id}` | Revoke a key | — |
| `GET /api/v1/jobs` | Paged job administration | cursor |
| `GET /api/v1/jobs/{id}` | One job | `ETag`, `304` |
| `POST /api/v1/jobs/{id}/retry` | Requeue a failed job | `If-Match` |
| `POST /api/v1/jobs/{id}/cancel` | Always `409`; see `actions.cancel` | — |
| `GET /api/v1/admin/storage` | Consumption and health | — |
| `GET /api/v1/admin/policies` | Retention, sharing, quotas | `ETag` |
| `PATCH /api/v1/admin/policies` | Merge patch of the policy groups | `If-Match` |
| `GET /api/v1/admin/audit` | Redacted audit trail | cursor |
| `GET /api/v1/admin/storage/validate` | Whether this deployment can test a credential | — |
| `POST /api/v1/admin/storage/validate` | Test a candidate storage target and its credential | — |

`/api/v1/admin/*` describes the tenant deployment rather than a gallery
resource. Membership and API key administration keep their resource-shaped
paths because they address a tenant sub-collection.

## Authorization

| Operation | Minimum role | Required scope |
|---|---|---|
| Read self, preferences, tenants | Viewer | — |
| Member read and administration | TenantAdmin | `members.manage` |
| API key administration | TenantAdmin | `api_keys.manage` |
| Audit read | TenantAdmin | `members.manage` |
| Storage and policy administration | TenantOwner | `quotas.manage` |
| Job read and retry | Viewer | `assets.read` |

Only an interactive cookie session may enumerate the principal's other
tenants. An API key or bearer token receives a projection limited to the
tenant it was issued for, and never receives an antiforgery token.

## Redaction

- Capabilities, storage, and job answers never contain bucket names,
  containers, endpoints, filesystem paths, connection strings, or credentials.
- Job answers omit payloads, trace context, and lease owners.
- Audit answers omit the field-level before and after summaries, which can
  contain user content; they publish only actor, action, outcome, and
  resource identity.
- API key answers never contain a digest, and the plaintext secret is
  returned exactly once at creation.

## Published vocabularies

One casing is used for every enumeration a client can both read and send, so
a listed value can be fed straight back into a filter.

| Concept | Published values | Filter |
|---|---|---|
| Job state | `pending`, `leased`, `retryScheduled`, `completed`, `deadLettered` | `states=` accepts these; the previous PascalCase spelling stays accepted |
| Job actions | `actions: { retry, cancel }` per job | — |
| Audit outcome | `succeeded`, `rejected`, `failed` | `outcome=` accepts these and the PascalCase spelling |
| Audit actor kind | `user`, `apiKey`, `system` | — |
| Membership status | `Invited`, `Active`, `Suspended`, `Removed` | request body only |
| Tenant role | `TenantOwner`, `TenantAdmin`, `Member`, `Viewer` | request body only |

## Quotas

`GET /api/v1/admin/policies` publishes each quota as a number or `null`.
`null` means the tenant has no limit for that dimension; zero would mean
nothing is allowed. In `PATCH`, an absent quota member is unchanged and an
explicit `null` clears the limit, so a retention-only patch can never turn an
unlimited tenant into a blocked one. Quota members this release does not model
are preserved verbatim.

## Antiforgery

The token is derived from the browser session, so `POST /api/v1/auth/login`
and `GET /api/v1/me` return the same value and every tab of one session holds
an identical usable token. Reading the session never invalidates a token
another tab is using, so a client needs no retry-on-403 protocol; it may read
`/api/v1/me` once and keep the token for the session's lifetime.

## Candidate storage validation

The setup assistant reads `GET /api/v1/admin/storage/validate` before it offers
"Test connection", so a secret is never sent to a deployment that cannot check
it.

```jsonc
// GET /api/v1/admin/storage/validate      tenant owner + quotas.manage
{ "supported": true, "providers": ["filesystem", "azureBlob", "s3"] }
```

`POST` takes exactly one provider object and only the members listed below. The
body is parsed by hand into a redacting secret holder, so a submitted credential
is never bound to a DTO, never printed by a `ToString`, and is zeroed when the
request ends.

```jsonc
// POST /api/v1/admin/storage/validate     tenant owner + quotas.manage
{ "provider": "filesystem", "filesystem": { "rootPath": "/srv/vistara/media" } }

{ "provider": "azureBlob",
  "azureBlob": {
    "accountName": "vistaramedia",              // 3-24 lower alphanumerics
    "container": "originals",
    "endpointSuffix": "core.windows.net",       // optional
    "credentialKind": "managedIdentity" | "accountKey" | "sasToken",
    "accountKey": "<secret>",                   // required for accountKey
    "sasToken": "<secret>"                      // required for sasToken
  } }

{ "provider": "s3",
  "s3": {
    "endpoint": "https://s3.eu-central-1.example",
    "region": "eu-central-1",
    "bucket": "vistara-media",
    "forcePathStyle": true,
    "accessKeyId": "<secret>",                  // with secretAccessKey, or omit both
    "secretAccessKey": "<secret>",
    "sessionToken": "<secret>"                  // optional, only with an access key
  } }

// 200 -> exactly this shape, never provider text and never a credential
{ "valid": true, "provider": "s3",
  "checks": [
    { "id": "reachable",     "status": "passed" },
    { "id": "authenticated", "status": "passed" },
    { "id": "read",          "status": "passed" },
    { "id": "write",         "status": "passed" },
    { "id": "delete",        "status": "passed" }
  ],
  "message": "The storage settings are usable with the supplied credential." }
```

`checks` always carries all five ids in this order. `status` is `passed`,
`failed`, or `skipped`; `detail` is optional and, when present, is drawn from a
fixed catalogue:

`The endpoint is not an allowed validation target.` ·
`The storage target could not be reached.` ·
`The credential was rejected.` ·
`No credential is available for this provider.` ·
`The container or bucket could not be listed with this credential.` ·
`The probe object could not be written.` ·
`The probe object could not be deleted.` ·
`The directory does not exist.` ·
`The provider did not answer within the validation timeout.`

Credential kinds:

- `managedIdentity` is the default for `azureBlob` and submits no secret; the
  deployment's workload or managed identity is used.
- `accountKey` and `sasToken` are bounded ephemeral secrets used to build one
  throwaway container client.
- S3 accepts a static access key with an optional session token. Omitting both
  key members requests an anonymous client, which is only honoured when the
  endpoint host is already in the operator's trusted endpoint host list; a
  request can never nominate its own exemption.
- A filesystem candidate has no credential, so `authenticated` is `skipped`.

Rules:

- Any member outside the lists above is ignored, and a request naming zero or
  more than one provider, or a member that fails validation, is rejected with
  `422` and a per-field `errors` map. A body over 16 KiB or a secret field over
  4096 characters is rejected before a client is built.
- The probe writes and deletes one empty object under the reserved prefix
  `.vistara-validate/` and reads only that prefix. Existing data is never
  listed, read, written, or deleted, and the active storage configuration is
  never modified.
- Secrets live only in the request. They are held in a redacting wrapper, handed
  once to the provider SDK, and zeroed on disposal. Nothing is written to a
  `DbContext`, configuration, a file, a cache, or static state, and no secret
  appears in a response, log message, activity tag, exception, or problem
  document.
- An endpoint that resolves to a loopback, private, carrier-grade, link-local,
  unique-local, or multicast address is refused before any client is
  constructed. Plaintext `http` requires the operator's existing trusted
  endpoint host configuration.
- Each validation is bounded by a server-side timeout and answers a failed
  `reachable` check with the timeout detail; a cancelled client request is not
  converted into a timeout. The one-shot provider client is disposed on every
  path, including timeout and cancellation. The platform rate limiter guards the
  route and answers `429` with `Retry-After`.

## Known limits in this release

- `POST /api/v1/jobs/{id}/cancel` always answers `409`
  `jobs.cancel_unsupported`: the durable job model has no cancelled state, and
  faking one would misreport attempts and the failure reason. Each job
  publishes `actions.retry` and `actions.cancel` so a client can hide the
  action instead of guessing.
- `GET /api/v1/admin/storage` reports `derivativeBytes` as every active blob
  that no asset revision references, because derivative blobs are not tracked
  separately in the core schema yet. The classification runs as a correlated
  `EXISTS`, so it does not grow with the tenant's object count.
- The capability document has no `authentication` section, so a client cannot
  yet discover an external sign-in provider.
