# Vistara — Product Specification and Engineering Plan

**Status:** Implementation-ready
**Date:** 2026-08-28
**License:** Apache-2.0
**Repository:** Public GitHub repository
**Implementation baseline:** .NET 10 LTS

**Implementation status:** the version 0.1 scope in section 4 is implemented on
`dev` and staged in pull request #2 for the 0.1.0 tag; it counts as released
only once that pull request merges and the tag is published. Section 16 carries
a per-wave status marker. Section 13 and the Wave 8 AI tasks describe a future
architecture; no AI capability exists in the codebase. Nothing in
`docs/future-ideas/` — AI metadata and editing, a Model Context Protocol server,
or cloud imports — is implemented or scheduled. The staged capabilities are
summarized in `README.md` and `docs/release-notes.md`.

## 1. Executive product definition

**Vistara** is a lightweight, open-source, self-hosted image control plane and gallery. It stores immutable originals in local or object storage, validates direct uploads, produces deterministic image derivatives, manages metadata and lifecycle policy, and delivers responsive galleries through cache-efficient URLs.

Its defensible wedge is:

- .NET-native, operationally small, modular deployment.
- Object-storage-first rather than filesystem-library-first.
- Portable local, Azure Blob, and S3-compatible storage.
- Secure direct upload, metadata, policy, signing, lifecycle, and delivery.
- A focused, responsive gallery—not an attempt to match every Immich or PhotoPrism feature.
- Optional asynchronous AI whose output can recommend but never directly delete.

Vistara ships as a modular monolith with API and worker roles from one release. SQLite supports one-host installations; PostgreSQL is the production database.

---

## 2. Verified facts, decisions, and assumptions

### Verified facts

- .NET 10 is an active LTS release supported through November 2028; .NET 9 support ends in November 2026:
  https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
- Microsoft recommends Minimal APIs for new ASP.NET Core HTTP APIs:
  https://learn.microsoft.com/en-us/aspnet/core/fundamentals/apis?view=aspnetcore-10.0
- EF Core 10 is LTS and requires .NET 10:
  https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew
- EF Core requires separate migration sets for SQLite and PostgreSQL:
  https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/providers
- SQLite WAL supports concurrent readers but remains single-writer and must stay on one host:
  https://www.sqlite.org/wal.html
- Presigned S3 URLs are reusable bearer credentials until expiry and can overwrite an existing key:
  https://docs.aws.amazon.com/AmazonS3/latest/userguide/using-presigned-url.html
- S3-compatible providers differ materially in supported operations and checksums; R2 documents these differences explicitly:
  https://developers.cloudflare.com/r2/api/s3/api/
- ImageSharp 4 directly requires a build-time license key and remains split-licensed:
  https://sixlabors.com/posts/licence-enforcement-changes/
  https://raw.githubusercontent.com/SixLabors/ImageSharp/main/LICENSE
- NetVips 3.2 targets libvips 8.18. NetVips is MIT; libvips is LGPL-2.1-or-later and uses a demand-driven, low-memory pipeline:
  https://github.com/kleisauke/net-vips/releases
  https://github.com/libvips/libvips/blob/master/README.md
- RFC 8246 defines immutable HTTP responses for resources that do not change during freshness:
  https://www.rfc-editor.org/rfc/rfc8246.html

### Chosen decisions

| Area | Decision |
|---|---|
| Runtime | .NET 10, ASP.NET Core Minimal APIs, EF Core 10 |
| License | Apache-2.0 |
| Architecture | Modular monolith; API and worker executables |
| Databases | PostgreSQL 18.x production; SQLite single-host starter |
| Storage | Local, Azure Blob, generic S3 adapter with AWS/R2/B2/MinIO profiles |
| Originals | Immutable, application-generated keys |
| Derivatives | Deterministic, immutable, disposable |
| Imaging | NetVips/libvips behind `IImageProcessor` |
| MVP formats | JPEG, PNG, WebP; single-frame only |
| AVIF | Post-MVP, after codec/license/performance validation |
| Gallery | Integrated React/TypeScript SPA |
| Browser auth | Secure cookie; optional external OIDC login |
| API auth | API keys and configured JWT bearer issuers |
| Queue | Durable relational jobs; no broker initially |
| Tenancy | Tenant-aware schema from day one; default installation creates one tenant |
| Privacy | Private by default; delivery derivatives strip sensitive metadata |
| Trash | Reversible, 30-day default retention |
| AI | Disabled by default, asynchronous, provenance-preserving |
| Deletion invariant | No model output can directly mutate or permanently delete media |

### Assumptions

- Linux containers on x64 and arm64 are primary production targets.
- MVP supports still images only; no video, RAW development, SVG, PDF, or animation.
- Default upload limit is 50 MiB and 40 megapixels, operator-configurable.
- CDN support is provider-neutral through HTTP headers and signed-delivery abstractions.
- Legal/privacy obligations remain deployment-specific.


---

## 3. Personas and use cases

| Persona | Concrete use cases |
|---|---|
| Solo self-hoster | Install with Compose, upload images, browse, share links, back up data |
| Tenant owner | Manage members, quotas, policies, retention, storage, and API keys |
| Member/curator | Upload, search, tag, album-organize, favorite, share, trash, and restore |
| Contributor | Upload to permitted tenant/albums without administering other users |
| API integrator | Direct-upload assets, request derivatives, manage metadata using API keys/JWT |
| Public recipient | Browse a shared gallery and download permitted renditions |
| Instance operator | Monitor health/jobs, migrate, restore backups, rotate credentials |
| Archivist | Review duplicates and future AI-generated organization recommendations |
| Accessibility user | Complete all primary workflows with keyboard, screen reader, zoom, or touch |

Primary scenarios:

1. Upload directly to S3/Azure, finalize, validate, and display in the gallery.
2. Upload through the API when using local storage.
3. Generate a safe responsive derivative set without blocking gallery requests.
4. Search and organize assets using metadata, albums, favorites, and tags.
5. Share an album using a password/expiry/download-controlled link.
6. Trash and restore assets without immediately deleting physical content.
7. Use an API key for automation without granting platform administration.
8. Later opt into AI tagging/search and review its provenance and recommendations.

---

## 4. Scope

### MVP / version 0.1

**Status: implemented.** Every item below is implemented on `dev` and staged in
pull request #2 for the 0.1.0 tag; none of it is released until that pull
request merges and the tag is published. Its exact shape, the endpoints,
presets, and configuration keys, and the caveats that remain are recorded in
`README.md`, `docs/release-notes.md`, and
`docs/security/admin-and-cloud-onboarding.md`.

- .NET 10 API and worker.
- SQLite and PostgreSQL with separate migration assemblies.
- Local, Azure Blob, and S3-compatible adapters.
- Tested profiles for AWS S3, Cloudflare R2, Backblaze B2, and MinIO.
- Direct single-part and multipart uploads where providers support them.
- Streaming proxy uploads for local storage and provider fallback.
- SHA-256 integrity verification and exact-duplicate detection.
- JPEG, PNG, and WebP processing through NetVips.
- Immutable originals and deterministic derivatives.
- Signed, allowlisted transformation presets.
- Integrated responsive gallery:
  - library grid/list;
  - viewer/details;
  - albums;
  - flat tags;
  - favorites;
  - metadata search/filter/sort;
  - upload queue;
  - bulk actions;
  - shares;
  - trash/restore/purge.
- Local account/cookie authentication, API keys, and JWT validation.
- Tenant isolation, authorization, quotas, audit, outbox, and durable jobs.
- Docker images and starter/production Compose environments.
- OpenTelemetry, health endpoints, migration bundles, backup/restore guidance.
- WCAG 2.2 AA target.

### Post-MVP

**Status: not implemented.** Nothing in this list ships in 0.1, and inclusion
here is not a schedule or a release promise.

- AVIF and animation.
- Remote URL ingestion with dedicated SSRF controls.
- Saved searches and smart albums.
- Advanced EXIF, map, ratings, fuzzy search, and collection uploads.
- Redis/message broker only after measured need.
- CDN-provider purge and signed-cookie integrations.
- Local AI tags, OCR, captions, embeddings, duplicate review, and sorting.
- Optional hosted AI adapters.
- Unnamed face clustering only after separate privacy review and consent.
- Full offline albums or native mobile applications.
- PostgreSQL vector search and larger-scale worker pools.

### Explicit non-goals

