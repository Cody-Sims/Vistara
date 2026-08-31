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
| `POST /api/v1/admin/storage/validate` | Probe a candidate storage target | — |

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

```jsonc
// POST /api/v1/admin/storage/validate     tenant owner + quotas.manage
// exactly one provider object, and only these members
{ "provider": "filesystem", "filesystem": { "rootPath": "/srv/vistara/media" } }
{ "provider": "azure",
  "azure": { "accountName": "acct", "containerName": "media",
             "serviceUri": "https://acct.blob.core.windows.net" } }
{ "provider": "s3",
  "s3": { "bucketName": "media", "region": "eu-central-1",
          "serviceUrl": "https://s3.example.com", "forcePathStyle": true } }

// 200 -> exactly this shape, never provider text
{ "reachable": true, "provider": "s3",
  "code": "storage.reachable", "message": "The storage target answered." }
```

Codes: `storage.reachable`, `storage.unreachable`, `storage.denied`,
`storage.timed_out`, `storage.path_missing`, `storage.path_not_writable`,
`storage.blocked_endpoint`, `storage.insecure_endpoint`,
`storage.unresolvable_endpoint`, `storage.endpoint_rejected`.

Rules:

- Any member outside the lists above is rejected with `422`; a request naming
  zero or more than one provider is rejected the same way.
- Credentials are not accepted. The validated target has no credential member,
  so nothing is persisted, logged, or echoed, and the active storage
  configuration is never modified.
- An endpoint that resolves to a loopback, private, carrier-grade, link-local,
  unique-local, or multicast address is refused before any probe. Plaintext
  `http` requires the operator's existing trusted endpoint host configuration.
- Each probe is bounded by a server-side timeout and answers
  `storage.timed_out`; a cancelled client request is not converted into a
  timeout. The platform rate limiter guards the route and answers `429` with
  `Retry-After`.
- This release validates configuration shape and endpoint reachability. It
  does not verify remote credentials, so `reachable` is not proof that a
  future upload will be authorized.

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
- `POST /api/v1/admin/storage/validate` checks reachability, not credentials.
- The capability document has no `authentication` section, so a client cannot
  yet discover an external sign-in provider.
