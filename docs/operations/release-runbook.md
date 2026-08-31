# Release, migration, and rollback runbook

This runbook covers a stable release of the API, worker, and migration bundle
images and the rollback path when a release misbehaves. User-visible changes for
each release are recorded in `docs/release-notes.md`.

## Release artifacts

Publishing a release runs `.github/workflows/release-images.yml`, which requires
an immutable, non-draft, non-prerelease stable SemVer release whose commit is
reachable from `main`. It then publishes, for the release tag and commit SHA:

| Artifact | Image |
|---|---|
| API (contains the built SPA) | `ghcr.io/<owner>/vistara-api` |
| Worker | `ghcr.io/<owner>/vistara-worker` |
| Migration bundles | `ghcr.io/<owner>/vistara-migrations` |

Every image is built for `linux/amd64` and `linux/arm64` with an SBOM, maximum
provenance, and a registry-pushed build-provenance attestation. The workflow
summary lists the three digests; deploy by digest, not by tag.

The migration image carries both the SQLite and PostgreSQL bundles and selects
one with `MIGRATION_PROVIDER`. It reads its connection string from
`ConnectionStrings__Vistara` and runs as a non-root user with a read-only root
filesystem.

## Pre-release gates

A release candidate ships only when these checks are green on the release
commit:

| Gate | Workflow |
|---|---|
| Build, unit, integration, contract, migration, and architecture tests | `ci.yml` (`build-test`) |
| Web lint, typecheck, unit tests, and production build | `ci.yml` (`web-check`) |
| Container context allowlists and image builds | `ci.yml` (`container-context`, `container-build`) |
| Gallery upload, browse, organize, share, trash, and restore workflow | `gallery-smoke.yml` |
| Migration lock, single application, and idempotency | `deployment-gates.yml` (`migration-lock`) |
| Compose startup, service health, and health endpoints | `deployment-gates.yml` (`compose-startup`) |
| Deterministic performance budgets | `performance.yml` (`local-budgets`) |
| Reference-host and browser budgets | `performance.yml` (`reference-gate`, dispatched with the run holding the k6 summaries) |
| CodeQL, dependency review, and the vulnerable dependency audit | `codeql.yml`, `dependency-review.yml`, `dependency-audit.yml` |
| Restore drill within the RTO | `docs/operations/backup-and-restore.md` |

The reference-host performance gate is dispatched manually because it consumes
measurements captured against a populated reference instance:

```bash
gh workflow run performance.yml -f measurements-run-id=<run-id-with-k6-summaries>
```

## Branch protection

Mark exactly these status checks as required on `main`. Each one runs on every
pull request without a path filter, so it always reports and never leaves a
pull request waiting on a check that will not run.

| Required status check | Workflow |
|---|---|
| `Restore, build, and test` | `ci.yml` |
| `Lint, typecheck, test, and build web` | `ci.yml` |
| `Validate container contexts` | `ci.yml` |
| `Build api container` | `ci.yml` |
| `Build worker container` | `ci.yml` |
| `Build migration container` | `ci.yml` |
| `Gallery smoke (Playwright)` | `gallery-smoke.yml` |
| `Migration lock and idempotency` | `deployment-gates.yml` |
| `Compose startup and health` | `deployment-gates.yml` |
| `Deterministic performance budgets` | `performance.yml` |
| `Audit vulnerable dependencies` | `dependency-audit.yml` |
| `Review dependency changes` | `dependency-review.yml` |
| `Analyze csharp` | `codeql.yml` |
| `Analyze javascript-typescript` | `codeql.yml` |

Do not mark these as required:

- `Validate repository tooling` (`repository-tooling.yml`) is filtered to
  `.github/**`, `.shadow/**`, `eng/**`, and agent files. A filtered workflow
  reports nothing on unrelated pull requests, so requiring it would block every
  such pull request indefinitely. Remove its path filters first if it must
  become a required check.
- `Reference host release gate` (`performance.yml`) only runs on a dispatch that
  supplies reference measurements.
- The Pages and provider live-test jobs, which are branch-, schedule-, or
  secret-scoped and never gate a pull request.

Also enable "Require branches to be up to date before merging" so a gate result
always reflects the merge target, and keep the checks listed above in step with
this table whenever a job name changes; renaming a job silently retires its
required check.

## Ordered deployment

1. Take a fresh backup and confirm the most recent drill report.
2. Run the migration image to completion. Both Compose examples express this as
   `depends_on: { migrate: { condition: service_completed_successfully } }`; on
   other orchestrators run it as a job that must exit zero before rollout.
3. Roll out the API image.
4. Roll out the worker image.
5. Confirm `/health/startup`, `/health/ready`, and `/health/live` on every
   replica, then confirm queue depth and job failure rates return to baseline.

Concurrent migration containers are safe: `deployment-gates.yml` proves that two
bundles started at the same instant both succeed, that each migration is applied
exactly once, and that a repeat run leaves the ledger unchanged. Runtime API and
worker logins hold no DDL rights, so only the migration login can change the
schema.

## Schema change policy

- Deploy only backward-compatible expand migrations before the application
  rollout; the previous application version must keep running against the new
  schema.
- Backfill in a separate, resumable step after the expand migration.
- Contract (drop or narrow) only after the release that stopped using the old
  shape has been stable and is no longer a rollback target.
- Keep the SQLite and PostgreSQL migration assemblies in step; provider parity
  is enforced by `tests/Vistara.MigrationTests`.

## Rollback

Because only expand migrations precede a rollout, rollback is an application
change, not a schema change.

1. Redeploy the previous API and worker digests. Leave the added columns and
   tables in place.
2. Do not run a `down` migration against a live instance. Reverting the schema
   destroys data written by the newer release and is only appropriate on a
   restored isolated copy.
3. Pause purge workers to halt physical deletion immediately while the incident
   is open; trash and tombstones keep the data recoverable.
4. Select the previous derivative pipeline generation instead of rewriting
   derivative bytes; existing derivative URLs stay immutable.
5. Disable a problematic storage or identity provider profile rather than
   editing stored records.
6. Restore trashed assets through the product trash flow so that stable asset
   IDs and relationships are preserved.
7. If data loss already occurred, follow
   `docs/operations/backup-and-restore.md`, restore into an isolated location
   first, verify it with the drill, and only then promote it.

Record in the incident notes: the failing digest, the digest rolled back to, the
migration ledger head before and after, whether purge workers were paused, and
the archive used for any restore.

## Operator checklist

- Deploy images by digest and verify the provenance attestation before rollout.
- Generate credentials with `deploy/generate-env.sh`; never reuse example
  values, and keep the environment file at mode `0600` and out of version
  control.
- Keep the migration login separate from the DDL-free API and worker logins.
- Keep one SQLite writer on local disk; never place it on NFS or SMB and never
  scale a SQLite deployment horizontally.
- Terminate TLS at a reviewed edge before exposing the bundled HTTP-only
  Compose examples, then re-enable
  `Security__Transport__RedirectHttpToHttps`.
- Watch job latency, queue depth, reconciliation and orphan counts, share and
  authorization failures, and derivative cache hit ratio after every release.
- Rotate the API key pepper and database credentials on the documented schedule
  in `docs/security/security-operations.md`.