- Immich, PhotoPrism, or Google Photos feature parity.
- Full image editor or RAW development.
- Video transcoding.
- Social feed, comments, followers, or public discovery network.
- Cross-instance federation.
- Arbitrary filesystem browser semantics.
- Kubernetes or microservices as the default deployment.
- Public-by-default buckets.
- Arbitrary user-authored image operation strings.
- Face identification, surveillance, demographic/emotion inference.
- Autonomous moderation, deletion, purge, or account restriction.

---

## 5. System context and data flows

### System context

```text
Users / API clients / public viewers
                  |
             HTTPS / CDN
                  |
        +---------v----------+
        | Vistara.Api        |
        | SPA, auth, REST,   |
        | signing, metadata  |
        +----+-----------+---+
             |           |
          EF Core     upload/read grants
             |           |
      +------v---+   +---v--------------------+
      | SQLite / |   | Blob storage           |
      | Postgres |   | local / Azure / S3     |
      +------^---+   | originals/derivatives  |
             |       +-----------^------------+
       jobs/outbox               |
             |                   |
        +----v-------------------+--+
        | Vistara.Worker            |
        | validation, NetVips, jobs,|
        | reconcile, purge, future AI|
        +---------------------------+
```

### Direct upload

```text
Client -> API: create upload intent
API -> DB: reserve quota and persist Pending upload
API -> Storage: create exact-key signed plan
API -> Client: signed headers/parts and expiry
Client -> Storage: upload bytes
Client -> API: commit upload
API -> DB: CommitRequested
Worker -> Storage: HEAD and stream SHA-256/decode validation
Worker -> Storage: promote staging object to immutable original key
Worker -> DB: revision + asset + jobs + outbox, transactionally
Worker -> Storage: create common derivatives
Gallery <- API/SSE: asset Ready
```

### Delivery

```text
Gallery -> API: list assets and responsive rendition URLs
Gallery -> CDN/origin: immutable rendition URL
CDN miss -> storage/media origin
Missing rendition -> API queues one deduplicated job
Worker -> storage: conditional publication under deterministic key
```

### Trash and purge

```text
Ready -> Trashed
  - deny normal reads immediately
  - revoke new grants
  - retain relationships and bytes
  - allow restore for 30 days

Trashed -> Restored
or
Trashed -> Purging -> Purged
  - recheck authorization, holds, revisions, references
  - delete derivatives, originals when unreferenced, metadata and AI artifacts
  - retain minimal tombstone and audit event
```

---

## 6. Repository, modules, and dependency direction

```text
Vistara.slnx
global.json
Directory.Build.props
Directory.Packages.props
.editorconfig
LICENSE
README.md

src/
  Vistara.Domain/
    Tenancy/
    Identity/
    Assets/
    Uploads/
    Derivatives/
    Gallery/
    Sharing/
    Lifecycle/
    Jobs/
    Ai/

  Vistara.Application/
    Common/
    Tenancy/
    Assets/
    Uploads/
    Derivatives/
    Gallery/
    Sharing/
    Lifecycle/
    Jobs/
    Ai/

  Vistara.Contracts/
  Vistara.Persistence/
  Vistara.Migrations.Sqlite/
  Vistara.Migrations.Postgres/
  Vistara.Storage.Local/
  Vistara.Storage.Azure/
  Vistara.Storage.S3/
  Vistara.Imaging.NetVips/
  Vistara.Auth/
  Vistara.Observability/
  Vistara.Api/
  Vistara.Worker/
  Vistara.Web/

tests/
  Vistara.UnitTests/
  Vistara.ArchitectureTests/
  Vistara.Api.ContractTests/
  Vistara.IntegrationTests/
  Vistara.Storage.ConformanceTests/
  Vistara.Imaging.Tests/
  Vistara.MigrationTests/
  Vistara.E2E/
  Vistara.PerformanceTests/

deploy/
  compose.dev.yml
  compose.starter.yml
  compose.postgres.yml
  containers/
  nginx/
```

### Dependency rules

```text
Vistara.Domain -> no Vistara project
Vistara.Application -> Domain
Contracts -> no infrastructure
Persistence -> Application + Domain
Storage.* -> Application
Imaging.NetVips -> Application
Auth -> Application + Persistence
Observability -> framework/OpenTelemetry only
Api -> Contracts + Application + infrastructure composition
Worker -> Application + infrastructure composition
Web -> HTTP/OpenAPI only
```

Rules:

- No infrastructure reference from Domain or Application.
- No frontend import from backend source.
- No generic repository or required mediator framework.
- Application ports are focused interfaces such as `IBlobStore`,
  `IAssetRepository`, `IJobQueue`, `IImageProcessor`, and `IClock`.
- Shared dependency-registration files are owned by explicit integration tasks.
- Architecture tests enforce the graph.

---

## 7. Data model

All tenant-owned tables include `tenant_id`. IDs are application-generated UUIDv7 values. Timestamps are UTC. Mutable aggregates use an application-managed `version bigint`.

### Tenancy and identity

| Table | Principal fields and invariants |
|---|---|
| `tenants` | `id`, unique `slug`, name, status, settings JSON, quotas, version |
| `users` | `id`, normalized email, display name, status, created/updated |
| `external_identities` | issuer + subject unique; user FK |
| `tenant_memberships` | tenant + user unique; role, status, joined timestamp |
| `api_keys` | tenant, prefix, HMAC digest, owner, scopes, expiry/revocation, last-used |
| `revoked_tokens` | issuer, `jti`, expiry, reason; optional emergency denylist |
| `auth_sessions` | refresh/session hash, user, expiry, revoked timestamp |

Roles: `TenantOwner`, `TenantAdmin`, `Member`, `Viewer`. `PlatformAdmin` is a separate audience and policy namespace.

### Assets and blobs

| Table | Principal fields and invariants |
|---|---|
| `assets` | tenant, owner, current revision, status, visibility, title, description, capture fields, created/updated, version |
| `asset_revisions` | asset, monotonic revision, blob, detected format, dimensions, frames, safe/raw metadata, immutable |
| `blobs` | tenant, provider, container, key, provider version, SHA-256, provider checksum, size, MIME, state |
| `asset_favorites` | tenant + user + asset unique |
| `asset_metadata_history` | actor/source, field changes, timestamp |

Blob physical deduplication is tenant-scoped. Multiple logical assets may reference one exact blob without merging ownership, album, or metadata records.

Capture time stores:

- `captured_at_utc`
- `captured_local`
- `captured_offset_minutes`
- `capture_precision`
- `capture_source`

### Uploads and quota reservations

| Table | Fields |
|---|---|
| `upload_sessions` | tenant, actor, strategy, staging key, provider upload ID, expected bytes/checksum/type, state, expiry, idempotency key |
| `upload_parts` | session + part number unique, ETag, checksum, bytes |
| `quota_reservations` | tenant, upload/job, reserved bytes/objects/compute, expiry |
| `idempotency_requests` | principal, key, request hash, response reference, expiry |

### Derivatives

| Table | Fields |
|---|---|
| `transform_presets` | scope, stable name, revision, canonical recipe JSON, enabled |
| `derivatives` | tenant, asset revision, recipe hash, pipeline fingerprint, blob, dimensions, format, state, error |
| `derivative_requests` | requester, preset/parameters, job, timestamps |

Unique derivative identity:

```text
(asset_revision_id, recipe_sha256, pipeline_fingerprint)
```

### Gallery organization

| Table | Fields |
|---|---|
| `albums` | tenant, owner, name, description, cover asset, sort mode, version |
| `album_items` | album + asset unique, position bigint, added by/at |
| `tags` | tenant, normalized name unique, display name, color, version |
| `asset_tags` | tenant + asset + tag unique, source (`user/import/ai-accepted`) |

### Grants and sharing

| Table | Fields |
|---|---|
| `resource_grants` | tenant, resource type/id, grantee user/group, role |
| `shares` | tenant, token HMAC, target type, album ID or snapshot, password hash, expiry, permissions, revoked/version |
| `share_assets` | share + asset + revision |
| `share_sessions` | short-lived hash after successful password challenge |

Public tokens contain at least 256 random bits and are stored only as keyed hashes.

### Trash, retention, and purge

| Table | Fields |
|---|---|
| `trash_entries` | tenant + asset unique active entry, deleted by/at, purge at, reason, restoration metadata |
| `retention_holds` | asset/tenant scope, reason, created/released by |
| `purge_batches` | requester/approver, dry-run hash, state, counts, timestamps |
| `purge_batch_items` | batch + asset/revision, result, reclaimed bytes |
| `deletion_tombstones` | former asset ID, tenant, purge time, backup-expiry time |

