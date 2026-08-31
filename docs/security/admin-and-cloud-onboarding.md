# Administration and cloud onboarding

This document describes how the first owner is created, how administrative
access is granted and checked, and what the storage onboarding assistant does
and deliberately does not do. Repository scanning, secret handling in CI, and
credential rotation live in [Security operations](security-operations.md);
product requirements live in [`../specification.md`](../specification.md).

## First-run setup and sign-in

Setup is a one-time, anonymous provisioning flow. No default account, password,
or invitation token is committed to this repository.

1. The browser opens `/setup`, or `/login`, which anonymously asks
   `GET /api/v1/setup` whether provisioning is still available and shows a setup
   link when it is. The discovery response contains only availability; it never
   reveals user counts, tenant counts, or topology, and it is rate limited.
2. `POST /api/v1/setup` supplies the workspace name and slug, the owner's
   display name and email, and a password of at least 12 characters. Every field
   is required; malformed JSON is rejected with 400 and invalid fields with 422.
3. The server creates the tenant, the user, a local identity bound to the email,
   a hashed password, an active `TenantOwner` membership, and a
   `tenant.owner.provisioned` audit record in one transaction.
4. Persistence accepts only the first owner. Later attempts fail with
   `setup.already_provisioned`, and simultaneous attempts with
   `setup.provisioning_contended`.
5. Setup returns `201 Created` with `Location: /api/v1/me`. It does **not** issue
   a session cookie. The web application immediately calls
   `POST /api/v1/auth/login` with the new credentials; if that call fails the
   owner signs in at `/login` instead.
6. Successful login persists a browser session, sets the `__Host-vistara-session`
   cookie — `Secure`, `HttpOnly`, `SameSite=Lax`, 30-minute idle and 24-hour
   absolute lifetimes — and returns an antiforgery token. Cookie-authenticated
   unsafe requests must echo that token in `X-Vistara-CSRF`.

Login, logout, and setup are forced onto the anonymous scheme so a stale or
invalid cookie can never block bootstrap or recovery.

## Access model

Every authenticated request carries exactly one tenant, fixed for the lifetime
of the request; a request cannot change tenant after the context is established.
Roles are ordered and grant scopes:

| Role | Scopes | Can |
|---|---|---|
| `Viewer` | `assets.read` | Browse |
| `Member` | adds `assets.upload`, `metadata.manage`, `shares.manage` | Upload, edit metadata, manage shares |
| `TenantAdmin` | adds `members.manage`, `api_keys.manage` | Manage members, API keys, read the audit log |
| `TenantOwner` | adds `quotas.manage` | Change quotas and policies, validate storage candidates |

Only an existing owner may grant `TenantOwner`. Endpoint policies require an
authenticated principal; the role, scope, tenant, and object checks that decide
each operation run in the feature authorization layer, so adding a route without
its authorization port grants nothing by default.

Credentials are never combined. Presenting more than one of a bearer token, an
`X-API-Key`, and a session cookie fails with `authentication.scheme_confusion`
rather than letting the server pick one. API keys are peppered, versioned, and
compared in fixed time; they are exempt from CSRF because they carry no ambient
browser authority.

The web application lazy-loads administration screens only after the
authorization guard admits the user, so an unprivileged session never downloads
or renders the administrative bundle.

## Cloud storage onboarding

`/admin/storage` is an onboarding assistant for filesystem, Azure Blob, and
S3-compatible storage — including Amazon S3, MinIO, Ceph, and Backblaze B2. It
has three jobs: report the active storage, test a candidate, and generate the
deployment configuration to apply it.

### What it reports

`GET /api/v1/admin/storage` returns the logical `originals`, `derivatives`, and
`staging` buckets with provider kind, health, byte and object counts, quotas,
and the last check time. It deliberately withholds container names, bucket
names, endpoints, and filesystem paths.

### What validation actually tests

`POST /api/v1/admin/storage/validate` requires `TenantOwner`, is separately
throttled, and performs a real connectivity and permission probe against a
reserved probe prefix:

| Provider | Checks |
|---|---|
| `filesystem` | Root exists, directory lists, empty probe file writes, probe file deletes. Authentication is reported as skipped |
| `azureBlob` | Lists one blob under the probe prefix, uploads an empty probe blob, deletes it |
| `s3` | `ListObjectsV2` under the probe prefix, `PutObject` of an empty probe, `DeleteObject` |

Each result reports `reachable`, `authenticated`, `read`, `write`, and `delete`
separately, so a permission gap is distinguishable from a wrong endpoint or a
bad credential. Validation never creates a container, bucket, or root directory,
and never reads or modifies existing media; the target must already exist.

Accepted candidate credentials are Azure managed identity, account key, or SAS
token; S3 access key and secret with an optional session token, or anonymous.

### Transient credentials

Submitted credentials are parsed directly into a redacted secret holder rather
than bound to ordinary string properties, are never echoed in a response, never
written to logs, passed only into a one-shot provider client, and disposed and
zeroed when the request scope ends. Nothing is cached or persisted. Requests are
bounded to a 16 KiB body, 4096 characters per secret, and a 10-second validation
timeout.

Before any credential is used, the candidate endpoint is checked against
first-party Azure endpoint rules, the operator-configured trusted host list,
transport restrictions, and blocked-address rules for both IP literals and
resolved DNS. A request cannot nominate its own trust exemption. Managed
identity is only ever sent to a trusted first-party Azure Blob endpoint for the
named account, and anonymous S3 validation is accepted only for an endpoint the
operator pretrusted, which in practice means an emulator or a private
compatible service.

### Restart to apply

There is no API that saves storage credentials, switches the active provider, or
rewrites configuration. Active media configuration is bound from the `Media`
section at startup, validated by a startup hosted service, and used to construct
a single blob store for the process lifetime.

Applying a validated candidate is therefore an operator action:

1. Validate the candidate from `/admin/storage` and confirm every check passes.
2. Copy the generated template and set the real secret values from your secret
   store — not from the browser, which keeps nothing.
3. Apply the configuration to the API and worker, then restart both. Restart the
   worker as well; it binds the same media options independently.
4. Confirm `/health/startup` and `/health/ready`, then re-open `/admin/storage`
   and check the reported provider kind and health.

Moving to a new provider does not migrate existing objects. Copy originals to
the new location before switching, or the previously stored assets will not be
readable from the new provider.

## Operational checks after an administrative change

- `curl --fail http://127.0.0.1:8080/health/live`, `/health/ready`, and
  `/health/startup` return success on every replica.
- `/admin/storage` reports the expected provider kind and a healthy status for
  `originals`, `derivatives`, and `staging`.
- `/admin/jobs` shows no growing backlog and no new dead-lettered jobs; retry a
  dead-lettered job only after the underlying cause is fixed.
- An upload completes end to end: the asset appears in `/library` and its
  derivatives render in the viewer.
- `/admin/audit` contains the expected records for the change — role changes,
  policy updates, and first-owner provisioning are all audited.
- Worker readiness covers database, schema, storage, and queue, and its startup
  probe covers configuration, migrations, and imaging. The worker publishes this
  snapshot in-process; the starter container health check only proves the
  process is alive, so read worker logs after a storage change.

## Current limitations

- Storage onboarding validates and generates configuration; it never applies it.
  A restart is always required.
- Google Cloud Storage is not a supported provider. Active storage is `Local`,
  `S3`, or `Azure`.
- PostgreSQL connections use a connection string only. Entra ID and
  managed-identity token authentication for PostgreSQL is not implemented;
  managed identity is supported for Azure Blob Storage. Keep password
  authentication enabled on Azure Database for PostgreSQL, as
  [Azure free credits](../operations/azure-free-credits.md) records.
- Vistara validates JWTs from configured issuers but hosts no interactive OIDC
  sign-in, callback, or token-issuance flow. At least one issuer must still be
  configured at startup even for a deployment that only uses local password
  sign-in.
- Administration covers tenant membership and roles. There is no API to delete a
  user, reset another person's password, or disable an account globally.
- Job cancellation is best effort; some job types answer
  `jobs.cancel_unsupported`.
- The API-key authenticator maps read methods to `assets.read` and every other
  method to `assets.upload` before feature authorization applies the specific
  rule, so an API key is not a route-scoped credential.
