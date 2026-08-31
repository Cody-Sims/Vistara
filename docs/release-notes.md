# Release notes

User-visible changes, newest first. Operational detail for a release lives in
[Release, migration, and rollback runbook](operations/release-runbook.md);
product scope lives in [the specification](specification.md).

Vistara follows Semantic Versioning. Images are published to GHCR for each
stable release tag and commit and should be deployed by digest.

## 0.1.0 — unreleased

First complete release of the 0.1 scope. No Git tag has been published yet;
this entry describes what is on `main` and ready to be tagged.

### Highlights

- **Set up in one command.** The starter Compose topology runs SQLite and local
  media on one host. Generate credentials with `deploy/generate-env.sh`, start
  the stack, and create the first workspace and owner at `/setup`. There is no
  default account or password.
- **A responsive gallery.** A virtualized library timeline with grid and list
  modes, an asset viewer, search with facets, albums, flat tags, favorites, bulk
  actions, and trash with restore and reviewed purge.
- **Uploads that survive a reload.** The upload queue persists in IndexedDB and
  resumes, reports duplicates, and supports cancel and retry. Uploads are
  verified by SHA-256, checked against quotas, and deduplicated exactly.
- **Deterministic derivatives.** A background worker generates the `thumb`,
  `grid`, `viewer`, and `download-web` presets in WebP, JPEG, and PNG through
  NetVips, correcting orientation, normalizing colour, and stripping metadata.
  Derivative URLs are immutable and cache-friendly.
- **Sharing.** Album and snapshot share links with optional passwords, expiry,
  metadata-exposure controls, and separate original and rendition download
  permissions, plus a public gallery view that never exposes a provider URL.
- **Installable web application.** A web manifest and a narrowly scoped service
  worker let the application shell open offline. API, media, delivery, share,
  health, and metrics responses are explicitly never cached, and an
  online/offline indicator reflects real connectivity.
- **Administration.** Storage usage and health, tenant quotas and policies, job
  administration with retry and cancel, an audit log, member and role
  management, and API key issuance and revocation.
- **Cloud storage onboarding assistant.** Test candidate filesystem, Azure Blob,
  or S3-compatible credentials against the real provider and generate the
  deployment configuration to apply them. Submitted credentials are
  request-scoped, redacted, and never stored; applying a provider is a server
  configuration change plus an API and worker restart.
- **Portable storage.** Local filesystem, Azure Blob Storage, and S3-compatible
  storage with tested AWS, Cloudflare R2, Backblaze B2, and MinIO profiles,
  including direct and multipart uploads and signed reads where the provider
  supports them.
- **Two databases.** SQLite for one-host installations and PostgreSQL for
  production, with separate migration bundles kept in provider parity.
- **Security by default.** Session cookies, API keys, and configured-issuer JWT
  validation with simultaneous credentials rejected, per-request tenant pinning,
  role-derived scopes, CSRF protection, `ETag` concurrency, idempotency keys,
  rate limiting, CSP and HSTS, and tenant-scoped audit records. Containers run
  non-root with read-only root filesystems and dropped capabilities.
- **Operable.** Liveness, readiness, and startup health endpoints that disclose
  no topology, OpenTelemetry traces, metrics, and logs over a single optional
  OTLP endpoint, migration bundles, backup and restore scripts with a drill, and
  release and rollback runbooks.
- **Documented evaluation on Azure.** Free-credit and identity, RBAC, and
  secrets guides map Azure resources onto the configuration keys this repository
  actually reads.

### Known limitations

- Imaging accepts and produces JPEG, PNG, and WebP single-frame images only.
  AVIF, HEIC, TIFF, GIF, and RAW are not processed.
- Metadata capture records presence and privacy flags plus dimensions, format,
  and orientation, not individual EXIF, IPTC, or XMP fields.
- Storage onboarding validates and generates configuration but never applies it;
  changing the active provider requires a configuration change and an API and
  worker restart, and does not migrate existing objects.
- PostgreSQL connections use a connection string only; there is no Entra ID or
  managed-identity token provider for PostgreSQL. Managed identity is supported
  for Azure Blob Storage.
- SQLite deployments support exactly one worker on one host and must not be
  scaled or placed on NFS or SMB.
- The API process does not host worker services; a separate worker container is
  always required.
- Local filesystem storage has no direct-upload URLs, multipart uploads, or
  signed reads; those uploads are proxied through the API.
- Vistara validates JWTs from configured issuers but hosts no interactive OIDC
  sign-in or callback flow, and at least one issuer must be configured at
  startup even for local password sign-in.
- The outbox advances durable publication state in the database; no external
  message broker is integrated.
- The worker exposes no HTTP health endpoint.

### Not in this release

AI metadata and editing, a Model Context Protocol server, and cloud imports are
research notes in [`future-ideas/`](future-ideas/README.md). They are not
implemented, not scheduled, and not a release promise.

### Upgrading

This is the first release, so there is no upgrade path yet. From the next
release onward, run the migration bundle to completion before rolling out the
API and worker, following
[the release runbook](operations/release-runbook.md).