### Jobs, audit, and events

| Table | Fields |
|---|---|
| `jobs` | type, payload/version, state, priority, attempts, available time, lease/heartbeat, dedupe key, trace context |
| `audit_events` | tenant, actor/key, action, resource, before/after summaries, outcome, timestamp |
| `outbox_messages` | sequence, event type/version, payload, publish state |
| `event_log` | retained SSE sequence and minimal client payload |

PostgreSQL workers claim jobs with `FOR UPDATE SKIP LOCKED`. SQLite uses one worker and short write transactions.

### Future AI records

- `ai_models`
- `ai_consents`
- `ai_runs`
- `ai_artifacts`
- `ai_embeddings`
- `ai_review_decisions`
- `ai_spaces`
- `action_proposals`

Every artifact records provider, immutable model digest, preprocessing/config/prompt digest, source revision, score semantics, confidence, provenance, consent snapshot, and review state. User metadata remains separate.

---

## 8. API surface and contracts

All management routes use `/api/v1`. JSON is camelCase. Timestamps are RFC 3339 UTC. OpenAPI 3.1 is generated in CI.

### Core routes

| Method and route | Purpose |
|---|---|
| `GET /api/v1/capabilities` | DB/storage/image/search/upload capabilities and configured limits |
| `GET /api/v1/me` | Current user, tenant memberships, preferences, CSRF bootstrap |
| `POST /api/v1/auth/login` | Local cookie login |
| `POST /api/v1/auth/logout` | End browser session |
| `GET /api/v1/tenants` | Authorized tenant list |
| `GET/POST /api/v1/tenants/{id}/members` | Tenant membership management |
| `GET/POST /api/v1/api-keys` | List/create key; secret returned once |
| `DELETE /api/v1/api-keys/{id}` | Revoke key |

### Uploads

| Route | Contract |
|---|---|
| `POST /api/v1/uploads` | Create upload intent and quota reservation |
| `PUT /api/v1/uploads/{id}/content` | Streaming proxy upload when needed |
| `GET /api/v1/uploads/{id}` | State, parts, expiry |
| `POST /api/v1/uploads/{id}/parts` | Obtain/refresh signed part plans |
| `POST /api/v1/uploads/{id}/commit` | Idempotently request verification |
| `DELETE /api/v1/uploads/{id}` | Abort upload |

### Assets and transformations

| Route | Contract |
|---|---|
| `GET /api/v1/assets` | Cursor list with search/filter/sort |
| `GET /api/v1/assets/{id}` | Full safe metadata and delivery sources |
| `PATCH /api/v1/assets/{id}` | Merge-patch mutable metadata |
| `GET /api/v1/assets/{id}/metadata` | Permission-gated metadata |
| `GET /api/v1/assets/{id}/original` | Redirect to grant or stream with Range |
| `POST /api/v1/assets/{id}/derivatives` | Request signed preset transformation |
| `GET /api/v1/assets/{id}/derivatives` | List available/requested derivatives |
| `POST /api/v1/assets/bulk` | Bulk metadata, album, tag, favorite, trash actions |
| `GET /media/{pipeline}/{sourceHash}/{recipeHash}.{ext}` | Public immutable derivative |
| `GET\|HEAD /delivery/{pipeline}/{sourceHash}/{recipeHash}.{ext}` | Authorized immutable derivative |
| `GET\|HEAD /delivery/assets/{assetId}/{renditionId}` | Tenant-authorized private asset rendition |

Transformation requests accept only:

- preset ID/revision;
- bounded width/height;
- `contain`, `cover`, or `crop`;
- validated focal point/crop rectangle;
- JPEG, PNG, or WebP;
- bounded quality;
- approved metadata/color policies.

They never accept arbitrary operation chains.

### Gallery resources

- `GET /api/v1/timeline`
- `GET/POST /api/v1/albums`
- `GET/PATCH/DELETE /api/v1/albums/{id}`
- `GET/POST/DELETE /api/v1/albums/{id}/items`
- `PATCH /api/v1/albums/{id}/items/order`
- `GET/POST /api/v1/tags`
- `PATCH/DELETE /api/v1/tags/{id}`
- `PUT/DELETE /api/v1/assets/{id}/tags/{tagId}`
- `PUT/DELETE /api/v1/assets/{id}/favorite`
- `GET /api/v1/search/facets`

### Shares and lifecycle

- `GET/POST /api/v1/shares`
- `GET/PATCH/DELETE /api/v1/shares/{id}`
- `GET /api/v1/public/shares/{token}`
- `POST /api/v1/public/shares/{token}/challenge`
- `GET /api/v1/trash`
- `POST /api/v1/trash/restore`
- `POST /api/v1/trash/purge`
- `GET /api/v1/jobs/{id}`
- `GET /api/v1/events` using SSE and `Last-Event-ID`

### Future AI

- `GET /api/v1/assets/{id}/ai-suggestions`
- `POST /api/v1/assets/{id}/ai-suggestions:generate`
- `POST /api/v1/ai-suggestions/{id}:accept`
- `POST /api/v1/ai-suggestions/{id}:reject`
- `POST /api/v1/action-proposals`
- `POST /api/v1/action-proposals/{id}:confirm`

### Status and errors

- `200`: successful read
- `201`: synchronously created resource
- `202`: queued verification/processing/bulk work
- `204`: successful empty mutation
- `206`: byte range
- `304`: conditional media hit
- `400`: malformed request
- `401`: missing/invalid authentication
- `403`: authorized identity lacks operation permission
- `404`: absent or concealed cross-tenant resource
- `409`: state/idempotency/cursor conflict
- `410`: expired/revoked share
- `412`: stale `If-Match`
- `413`: encoded body too large
- `415`: unsupported declared media type
- `422`: invalid, corrupt, or policy-rejected image
- `428`: required `If-Match` absent
- `429`: throttled, with `Retry-After`
- `503`: required dependency unavailable

Errors use RFC 9457 `application/problem+json`:

```json
{
  "type": "https://vistara.dev/problems/upload-integrity",
  "title": "Upload integrity verification failed",
  "status": 422,
  "code": "upload_integrity_failed",
  "traceId": "...",
  "errors": {}
}
```

Specification: https://www.rfc-editor.org/rfc/rfc9457.html

### Pagination

Default `limit=60`, maximum `200`. Signed keyset cursors contain:

- tenant/principal;
- normalized query hash;
- sort tuple plus asset ID;
- ingest snapshot sequence;
- search index revision;
- expiry.

No expensive exact count is returned by default. Cursors reused with another query or tenant return `409`.

### Concurrency

- Every mutable aggregate returns `ETag: "v{version}"`.
- Single-resource mutations require `If-Match`.
- Bulk operations carry `{id, version}` per item.
- Large operations return a job with per-item success/conflict results.
- Retriable creates and commits require `Idempotency-Key`.

---

## 9. Storage design and state machines

### Port

```csharp
public interface IBlobStore
{
    string Name { get; }
    BlobStoreCapabilities Capabilities { get; }

    ValueTask<BlobHead?> HeadAsync(BlobKey key, CancellationToken ct);
    ValueTask<BlobReadHandle> OpenReadAsync(
        BlobKey key, BlobReadOptions options, CancellationToken ct);
    ValueTask<BlobWriteResult> PutAsync(
        BlobKey key, IReplayableBlobContent content,
        BlobWriteOptions options, CancellationToken ct);
    ValueTask<BlobCopyResult> CopyAsync(
        BlobKey source, BlobKey destination,
        BlobCopyOptions options, CancellationToken ct);
    ValueTask DeleteAsync(
        BlobKey key, BlobDeleteOptions options, CancellationToken ct);
    IAsyncEnumerable<BlobHead> ListAsync(
        BlobListOptions options, CancellationToken ct);

    ValueTask<DirectUploadPlan> CreateDirectUploadAsync(
        DirectUploadRequest request, CancellationToken ct);
    ValueTask<MultipartSession> BeginMultipartAsync(
        MultipartRequest request, CancellationToken ct);
    ValueTask<MultipartPartPlan> CreatePartPlanAsync(
        MultipartSession session, int partNumber, CancellationToken ct);
    ValueTask<MultipartCompletion> CompleteMultipartAsync(
        MultipartSession session, IReadOnlyList<UploadedPart> parts,
        CancellationToken ct);
    ValueTask AbortMultipartAsync(
        MultipartSession session, CancellationToken ct);

    ValueTask<ReadGrant> CreateReadGrantAsync(
        BlobKey key, ReadGrantOptions options, CancellationToken ct);
}
```

