# Vistara

Vistara is an open-source, self-hosted image control plane and responsive
gallery. It stores immutable originals in local or object storage, verifies
direct uploads, produces deterministic derivatives in a background worker, and
serves a browsable, installable gallery from the same origin as its API.

The 0.1 scope in [`docs/specification.md`](docs/specification.md) is
implemented: API, worker, SQLite and PostgreSQL persistence, local/Azure
Blob/S3-compatible storage, NetVips imaging, the React gallery, sharing,
lifecycle, administration, observability, container images, and Compose
topologies. The ideas in [`docs/future-ideas/`](docs/future-ideas/README.md) —
AI metadata and editing, an MCP server, and cloud imports — are **not
implemented and not committed to a release**.

Licensed under the [Apache License 2.0](LICENSE).

## Contents

- [What Vistara does](#what-vistara-does)
- [Quick start: first owner on one host](#quick-start-first-owner-on-one-host)
- [Other Compose topologies](#other-compose-topologies)
- [Build, test, and validate](#build-test-and-validate)
- [Configuration](#configuration)
- [Screenshots](#screenshots)
- [Documentation](#documentation)
- [Repository layout](#repository-layout)
- [Current limitations](#current-limitations)

## What Vistara does

### HTTP API

An ASP.NET Core Minimal API under `/api/v1` covering:

| Area | Routes |
|---|---|
| First-run setup | `GET/POST /api/v1/setup` |
| Session and account | `POST /api/v1/auth/login`, `POST /api/v1/auth/logout`, `GET /api/v1/me`, `GET/PATCH /api/v1/me/preferences` |
| Assets and search | `GET /api/v1/assets`, `GET/PATCH /api/v1/assets/{id}`, `POST /api/v1/assets/bulk`, `GET /api/v1/assets/{id}/metadata`, `GET /api/v1/timeline`, `GET /api/v1/search/facets` |
| Curation | `/api/v1/albums`, `/api/v1/tags`, `/api/v1/assets/{id}/favorite` |
| Uploads | `POST /api/v1/uploads`, `PUT /api/v1/uploads/{id}/content`, `POST /api/v1/uploads/{id}/parts`, `POST /api/v1/uploads/{id}/commit`, `DELETE /api/v1/uploads/{id}` |
| Derivatives | `GET /api/v1/derivative-presets`, `GET/POST /api/v1/assets/{id}/derivatives` |
| Delivery | `GET` and `HEAD` on `/media/{pipeline}/{sourceHash}/{recipeHash}.{ext}`, `/delivery/{pipeline}/{sourceHash}/{recipeHash}.{ext}`, `/delivery/assets/{assetId}/{renditionId}`, and `/api/v1/assets/{id}/original` |
| Sharing | `/api/v1/shares`, `GET /api/v1/public/shares/{token}`, `POST /api/v1/public/shares/{token}/challenge`, `GET` and `HEAD` on `/api/v1/public/shares/{token}/assets/{assetId}/renditions/{renditionId}` |
| Lifecycle | `GET /api/v1/trash`, `POST /api/v1/trash/restore`, `POST /api/v1/trash/purge`, `POST /api/v1/trash/purge/{batchId}/confirm` |
| Jobs | `GET /api/v1/jobs`, `GET /api/v1/jobs/{id}`, `POST /api/v1/jobs/{id}/retry`, `POST /api/v1/jobs/{id}/cancel` |
| Tenancy and keys | `/api/v1/tenants`, `/api/v1/tenants/{id}/members`, `/api/v1/api-keys` |
| Administration | `GET /api/v1/admin/storage`, `GET/PATCH /api/v1/admin/policies`, `GET/POST /api/v1/admin/storage/validate`, `GET /api/v1/admin/audit` |
| Platform | `GET /api/v1/capabilities`, `GET /api/v1/events` |
| Health and contract | `GET /health/live`, `/health/ready`, `/health/startup`, `GET /openapi/gallery-v1.json` |

Cross-cutting behavior: authentication by browser session cookie, `X-API-Key`,
or configured-issuer JWT, with simultaneous credentials rejected rather than
silently resolved; per-request tenant pinning; role-derived scopes; strong
`ETag`/`If-Match` concurrency on mutations; `Idempotency-Key` on creation and
command routes; cursor pagination; Problem Details errors; per-IP rate limiting;
CSP, HSTS, and other response hardening; and relational, tenant-scoped audit
records.

### Background worker

A separate `Vistara.Worker` process runs durable jobs — `upload.ingest`,
`asset.derivative.generate`, `lifecycle.restore`, `lifecycle.purge` — plus
upload, blob-integrity, derivative, and purge reconciliation. Jobs are leased,
heartbeated, retried with jittered exponential backoff, and dead-lettered after
the attempt limit; a transactional outbox advances event publication in the same
database. Ingest verifies staged bytes with SHA-256, enforces decode limits,
detects exact duplicates, promotes the original to its canonical key, and
enqueues standard derivatives. Derivative publication is checkpointed and
recoverable.

### Storage and imaging

Local filesystem, Azure Blob Storage, and S3-compatible storage with AWS,
Cloudflare R2, Backblaze B2, and MinIO profiles. Azure and S3 support direct and
multipart uploads and signed reads; local storage uses proxy uploads. Imaging
uses NetVips/libvips: JPEG, PNG, and WebP in and out, EXIF-orientation
correction, sRGB and ICC handling, Lanczos3 resizing, deterministic encoding,
and metadata stripping. Standard presets are `thumb`, `grid`, `viewer`, and
`download-web`.

### Gallery web application

A React 19 + Vite single-page app served from the API image, with routes for the
library timeline, search, asset viewer, uploads, albums, tags, favorites, share
links, trash, settings, first-run setup, sign-in, the public shared-gallery view
at `/s/:token`, and administration at `/admin/users`, `/admin/storage`,
`/admin/jobs`, `/admin/policies`, and `/admin/audit`. The upload queue persists
in IndexedDB and survives reloads; administration screens are lazy-loaded only
after an authorization guard admits the user.

The app is installable: it ships a web manifest and a narrowly scoped service
worker that precaches only the application shell and brand assets and explicitly
refuses to cache API, media, delivery, share, health, and metrics responses. The
shell opens offline; content still requires connectivity, and an online/offline
indicator reflects that.

### Setup, administration, and cloud onboarding

`/setup` provisions the first workspace and its owner exactly once, then signs
that owner in. Administration exposes storage usage and health, tenant quotas
and policies, job administration, the audit log, and a cloud storage onboarding
assistant that tests candidate filesystem, Azure Blob, or S3 credentials against
the real provider and generates deployment configuration. Submitted credentials
are request-scoped, redacted, and never persisted; applying a provider is a
server configuration change followed by an API and worker restart. See
[Administration and cloud onboarding](docs/security/admin-and-cloud-onboarding.md).

### Observability and operations

OpenTelemetry traces, metrics, and logs export to one OTLP endpoint when
`Telemetry:OtlpEndpoint` is set and are inert when it is not. The API exposes
liveness, readiness, and startup health endpoints that probe configuration,
migrations, database, schema, storage, queue, and imaging without disclosing
versions, credentials, SQL, or topology. Migration bundles, backup and restore
scripts, and release, rollback, and security runbooks ship with the repository.

## Quick start: first owner on one host

The starter topology runs SQLite and local media on one host. It needs Docker
with Compose v2, `openssl`, and a free `127.0.0.1:8080`.

```bash
git clone https://github.com/<owner>/Vistara.git
cd Vistara
./deploy/generate-env.sh deploy/.env
docker compose --env-file deploy/.env -f deploy/compose.starter.yml up --build -d
```

`generate-env.sh` writes a git-ignored, mode-`0600` file with a random API key
pepper and random database and object-storage passwords. Its OIDC values are
`issuer.example.invalid` placeholders; local owner setup and password sign-in do
not depend on them, but replace them before enabling JWT login.

Wait for the services, then confirm health:

```bash
docker compose --env-file deploy/.env -f deploy/compose.starter.yml ps
curl --fail http://127.0.0.1:8080/health/live
curl --fail http://127.0.0.1:8080/health/ready
curl --fail http://127.0.0.1:8080/health/startup
```

Open <http://127.0.0.1:8080/setup> and create the first workspace and owner:

1. Enter a workspace name and address, the owner's name and email, and a
   password of at least 12 characters.
2. Submit. Vistara creates the tenant, the owner membership, and a
   `tenant.owner.provisioned` audit record, then signs the owner in and opens
   `/library`. If the automatic sign-in does not complete, sign in at
   <http://127.0.0.1:8080/login>.
3. Upload images at `/uploads`. The worker ingests them and generates
   derivatives; `/admin/jobs` shows progress.

Setup is one-time. Once an owner exists, `GET /api/v1/setup` reports that
provisioning is unavailable and further attempts are rejected. Add further
people from `/admin/users`.

Stop the instance, keeping data:

```bash
docker compose --env-file deploy/.env -f deploy/compose.starter.yml down
```

Add `--volumes` only when you intend to delete the database and media.

These bundled topologies are HTTP-only and bound to loopback on purpose.
Terminate TLS at a reviewed edge and re-enable
`Security__Transport__RedirectHttpToHttps` before serving user traffic.

## Other Compose topologies

```bash
# PostgreSQL 18, MinIO, separately credentialed database roles
docker compose --env-file deploy/.env -f deploy/compose.postgres.yml up --build

# Development: PostgreSQL, MinIO, Vite dev server, local OTLP collector
docker compose --env-file deploy/.env -f deploy/compose.development.yml up --build

# Validate configuration without starting anything
docker compose --env-file deploy/.env -f deploy/compose.starter.yml config
docker compose --env-file deploy/.env -f deploy/compose.postgres.yml config
docker compose --env-file deploy/.env -f deploy/compose.development.yml config
```

Only the reverse proxy publishes a host port. Topology detail, network trust,
and the fixed proxy addresses are documented in
[`deploy/README.md`](deploy/README.md).

## Build, test, and validate

Prerequisites: the .NET SDK feature band pinned in `global.json` (10.0.400,
rolling forward to the latest feature band), Node.js 24 (the web app requires
`^22.13.0 || >=24.0.0`), Docker for container and Compose validation, and
`libvips` when running imaging tests outside a container.

.NET, as CI runs it:

```bash
dotnet restore Vistara.slnx
dotnet build Vistara.slnx --configuration Release --no-restore
dotnet test Vistara.slnx --configuration Release --no-build --no-restore
```

Web application:

```bash
npm --prefix src/Vistara.Web ci
npm --prefix src/Vistara.Web run check
```

`check` runs `api:check`, `lint`, `typecheck`, `test -- --run`, and `build` in
that order. Individual scripts are `dev`, `build`, `build:pages`, `lint`,
`typecheck`, `test` (Vitest; append `-- --run` for a single pass),
`api:generate`, `api:check`, and `brand:generate`.

Repository tooling:

```bash
npm ci --prefix eng
node eng/validate-shadow.mjs
node eng/validate-agent-workflows.mjs
node --test eng/tests/*.test.mjs
```

Browser smoke tests and the performance harness:

```bash
npm --prefix tests/Vistara.E2E ci
npm --prefix tests/Vistara.E2E run install:browsers
npm --prefix tests/Vistara.E2E run test
dotnet run -c Release --project tests/Vistara.PerformanceTests
```

Container and deployment gates:

```bash
./deploy/containers/tests/context.sh
docker build -f deploy/containers/api.Dockerfile .
docker build -f deploy/containers/worker.Dockerfile .
docker build -f deploy/containers/migration.Dockerfile .
```

`npm --prefix src/Vistara.Web run build:pages` creates a GitHub Pages artifact
under `src/Vistara.Web/dist-pages`, separate from the production bundle in
`src/Vistara.Web/dist`. The Pages artifact is a static preview only: it renders
placeholder routes and has no API, authentication, uploads, persistence, or
worker processing.

## Configuration

The API and worker read standard .NET configuration and ship no `appsettings`
defaults, so every deployment supplies its own values — in environment variables
`:` becomes `__`. Startup validation fails fast on missing or contradictory
settings.

| Setting | Values | Notes |
|---|---|---|
| `Persistence:Provider` | `Sqlite`, `PostgreSql` | Required |
| `ConnectionStrings:Vistara` | connection string | Falls back to `Persistence:ConnectionString` |
| `Media:Storage:Provider` | `Local`, `S3`, `Azure` | Exactly one matching `Media:Storage:*` subsection must be configured |
| `Media:Storage:Local:RootPath` | absolute path | Dedicated directory |
| `Media:Storage:S3:*` | `Profile`, `CredentialMode`, `BucketName`, `Region`, `ServiceUrl`, `ForcePathStyle`, `AllowInsecureHttp`, `AllowedEndpointHosts`, `AccessKeyId`, `SecretAccessKey`, `SessionToken`, `MaximumPresignLifetime` | `Profile` is `Aws`, `CloudflareR2`, `BackblazeB2`, or `Minio`; `CredentialMode` is `DefaultChain` or `Static` |
| `Media:Storage:Azure:*` | `AccountName`, `ContainerName`, `ServiceUri`, `EmulatorMode`, `CredentialMode`, `ManagedIdentityClientId`, `AllowDefaultCredentialOutsideDevelopment`, `ConnectionString`, `AllowSharedKeySas`, `MaximumGrantLifetime` | `CredentialMode` is `ManagedIdentity`, `SharedKey`, or `DefaultCredential`. `ManagedIdentity` requires `ManagedIdentityClientId`, the client ID of a **user-assigned** managed identity as a hyphenated GUID; a system-assigned identity is not supported. `SharedKey` requires `ConnectionString` and `AllowSharedKeySas`. `DefaultCredential` is for local development only and is rejected outside a `Development` environment unless `AllowDefaultCredentialOutsideDevelopment` is set as a reviewed exception |
| `Media:Imaging:Provider` | `NetVips` | Required |
| `Platform:Authentication:ApiKeys:*` | `CurrentPepperVersion`, `Peppers:<version>` | Base64 peppers; required |
| `Platform:Authentication:Jwt:Issuers` | `ProfileId`, `Issuer`, `Audience`, `MetadataAddress`, `AllowedAlgorithms`, `AllowedTypes` | At least one issuer entry is required at startup even when only local password sign-in is used |
| `Security:*` | `Cors`, `Hosts`, `Limits`, `Proxy`, `Transport`, `RequiredSecretKeys` | Defaults deny cross-origin requests, allow only loopback hosts, cap the body at 50 MiB, and rate-limit 300 requests per minute per address |
| `Worker:*` | `InstanceId`, `Jobs`, `Outbox`, `ImagingLimits`, `Health`, `DerivativeOwnershipDuration` | Imaging limits are hard ceilings that configuration cannot raise |
| `Telemetry:*` | `Enabled`, `ServiceName`, `ServiceVersion`, `ServiceInstanceId`, `OtlpEndpoint`, `SamplingRatio`, `Tracing`, `Metrics`, `Logging` | With no endpoint, no exporter is registered |

Migrations are never applied automatically. Run the migration image, or the
matching migration bundle, to completion before starting the API and worker;
both Compose examples express this with
`depends_on: { migrate: { condition: service_completed_successfully } }`.

Running `dotnet run --project src/Vistara.Api` outside Compose therefore requires
supplying persistence, media, platform authentication, and imaging configuration
yourself, and running the migration bundle first. Compose is the supported local
path.

## Screenshots

None are committed yet, and none are required to tag a release. Gallery,
viewer, upload, share, and administration screenshots are captured as review
evidence on the release pull request. Committing a set here later is optional
follow-up work; when it happens, store the images under a `docs/` asset
directory rather than linking to external hosts.

## Documentation

| Document | Read it when |
|---|---|
| [Product specification](docs/specification.md) | You need the product, architecture, acceptance, and roadmap authority |
| [Release notes](docs/release-notes.md) | You want the user-visible change history |
| [Operations index](docs/operations/README.md) | You run an instance |
| [Deployment topologies](deploy/README.md) | You are choosing or changing a Compose topology |
| [Administration and cloud onboarding](docs/security/admin-and-cloud-onboarding.md) | You are creating the first owner, granting administration, or moving to cloud storage |
| [Security operations](docs/security/security-operations.md) | You are reviewing scanning coverage, handling an advisory, or rotating credentials |
| [Backup and restore](docs/operations/backup-and-restore.md) | You are scheduling backups or running a restore drill |
| [Release, migration, and rollback runbook](docs/operations/release-runbook.md) | You are publishing, migrating, or rolling back a release |
| [Azure free credits](docs/operations/azure-free-credits.md) | You are evaluating Vistara on Microsoft Azure free-credit offers |
| [Azure identity, RBAC, and secrets](docs/operations/azure-identity-and-secrets.md) | You are assigning managed identities, blob roles, or Key Vault secrets on Azure |
| [Future plans](docs/future-plans/README.md) | You want the planned Entra sign-in and one-command Azure bootstrap direction |
| [Future ideas](docs/future-ideas/README.md) | You want the uncommitted post-MVP research directions |

## Repository layout

- `src/`: domain, application, contracts, storage/imaging/persistence adapters,
  observability, migrations, API, worker, and the React web application.
- `tests/`: unit, architecture, contract, integration, conformance, imaging,
  migration, E2E-host, and performance projects, plus the Playwright suite in
  `tests/Vistara.E2E`.
- `deploy/`: Dockerfiles, Compose topologies, Nginx, MinIO, PostgreSQL, OTLP
  collector configuration, backup scripts, and deployment gates.
- `eng/`: repository and Shadow validators with isolated Node dependencies.
- `.shadow/`: evidence-linked architecture decisions.
- `.github/workflows/`: CI, Pages preview, gallery smoke, deployment gates,
  performance, provider live tests, CodeQL, dependency audit and review,
  repository tooling, and release-image publication.
- `docs/`: specification, release notes, operations, security, and future ideas.

## Current limitations

- AI features, an MCP server, and cloud imports are research notes only; nothing
  in `docs/future-ideas/` is implemented.
- Imaging accepts and produces JPEG, PNG, and WebP single-frame images only.
  AVIF, HEIC, TIFF, GIF, and RAW are not processed.
- Metadata capture records presence and privacy flags plus dimensions, format,
  and orientation. Vistara does not yet catalogue individual EXIF, IPTC, or XMP
  fields such as camera, lens, exposure, keywords, or coordinates.
- PostgreSQL connections use a connection string only. There is no Entra ID or
  managed-identity token provider for PostgreSQL; managed identity is supported
  for Azure Blob Storage. See the Azure guides for the passwordless-PostgreSQL
  caveat.
- Azure Blob managed identity is **user-assigned only**. Set
  `Media:Storage:Azure:CredentialMode` to `ManagedIdentity` and supply the
  identity's client ID in `Media:Storage:Azure:ManagedIdentityClientId`; a
  system-assigned identity, or any identity inferred from the host, is not
  supported. `DefaultCredential` remains for local development and needs the
  explicit `AllowDefaultCredentialOutsideDevelopment` opt-in anywhere else,
  and `SharedKey` remains the connection-string fallback.
- Storage onboarding validates and generates configuration; it never writes
  configuration or swaps the active provider. Applying a provider requires a
  configuration change and an API and worker restart.
- SQLite deployments support exactly one worker and one host. Do not scale them
  or place the database file on NFS or SMB.
- The API process does not host worker services; a separate worker container is
  always required.
- Local filesystem storage has no direct-upload URLs, multipart uploads, or
  signed reads; uploads are proxied through the API.
- The outbox advances durable publication state in the database. No external
  message broker is integrated.
- Vistara validates JWTs from configured issuers but hosts no interactive OIDC
  sign-in, callback, or token-issuance flow of its own.
- The worker exposes no HTTP health endpoint; its health snapshot is in-process.