Capabilities explicitly report:

- direct PUT;
- multipart;
- range reads;
- conditional create/replace;
- conditional multipart completion;
- server-side copy;
- native checksum algorithms;
- object versioning;
- strong read/list-after-write;
- signed read support;
- size/key/part limits.

Unsupported operations return `Unsupported`; adapters must not silently replace a conditional write with a race-prone `HEAD`/`PUT`.

### Provider decisions

| Provider | Adapter behavior |
|---|---|
| Local | Streaming API upload; random same-directory temporary file; fsync then non-overwriting move |
| Azure Blob | SAS/block blobs; prefer user-delegation SAS; ETag conditions |
| AWS S3 | SigV4 direct PUT/multipart, conditional operations, checksums |
| Cloudflare R2 | S3 profile with documented unsupported features and cache caveats |
| Backblaze B2 | S3 profile; do not assume AWS checksum/conditional parity; account for versioning |
| MinIO | S3 profile; capability tests tied to deployed release |

### Keys

```text
staging/{tenantShard}/{tenantId}/{uploadId}
originals/{tenantShard}/{tenantId}/{assetId}/{revision}/{uploadId}.{ext}
derivatives/v{pipeline}/{sourcePrefix}/{sourceSha256}/{recipeSha256}.{ext}
```

No filenames, emails, or PII appear in keys. Keys are lowercase ASCII and application-generated.

### Upload state machine

```text
Pending
  -> UploadIssued
  -> CommitRequested
  -> Verifying
  -> Promoting
  -> Accepted

Any pre-accept state
  -> Aborted | Expired | Rejected

Ambiguous provider completion
  -> OutcomeUnknown
  -> Reconciling
  -> prior successful state | Rejected
```

Commit verification:

1. Authenticate caller and lock the pending intent.
2. Verify provider key, generation/version, length, declared type, expiry.
3. Stream and calculate canonical SHA-256.
4. Identify and fully decode with allowed codec and limits.
5. Optionally scan; scanner failure remains quarantined.
6. Promote using create-only semantics.
7. Transactionally create revision/asset/jobs/outbox and consume quota.
8. Delete staging data asynchronously.

### Derivative state machine

```text
Missing -> Queued -> Processing -> Ready
                         |
                       Failed -> Queued retry
```

A unique DB constraint and storage create-only operation ensure concurrent identical requests produce one visible derivative.

### Reconciliation

Scheduled jobs detect:

- expired upload sessions;
- abandoned multipart sessions;
- staging objects without DB intents;
- active DB blobs missing from storage;
- storage objects not referenced by DB;
- jobs with expired leases;
- purges with uncertain provider outcomes;
- obsolete derivative pipeline generations.

Destructive reconciliation requires age thresholds and a dry-run metric/report before deletion.

---

## 10. Imaging and CDN design

### Processor decision

**Chosen: NetVips/libvips.**

| Option | Assessment |
|---|---|
| ImageSharp | Excellent managed portability and security controls, but split licensing and mandatory direct-dependency build key add friction for a public OSS project; no built-in AVIF |
| NetVips/libvips | Chosen: MIT binding, LGPL native library, strong server performance and low-memory demand-driven processing; native packaging is manageable in Docker |
| imgproxy/Imagor | Rejected for MVP: additional service, configuration, signing, cache, monitoring, and authorization boundary; possible later external processor adapter |

Vistara dynamically links an unmodified libvips package, ships license notices/SBOM data, and documents corresponding source-package locations. This requires release compliance review but avoids ImageSharp’s build-key workflow.

### Adapter boundary

```csharp
public interface IImageProcessor
{
    ImageProcessorCapabilities Capabilities { get; }
    string PipelineFingerprint { get; }

    ValueTask<ImageInspection> InspectAsync(
        Stream source, InspectionLimits limits, CancellationToken ct);

    ValueTask<ImageTransformResult> TransformAsync(
        Stream source, Stream destination,
        CanonicalTransformRecipe recipe, CancellationToken ct);
}
```

The fingerprint includes NetVips/libvips, codec, color-management, recipe-schema, and Vistara pipeline versions.

### Secure pipeline

1. Enforce encoded bytes while streaming.
2. Inspect with an allowlisted loader.
3. Reject unsupported type, dimensions, frames, decoded pixels, and malformed metadata.
4. Decode one frame.
5. Auto-orient.
6. Normalize color profile.
7. Apply validated crop/fit/resize without upscaling unless preset permits.
8. Strip EXIF, GPS, XMP, IPTC, comments, embedded thumbnails, and filenames.
9. Encode explicitly.
10. Reinspect output.
11. Hash bytes and publish conditionally.

### Initial limits

| Limit | Default |
|---|---:|
| Original encoded bytes | 50 MiB |
| Width or height | 20,000 px |
| Aggregate pixels | 40 MP |
| Frames | 1 |
| Estimated decoded memory | 512 MiB |
| Processing deadline | 30 seconds |
| Derivative dimension | 8,192 px |
| Common gallery maximum | 2,400 px |
| Worker transforms | 1 by default; 2 on documented reference host |

All are configurable, with operator hard ceilings.

### Standard derivative presets

- `thumb`: 256, 512
- `grid`: 512, 1024
- `viewer`: 1024, 1600, 2400
- `download-web`: bounded user-selected width
- JPEG fallback, WebP preferred, PNG/lossless WebP for alpha or screenshot-like content

No transform executes synchronously during a gallery listing request.

### Cache policy

Public permanent derivatives:

```http
Cache-Control: public, max-age=31536000, immutable
ETag: "<sha256-of-representation>"
X-Content-Type-Options: nosniff
```

- Format is encoded in the URL; no `Vary: Accept`.
- Mutable aliases are not introduced in MVP.
- API JSON and `202` processing responses use `no-store`.
- Private/revocable delivery performs authorization and uses short bounded edge TTLs or private caching.
- Public immutable content cannot be guaranteed to disappear immediately from all caches; the UI must warn before permanent public publication.

Caching standards:

- https://www.rfc-editor.org/rfc/rfc9111.html
- https://www.rfc-editor.org/rfc/rfc8246.html

---

## 11. Gallery frontend

### Architecture

- React + TypeScript + Vite.
- React Router data APIs.
- TanStack Query for server state.
- TanStack Virtual for vertically virtualized computed rows.
- CSS Modules and design tokens.
- Generated TypeScript API client from OpenAPI.
- Workbox/`vite-plugin-pwa`; cache only fingerprinted shell assets initially.
- Integrated into the API image for same-origin production delivery.
- IndexedDB stores resumable upload metadata, never permanent credentials.

### Routes

```text
/login
/library
/library/recent
/assets/:assetId
/albums
/albums/new
/albums/:albumId
/favorites
/tags
/tags/:tagId
/search
/shared/with-me
/shared/links
/shared/links/:linkId
/trash
/settings
/s/:token
/admin/users
/admin/storage
/admin/jobs
/admin/policies
/admin/audit
/review/duplicates       post-MVP
/review/suggestions      post-MVP
```

### Required behaviors

- Library is the home screen.
- Responsive grid and sortable metadata list.
- Stable `/assets/:id` route; desktop may render it as an overlay.
- Date grouping by capture date with import-date fallback.
- Search/filter state remains URL-addressable.
- Browser Back restores route, filters, focus, and scroll position.
- Visible Select control on touch devices.
- Shift-range selection and “Select visible/all results.”
- Upload queue states: queued, hashing, uploading, paused, processing, complete, duplicate, cancelled, failed.
- Pause/cancel/retry and signature refresh.
- Bulk tag/album/favorite/download/trash.
- Exact duplicates link to the existing asset.
- Trash shows deletion and purge dates, supports Undo and restore.
- Permanent deletion is visually and procedurally separate from Restore.
- Public links show expiry/download/metadata exposure before creation.

### Accessibility

Target WCAG 2.2 AA: https://www.w3.org/TR/WCAG22/

- Semantic lists, headings, links, buttons, and checkboxes.
- Do not use ARIA grid without implementing its complete keyboard model.
- All workflows work without drag-and-drop or hover.
- Minimum WCAG target sizing; primary mobile actions target 44×44 CSS pixels.
- Dialog focus trap, Escape close, and logical focus restoration.
- Focused virtualized rows remain mounted.
- Screen-reader-friendly paged/list mode.
- Status messages for selection, upload, background jobs, and deletion.
- User-authored descriptions may become alt text; unaccepted AI captions may not.
- Reflow at 320 CSS pixels and 400% zoom.
- Reduced motion, RTL, pseudo-localization, locale-aware dates/numbers/plurals.

### Performance budgets

- p75 LCP ≤2.5 seconds.
- p75 INP ≤200 ms.
- CLS ≤0.1.
- Initial JS ≤180 KiB Brotli.
- Initial CSS ≤40 KiB Brotli.
- First metadata page ≤100 KiB compressed.
- Initial visible image bytes ≤500 KiB on a 390×844 DPR-2 viewport.
- At most 400 thumbnail DOM nodes.
- At most one high-priority image.
- No routine scrolling main-thread task over 50 ms.

Core Web Vitals:

- https://web.dev/articles/lcp
- https://web.dev/articles/inp
- https://web.dev/articles/optimize-cls

---

## 12. Security, authorization, and quotas

### Authentication

- Browser: local ASP.NET Core Identity account and secure session cookie.
- Optional external OIDC browser login.
- API keys for automation.
- JWT bearer validation against configured trusted issuers.
- Vistara does not implement a general OAuth authorization server in MVP.

Cookies:

- `Secure`, `HttpOnly`, `SameSite=Lax`.
- Session rotation at login/privilege change.
- Antiforgery header required for unsafe cookie-authenticated requests:
  https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0

API keys:

```text
vst_<non-secret-key-id>_<256-bit-secret>
```

Only `HMAC-SHA-256(server-pepper, secret)` is stored. Keys have explicit scopes, tenant, owner, expiry, revocation, and coarse last-used timestamps.

JWT validation pins algorithms and validates issuer, audience, expiry, not-before, token type, and tenant membership. Guidance: https://www.rfc-editor.org/rfc/rfc8725.html

### Authorization and isolation

- Every query and mutation is tenant-scoped before lookup.
- Tenant context derives from membership/token claims, not an arbitrary header.
- Cross-tenant absence and denial are concealed where appropriate.
- PostgreSQL enables and forces RLS on tenant-owned tables:
  https://www.postgresql.org/docs/current/ddl-rowsecurity.html
- SQLite uses mandatory scoped repositories/interceptors and composite FK/index tests.
- Platform administration uses separate policies, routes, audience, and credentials.
- Infrastructure administration does not silently grant gallery-content access.
- Original download, metadata, derivatives, shares, restore, and purge each receive object-level checks.

OWASP BOLA guidance:
https://owasp.org/API-Security/editions/2023/en/0xa1-broken-object-level-authorization/

### Quotas

Per tenant:

- stored original and derivative bytes;
- active object count;
- pending upload bytes;
- concurrent uploads;
- transform concurrency and daily transform pixels;
- queued jobs;
- optional egress and hosted-AI budget.

Quota bytes are reserved before upload. Expired intents release reservations. Limits apply by IP, user, API key, tenant, endpoint, and global capacity. `429` includes `Retry-After`.

### Threat controls

- Private buckets and application-generated keys.
- Exact upload plans expiring in 5–10 minutes.
- No client-selected storage paths.
- File type allowlist and full decoder validation.
- No SVG, PDF, raw, archives, or remote URL import.
- Worker runs non-root with read-only root filesystem, bounded scratch space, dropped capabilities, and egress limited to required DB/storage endpoints.
- Block cloud metadata addresses.
- Strict CORS only for configured upload origins.
- CSP, HSTS, `nosniff`, restrictive referrer and permissions policies.
- Share-token/password rate limiting.
- No signed URLs, credentials, raw metadata, authorization headers, or image bodies in logs.
- Startup fails when required secrets are missing.
- Workload identity is preferred over static Azure/AWS credentials.
- Secrets enter through secret stores or mounted secret files, never images or command arguments.

Upload guidance:
https://cheatsheetseries.owasp.org/cheatsheets/File_Upload_Cheat_Sheet.html

---

## 13. AI architecture and roadmap

**Status: not implemented.** This section is the design that any future AI work
must satisfy, not a description of shipped behavior. The `Ai` folders in
`src/Vistara.Application` and `src/Vistara.Domain` are empty placeholders: there
are no AI contracts, adapters, models, artifacts, embeddings, proposals, or
review surfaces in the codebase. Read the invariants below as constraints on a
future decision, and see `docs/future-ideas/metadata-and-ai-editing.md` for the
uncommitted research direction.

### Invariant

An AI provider may write typed suggestions or an `ActionProposal`. It receives no interface capable of deleting, moving, hiding, banning, reporting, or writing arbitrary storage objects.

```text
Asset revision -> AI job -> provider adapter -> immutable AI artifact
                                      |
                                      v
                              review/accept/reject
                                      |
                                      v
                        canonical metadata or proposal
```

### Provider contract

```text
capabilities()
submit(request)
poll(operation)
cancel(operation)
health()
```

Requests include tenant, asset revision, capability, model digest, preprocessing/config digest, consent snapshot, timeout, budget, and idempotency key.

### Phases

1. **Deterministic foundation**
   - SHA-256 duplicates.
   - Perceptual hash candidates.
   - Technical quality metrics.
   - No AI dependency.

2. **Local metadata**
   - Fixed-taxonomy tags.
   - OCR.
   - Captions.
   - Suggestions remain distinct from user metadata.

3. **Search and organization**
   - Image/text embeddings.
   - Hybrid metadata/vector search.
   - Similarity clustering.
   - Near-duplicate review.
   - Quality/date/content sorting suggestions.

4. **Optional hosted providers**
   - Separate consent and provider disclosure.
   - Residency, retention, budget, and request auditing.
   - Resized/metadata-stripped payloads by default.

5. **Cleanup rules**
   - Natural language compiles to an allowlisted declarative AST.
   - Deterministic evaluator produces a revision-bound dry run.
   - Human confirms movement to Trash.
   - Purge remains an independent later action.

6. **Optional unnamed face grouping**
   - Local-only default.
   - Separate consent and erasure.
   - No identity lookup or sensitive-attribute inference.

### Provenance and confidence

Every result exposes:

- provider and model;
- immutable model/preprocessing/config versions;
- source revision;
- creation time;
- confidence/score semantics;
- calibration data version;
- local/hosted provenance;
- accepted, edited, or rejected review state.

MVP AI never auto-accepts generated metadata. A future automatically visible tag requires measured precision ≥98% on a representative evaluation set.

### Deletion recommendation firewall

1. Produce read-only criteria and candidate list.
2. Show reasons, uncertainty, shared-link impact, holds, and estimated recoverable bytes.
3. Bind proposal to exact asset revisions and an expiring hash.
4. Reauthenticate for large operations.
5. Recheck permission, ownership, holds, references, and revisions.
6. Human selects and confirms assets.
7. Move to Trash.
8. Retain for 30 days.
9. Require a separate purge confirmation.
10. Preserve audit and restoration records.

No model score can bypass these stages.

Primary governance references:

- https://www.nist.gov/itl/ai-risk-management-framework
- https://cheatsheetseries.owasp.org/cheatsheets/LLM_Prompt_Injection_Prevention_Cheat_Sheet.html
- https://eur-lex.europa.eu/eli/reg/2024/1689/oj

---

## 14. Operations and deployment

### Telemetry

OpenTelemetry traces, metrics, and structured logs cover:

- API latency/errors;
- upload bytes/rejections;
- storage operations;
- DB latency and SQLite busy events;
- job depth, age, attempts, leases, dead letters;
- image inspection/transformation duration and failures;
- worker memory/CPU;
- derivative hit/miss/202 rates;
- reconciliation/orphan counts;
- CDN hit ratio;
- share and authorization failures.

Asset IDs, filenames, hashes, and tenant IDs must not become metric labels.

.NET instrumentation guidance:
https://opentelemetry.io/docs/languages/dotnet/instrumentation/

### Health

- `/health/live`: process only.
- `/health/ready`: DB, schema compatibility, storage sentinel, worker/queue usability.
- `/health/startup`: configuration, migrations, native codec initialization.
- Health responses reveal no versions, credentials, SQL, or topology.

### Migrations

- Separate SQLite and PostgreSQL migration assemblies.
- Migration bundles run once before API rollout.
- Runtime DB users lack schema-owner/DDL permissions in production.
- Expand/backfill/contract changes.
- Advisory lock, lock timeout, and migration ledger.
- Test upgrades from the two prior supported releases.

### Docker roles

One release image supports:

```text
Vistara.Api
Vistara.Worker
migration bundle
```

The API image contains the built SPA. Images use a glibc-based official .NET runtime, run non-root, and pin native libvips packages and base-image digests.

### Compose environments

| Environment | Topology |
|---|---|
| Development | API, web dev server, PostgreSQL, MinIO, worker, OTLP collector |
| Starter | API with embedded worker, SQLite named volume, local blob volume |
| Production example | Migration task, API, worker, PostgreSQL, MinIO/reverse proxy; external object storage supported |

SQLite is never placed on NFS/SMB or shared by replicas.

### Backup and restore

Back up:

- database;
- original blobs;
- required configuration and key identifiers;
- audit records;
- restoration/deletion tombstones.

Derivatives may be excluded because they are reproducible.

Targets:

- Production PostgreSQL: RPO ≤15 minutes, RTO ≤4 hours.
- Starter SQLite: documented daily-backup default, RTO ≤4 hours.
- Quarterly isolated restore drill.
- Restore verifies schema, tenant counts, blob references, checksums, and authorization.
- Purged data may remain in immutable backups until the published backup expiry.

---

## 15. Test strategy and acceptance criteria

### Test layers

- Unit: domain invariants, recipes, signatures, quotas, state machines.
- Architecture: dependency direction and tenant-scoping conventions.
- Integration: API, persistence, jobs, auth, both DB providers.
- Storage conformance: local, Azurite, MinIO; scheduled live AWS/Azure/R2/B2.
- Imaging: golden corpus, corruption, bombs, metadata privacy, fuzz regressions.
- Contract: OpenAPI compatibility and generated TypeScript client.
- E2E: Playwright Chromium, Firefox, WebKit.
- Accessibility: automated plus NVDA/Firefox, VoiceOver/Safari, keyboard.
- Security: cross-tenant, CSRF, JWT, API key, upload, signature, share brute-force.
- Performance: BenchmarkDotNet and k6.
- Migration/restore: N-2 upgrade and disaster restoration.

### Requirement mapping

| Requirement | Acceptance evidence |
|---|---|
| .NET 10 Minimal APIs | All projects target `net10.0`; OpenAPI build succeeds |
| Local/Azure/S3 storage | Same conformance suite passes all adapters/profiles |
| Direct uploads | Azure/S3 bytes bypass API; completion independently verifies object |
| SQLite/PostgreSQL | Behavioral API suite passes both providers |
| Dynamic transforms | Canonical preset creates deterministic URL/bytes |
| Immutable CDN cache | Same URL never changes and returns one-year immutable header |
| API key/JWT | Scope, revocation, claim, algorithm, audience, and tenant tests pass |
| Configurable limits | Boundary tests reject encoded, pixel, frame, time, and quota excess |
| Docker | Starter and PostgreSQL Compose smoke tests become ready |
| Gallery | Upload, browse, search, organize, share, trash, restore workflows pass E2E |
| AI safety | Model-provider test double cannot invoke storage mutation or purge |

### Measurable release criteria

- 100% of activated originals have canonical SHA-256.
- No public API buffers an entire upload into `byte[]`.
- Ten concurrent identical derivative misses store exactly one result.
- Cross-tenant tests deny every list/read/write/download/share/delete/restore route.
- Revoked API keys fail within 60 seconds.
- Public derivatives contain none of the GPS/EXIF/IPTC/XMP privacy fixtures.
- Malformed/bomb corpus causes no process crash or container OOM.
- Worker termination at each job stage loses no job and exposes no partial derivative.
- Abandoned staging objects/multipart sessions are removed within 24 hours.
- Warm management GET p95 ≤200 ms on the documented reference host.
- Cold 2 MP JPEG to 1200 px WebP p95 ≤750 ms.
- CDN-warm public derivative p95 TTFB ≤100 ms in the tested region.
- SQLite profile exposes no `SQLITE_BUSY` errors under its documented load.
- WCAG automated testing has zero serious/critical findings, with manual primary-flow passes.
- Trash restore succeeds with preserved metadata and relationships.
- Restore drills meet declared RPO/RTO.

---

## 16. Ordered implementation roadmap

Tasks may edit only their listed ownership paths. Shared root, composition, and generated files are reserved for integration tasks.

**Status legend.** Each wave below carries a status line. *Complete* means the
wave's outcomes are implemented on `dev`, staged in pull request #2 for the
0.1.0 tag, and covered by verification commands that are part of the
repository's checks. *Not started* means no code for that wave exists. Waves 0A
through 7 are complete; Wave 8 is not started.

### Wave 0A — repository bootstrap

**Status: complete.** The Apache-2.0 solution, central package and analyzer settings, and project graph are in place.

| ID | Size | Dependencies | Ownership | Outcome | Verification |
|---|---|---|---|---|---|
| `BOOT-01` | M | None | Root files, empty project files, `LICENSE` | Public Apache-2.0 solution, central package/version/analyzer settings, project graph | `dotnet restore Vistara.slnx && dotnet build Vistara.slnx -c Release --no-restore` |

### Wave 0B — first fleet dispatch

**Status: complete.** Primitives, contracts, the web shell, workflow and container skeletons, and dependency-rule tests all exist.

Run in parallel after `BOOT-01`.

| ID | Size | Ownership | Outcome | Verification |
|---|---|---|---|---|
| `BOOT-02` | S | `src/Vistara.Domain/Common/**`, `src/Vistara.Application/Common/**` | UUIDv7, clocks, result/error primitives, cancellation conventions | `dotnet test tests/Vistara.UnitTests --filter Common` |
| `BOOT-03` | S | `src/Vistara.Contracts/**` | API envelope, Problem Details, cursor and ETag contracts | `dotnet build src/Vistara.Contracts -c Release` |
| `BOOT-04` | M | `src/Vistara.Web/**` | React/Vite/Router/Query shell, lint/type/test/build scripts | `npm --prefix src/Vistara.Web ci && npm --prefix src/Vistara.Web run check` |
| `BOOT-05` | S | `.github/**`, `deploy/containers/**` | Build/test/security workflow skeleton and container build | `docker build -f deploy/containers/api.Dockerfile .` |
| `BOOT-06` | S | `tests/Vistara.ArchitectureTests/**` | Dependency-rule tests | `dotnet test tests/Vistara.ArchitectureTests -c Release` |

`BOOT-07` integrates and runs:

```bash
dotnet format Vistara.slnx --verify-no-changes &&
dotnet build Vistara.slnx -c Release &&
dotnet test Vistara.slnx -c Release --no-build &&
npm --prefix src/Vistara.Web run build
```

### Wave 1 — domain and persistence

**Status: complete.** Domain slices, EF Core persistence with tenant filters, and both provider migration assemblies ship with parity tests.

Parallel domain slices:

| ID | Size | Dependencies | Ownership | Verification |
|---|---|---|---|---|
| `CORE-01` | M | BOOT-02 | `Domain/Tenancy`, `Domain/Identity`, matching Application folders/tests | `dotnet test tests/Vistara.UnitTests --filter Tenancy` |
| `CORE-02` | M | BOOT-02 | `Domain/Assets`, `Domain/Uploads`, matching Application folders/tests | `dotnet test tests/Vistara.UnitTests --filter "Assets|Uploads"` |
| `CORE-03` | M | BOOT-02 | `Domain/Gallery`, `Domain/Sharing`, `Domain/Lifecycle` | `dotnet test tests/Vistara.UnitTests --filter "Gallery|Sharing|Lifecycle"` |
| `CORE-04` | M | BOOT-02 | `Domain/Jobs`, Application job/audit/outbox ports | `dotnet test tests/Vistara.UnitTests --filter Jobs` |

Then:

| ID | Size | Dependencies | Ownership | Verification |
|---|---|---|---|---|
| `CORE-05` | M | CORE-01..04 | `src/Vistara.Persistence/**` | DbContext, mappings, tenant filters, indexes | `dotnet test tests/Vistara.IntegrationTests --filter Persistence` |
| `CORE-06` | M | CORE-05 | Both migration projects | Initial SQLite/PostgreSQL migrations | `dotnet ef migrations list --project src/Vistara.Migrations.Sqlite && dotnet ef migrations list --project src/Vistara.Migrations.Postgres` |
| `CORE-07` | S | CORE-06 | `tests/Vistara.MigrationTests/**` | Fresh install and provider parity tests | `dotnet test tests/Vistara.MigrationTests -c Release` |

### Wave 2 — storage and imaging

**Status: complete.** The blob and image ports, the local, S3, and Azure adapters, the NetVips processor, and startup capability validation are implemented and covered by conformance tests.

First establish stable ports/conformance tests, then parallelize adapters.

| ID | Size | Dependencies | Ownership | Verification |
|---|---|---|---|---|
| `MEDIA-01` | M | CORE-02 | Application blob/image ports; base conformance fixtures | `dotnet test tests/Vistara.Storage.ConformanceTests --filter Contract` |
| `MEDIA-02` | M | MEDIA-01 | `src/Vistara.Storage.Local/**` | Durable local adapter | `dotnet test tests/Vistara.Storage.ConformanceTests --filter Local` |
| `MEDIA-03` | M | MEDIA-01 | `src/Vistara.Storage.S3/**` | AWS/R2/B2/MinIO profiles | `dotnet test tests/Vistara.Storage.ConformanceTests --filter Minio` |
| `MEDIA-04` | M | MEDIA-01 | `src/Vistara.Storage.Azure/**` | Azure Blob adapter and SAS plans | `dotnet test tests/Vistara.Storage.ConformanceTests --filter Azurite` |
| `MEDIA-05` | M | MEDIA-01 | `src/Vistara.Imaging.NetVips/**`, imaging tests | Inspection, transforms, fingerprint, metadata stripping | `dotnet test tests/Vistara.Imaging.Tests -c Release` |
| `MEDIA-06` | S | MEDIA-02..05 | API/Worker storage/imaging composition files only | Capability registration and startup validation | `dotnet test tests/Vistara.IntegrationTests --filter AdapterComposition` |

### Wave 3 — auth, quotas, jobs, and events

**Status: complete.** Cookie sessions, API keys, configured-issuer JWT validation, tenant authorization, quotas, the durable job queue, the transactional outbox, and the event stream are implemented.

Parallel ownership by subfolder; no task edits shared registration files.

| ID | Size | Dependencies | Ownership | Verification |
|---|---|---|---|---|
| `PLAT-01` | M | CORE-01, CORE-05 | `Vistara.Auth/Cookies/**` | Local identity, session cookies, antiforgery | `dotnet test tests/Vistara.IntegrationTests --filter CookieAuth` |
| `PLAT-02` | S | CORE-01 | `Vistara.Auth/ApiKeys/**` | Key creation, hashing, scopes, revocation | `dotnet test tests/Vistara.IntegrationTests --filter ApiKeys` |
| `PLAT-03` | S | CORE-01 | `Vistara.Auth/Jwt/**` | Configured issuer JWT validation | `dotnet test tests/Vistara.IntegrationTests --filter Jwt` |
| `PLAT-04` | M | CORE-01..03 | `Application/Tenancy/Authorization/**`, `Application/Uploads/Quotas/**` | Tenant policies and reservations | `dotnet test tests/Vistara.UnitTests --filter "Authorization|Quota"` |
| `PLAT-05` | M | CORE-04, CORE-05 | `Persistence/Jobs/**`, Worker job runtime | Durable leasing/retry/dead-letter | `dotnet test tests/Vistara.IntegrationTests --filter JobQueue` |
| `PLAT-06` | M | CORE-04, CORE-05 | `Persistence/Outbox/**`, `Api/Features/Events/**` | Transactional outbox and SSE replay | `dotnet test tests/Vistara.IntegrationTests --filter "Outbox|Events"` |
| `PLAT-07` | S | PLAT-01..06 | Auth/job composition files | Integrated policies and middleware order | `dotnet test tests/Vistara.Api.ContractTests --filter Authentication` |

### Wave 4 — upload and ingest vertical slice

**Status: complete.** Upload intent, proxy and multipart uploads, commit and abort, worker ingest with hash and decode verification, exact-duplicate detection, and upload reconciliation are implemented.

| ID | Size | Dependencies | Ownership | Verification |
|---|---|---|---|---|
| `INGEST-01` | M | MEDIA-01..04, PLAT-04 | `Api/Features/Uploads/**`, contracts | Intent, proxy upload, parts, commit, abort endpoints | `dotnet test tests/Vistara.Api.ContractTests --filter Uploads` |
| `INGEST-02` | M | MEDIA-05, PLAT-05 | `Worker/Features/Ingest/**` | HEAD/hash/decode verification and promotion | `dotnet test tests/Vistara.IntegrationTests --filter IngestWorker` |
| `INGEST-03` | S | INGEST-02 | `Application/Assets/Ingest/**` | Exact duplicates and asset/revision transaction | `dotnet test tests/Vistara.UnitTests --filter AssetIngest` |
| `INGEST-04` | S | INGEST-01, PLAT-05 | `Worker/Features/Reconciliation/Uploads/**` | Expiry, multipart abort, orphan cleanup | `dotnet test tests/Vistara.IntegrationTests --filter UploadReconciliation` |
| `INGEST-05` | M | INGEST-01..04 | Upload integration/E2E tests only | Local, MinIO, Azurite complete flows | `dotnet test tests/Vistara.IntegrationTests --filter UploadEndToEnd` |

### Wave 5 — derivatives and delivery

**Status: complete.** Canonical recipes, checkpointed worker generation, immutable public and private delivery, the preset API, and revocable delivery grants are implemented.

| ID | Size | Dependencies | Ownership | Verification |
|---|---|---|---|---|
| `DERIV-01` | M | MEDIA-05, CORE-02 | `Application/Derivatives/**` | Canonical recipes, preset revisions, hashes | `dotnet test tests/Vistara.UnitTests --filter Derivatives` |
| `DERIV-02` | M | DERIV-01, PLAT-05 | `Worker/Features/Derivatives/**` | Deduplicated generation and conditional publication | `dotnet test tests/Vistara.IntegrationTests --filter DerivativeWorker` |
| `DERIV-03` | M | DERIV-02 | `Api/Features/Media/**` | HEAD, ETag, Range, cache headers | `dotnet test tests/Vistara.Api.ContractTests --filter MediaDelivery` |
| `DERIV-04` | S | DERIV-01 | `Api/Features/Derivatives/**` | Preset request/list API | `dotnet test tests/Vistara.Api.ContractTests --filter Derivatives` |
| `DERIV-05` | S | DERIV-03 | `Auth/Delivery/**` | Private/revocable media grants | `dotnet test tests/Vistara.IntegrationTests --filter DeliveryGrants` |
| `DERIV-06` | M | DERIV-02..05 | Media performance/security tests | Stampede, immutability, cache, metadata corpus | `dotnet test tests/Vistara.Imaging.Tests && dotnet test tests/Vistara.IntegrationTests --filter DerivativeConcurrency` |

### Wave 6 — gallery and lifecycle

**Status: complete.** The asset, curation, sharing, and lifecycle APIs and the library, viewer, upload, curation, share, trash, settings, and administration screens are implemented, with browser workflows covered by the gallery smoke suite.

Backend tasks may run in parallel:

| ID | Size | Ownership | Verification |
|---|---|---|---|
| `GAL-01` | M | `Application/Gallery/Queries/**`, `Api/Features/Assets/**` | `dotnet test tests/Vistara.Api.ContractTests --filter AssetQueries` |
| `GAL-02` | M | Album/tag/favorite application and API folders | `dotnet test tests/Vistara.Api.ContractTests --filter "Albums|Tags|Favorites"` |
| `GAL-03` | M | Sharing application/API/auth folders | `dotnet test tests/Vistara.IntegrationTests --filter Shares` |
| `GAL-04` | M | Lifecycle application/API/worker folders | `dotnet test tests/Vistara.IntegrationTests --filter "Trash|Purge"` |

Frontend feature tasks run in parallel with those backend tasks against the reviewed OpenAPI contract:

| ID | Size | Ownership | Verification |
|---|---|---|---|
| `WEB-01` | M | `Web/src/features/library/**`, `viewer/**` | `npm --prefix src/Vistara.Web run test -- --run library` |
| `WEB-02` | M | `Web/src/features/uploads/**` | `npm --prefix src/Vistara.Web run test -- --run uploads` |
| `WEB-03` | M | `Web/src/features/albums/**`, `tags/**`, `favorites/**` | `npm --prefix src/Vistara.Web run test -- --run curation` |
| `WEB-04` | M | `Web/src/features/shares/**`, `trash/**` | `npm --prefix src/Vistara.Web run test -- --run "shares|trash"` |
| `WEB-05` | S | `Web/src/accessibility/**`, shared a11y tests | `npm --prefix src/Vistara.Web run test:a11y` |

Integration:

```bash
dotnet build src/Vistara.Api -c Release &&
npm --prefix src/Vistara.Web run api:check &&
npm --prefix src/Vistara.Web run build &&
npm --prefix tests/Vistara.E2E ci &&
npm --prefix tests/Vistara.E2E run test
```

### Wave 7 — release hardening

**Status: complete.** Security middleware, health and telemetry, Compose topologies, migration and backup tooling, security and performance suites, provider live-test workflows, and the release documentation set are in place.

| ID | Size | Ownership | Verification |
|---|---|---|---|
| `OPS-01` | M | API security middleware/configuration | `dotnet test tests/Vistara.IntegrationTests --filter SecurityHeaders` |
| `OPS-02` | M | `src/Vistara.Observability/**`, health tests | `dotnet test tests/Vistara.IntegrationTests --filter Health` |
| `OPS-03` | M | `deploy/**` excluding CI | `docker compose -f deploy/compose.starter.yml config && docker compose -f deploy/compose.postgres.yml config` |
| `OPS-04` | M | Migration/backup tooling and tests | `dotnet test tests/Vistara.MigrationTests -c Release` |
| `OPS-05` | M | Security test suites | `dotnet test tests/Vistara.IntegrationTests --filter Security` |
| `OPS-06` | M | Performance tests | `dotnet run -c Release --project tests/Vistara.PerformanceTests` |
| `OPS-07` | S | Provider live-test workflows | Scheduled AWS/Azure/R2/B2 workflow dry-run/config validation |
| `OPS-08` | M | README, deployment, security, backup, API documentation | Validate documented starter install from a clean checkout |

### Wave 8 — post-MVP AI

**Status: not started.** No task in this wave has been implemented, and none is scheduled. Nothing in the codebase should be read as partial AI support.

| ID | Size | Dependencies | Ownership | Verification |
|---|---|---|---|---|
| `AI-01` | M | Core lifecycle | AI contracts, consent, model/artifact tables | `dotnet test tests/Vistara.UnitTests --filter AiContracts` |
| `AI-02` | M | AI-01 | Local tag/OCR/caption adapter | Evaluation corpus command with versioned results |
| `AI-03` | M | AI-01 | Embedding spaces and search adapter | Recall and cross-tenant isolation suite |
| `AI-04` | M | AI-03 | Duplicate/sorting review | Candidate precision evaluation |
| `AI-05` | M | AI-01, lifecycle | Action proposals and deterministic dry runs | Prompt-injection suite proves zero direct mutation |
| `AI-06` | M | AI-01 | Hosted consent/budget/provider adapters | Consent, retention, circuit-breaker, budget tests |
| `AI-07` | M | AI-02..06 | Gallery review UI | Playwright accept/edit/reject/dry-run/undo tests |

---

## 17. Risks, alternatives, rollout, and open questions

### Principal risks

| Risk | Mitigation |
|---|---|
| Native libvips packaging | Digest-pinned glibc images, x64/arm64 CI, SBOM and license notices |
| Decoder vulnerability/OOM | Allowlisted codecs, preflight, limits, patched worker, container bounds |
| S3-compatible drift | Capability flags and shared live-provider conformance suite |
| DB/object non-atomicity | Immutable writes, idempotency, outbox, reconciliation |
| SQLite contention | Embedded single worker, local disk, documented PostgreSQL upgrade path |
| CDN revocation limits | Separate permanent-public and revocable delivery contracts |
| Search provider differences | Capability reporting; no false parity between FTS5/PostgreSQL |
| Scope expansion into photo suite | Preserve focused library/albums/tags/share/trash scope |
| Migration divergence | Separate migration assemblies and N-2 provider tests |
| AI privacy/licensing | Opt-in adapters, model registry, provenance, consent, no mutation capability |

### Alternatives rejected

- **ImageSharp as baseline:** managed portability does not outweigh split-license/build-key friction and higher server-memory risk.
- **imgproxy/Imagor baseline:** unnecessary extra service boundary for MVP.
- **Microservices:** premature operational complexity.
- **Redis/RabbitMQ initially:** DB-backed jobs satisfy expected workload.
- **Blazor WASM:** React ecosystem better fits gallery virtualization and PWA needs.
- **SSR authenticated gallery:** little SEO value and more complexity.
- **Mutable media URLs:** incompatible with reliable immutable caching.
- **Arbitrary URL transforms:** difficult to authorize, bound, and cache safely.
- **Public-by-default media:** unacceptable privacy and revocation behavior.
- **AI automatic deletion:** conflicts with safety, auditability, and recovery.

### Rollout

1. Internal alpha: local storage + SQLite + core ingest/derivatives.
2. Alpha: MinIO/S3 and integrated gallery.
3. Beta: PostgreSQL, Azure, AWS/R2/B2 profiles, shares and lifecycle.
4. Release candidate: security/load/accessibility/provider tests and restore drill.
5. Stable 0.1: signed artifacts, SBOM, migration bundles, documented rollback.
6. Post-0.1 features are gated by capabilities and tenant feature flags.

### Rollback

- Deploy only backward-compatible expand migrations before application rollout.
- Roll back API/worker while retaining added columns/tables.
- Never mutate existing derivative bytes; select the prior pipeline generation.
- Disable problematic provider profiles without altering stored records.
- Retain prior AI embedding/model generations until validation completes.
- Pausing purge workers immediately halts physical deletion.
- Restore trashed assets without changing stable asset IDs.

### Nonblocking product-owner questions

1. Should the 0.1 UI expose multiple-tenant creation, or retain tenant provisioning as administrator API/configuration only?
2. Should explicit permanent-public asset publishing appear in 0.1, or should the UI initially expose only revocable share links?
3. Which optional AI hardware/provider profile should be prioritized after 0.1: CPU-local, NVIDIA-local, or hosted?
4. Is a Helm deployment an early post-MVP priority, or should official support remain Docker Compose first?

None block repository bootstrap, schema design, storage, processing, or gallery implementation.

---

## 18. Authoritative source index

### .NET and databases

- https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
- https://learn.microsoft.com/en-us/aspnet/core/fundamentals/apis?view=aspnetcore-10.0
- https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/aspnetcore-openapi?view=aspnetcore-10.0
- https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew
- https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/providers
- https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations
- https://www.sqlite.org/wal.html
- https://www.postgresql.org/docs/current/ddl-rowsecurity.html
- https://www.postgresql.org/docs/current/sql-select.html#SQL-FOR-UPDATE-SHARE

### Storage

- https://docs.aws.amazon.com/AmazonS3/latest/userguide/using-presigned-url.html
- https://docs.aws.amazon.com/AmazonS3/latest/userguide/checking-object-integrity-upload.html
- https://docs.aws.amazon.com/AmazonS3/latest/userguide/qfacts.html
- https://learn.microsoft.com/en-us/azure/storage/blobs/sas-service-create-dotnet
- https://learn.microsoft.com/en-us/azure/storage/blobs/concurrency-manage
- https://developers.cloudflare.com/r2/api/s3/api/
- https://developers.cloudflare.com/r2/api/s3/presigned-urls/
- https://developers.cloudflare.com/r2/reference/consistency/
- https://www.backblaze.com/docs/cloud-storage-s3-compatible-api

### Imaging and caching

- https://github.com/kleisauke/net-vips/releases
- https://github.com/libvips/libvips/blob/master/README.md
- https://raw.githubusercontent.com/libvips/libvips/master/LICENSE
- https://sixlabors.com/posts/licence-enforcement-changes/
- https://raw.githubusercontent.com/SixLabors/ImageSharp/main/LICENSE
- https://docs.sixlabors.com/articles/imagesharp/security.html
- https://www.rfc-editor.org/rfc/rfc8246.html
- https://www.rfc-editor.org/rfc/rfc9111.html

### Security and accessibility

- https://cheatsheetseries.owasp.org/cheatsheets/File_Upload_Cheat_Sheet.html
- https://owasp.org/API-Security/editions/2023/en/0xa1-broken-object-level-authorization/
- https://www.rfc-editor.org/rfc/rfc8725.html
- https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0
- https://www.w3.org/TR/WCAG22/
- https://www.w3.org/WAI/ARIA/apg/patterns/grid/
- https://www.w3.org/WAI/ARIA/apg/patterns/dialog-modal/

### AI

- https://www.nist.gov/itl/ai-risk-management-framework
- https://cheatsheetseries.owasp.org/cheatsheets/LLM_Prompt_Injection_Prevention_Cheat_Sheet.html
- https://eur-lex.europa.eu/eli/reg/2024/1689/oj
- https://github.com/openai/CLIP
- https://huggingface.co/google/siglip2-base-patch16-224
- https://onnxruntime.ai/docs/execution-providers/
