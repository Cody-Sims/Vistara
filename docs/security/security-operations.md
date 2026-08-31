# Security operations

This document describes the automated security controls that guard the
repository and the operator duties that keep a running instance safe. Product
security requirements live in `docs/specification.md`. The first-owner setup
flow, the administrative access model, and the cloud storage onboarding
assistant — including how candidate credentials are held and why applying a
provider needs a restart — are documented in
[Administration and cloud onboarding](admin-and-cloud-onboarding.md).

## Automated scanning

| Control | Where | Trigger | Failure meaning |
|---|---|---|---|
| CodeQL with the extended security query suite | `.github/workflows/codeql.yml` | Push to `main`, every pull request, weekly | A C# or TypeScript pattern matched a security query |
| Dependency review | `.github/workflows/dependency-review.yml` | Every pull request | A changed dependency carries an advisory of low severity or higher, or a denied license |
| Vulnerable dependency audit | `.github/workflows/dependency-audit.yml` | Every pull request, push to `main`, weekly, dispatch | A restored .NET or npm dependency has a known advisory |
| Dependabot updates | `.github/dependabot.yml` | Weekly | Actions, NuGet, every npm manifest, and deployment images have pending updates |
| Workflow policy validation | `eng/validate-agent-workflows.mjs` | `repository-tooling.yml` | A workflow broke a pinning, permission, artifact, secret, or Dependabot rule |
| Container context allowlists | `deploy/containers/tests/context.sh` | `ci.yml` | A Dockerfile build context would include unexpected files |
| Compose startup and health | `deploy/containers/tests/compose-startup.sh` | `deployment-gates.yml` | A hardened topology failed to start or stayed unhealthy |
| Migration lock and idempotency | `deploy/containers/tests/migration-lock.sh` | `deployment-gates.yml` | Concurrent migrations were unsafe or non-idempotent |

The pull-request gates that must be marked required on `main` are listed in
`docs/operations/release-runbook.md`; every one of them runs unfiltered so it
always reports.

Denied licenses are the copyleft and source-available families that conflict
with the Apache-2.0 distribution: AGPL, GPL, SSPL, and BUSL. The LGPL native
libvips runtime is shipped as a separate dynamically linked library with its
license and provenance recorded under `/usr/share/licenses` inside the image and
in `deploy/licenses/THIRD-PARTY-NOTICES.md`.

## Secret handling in CI

- Pull-request workflows never read repository secrets. `pull_request_target` is
  forbidden, `secrets: inherit` is rejected, and any `secrets.*` reference other
  than the built-in `GITHUB_TOKEN` fails validation in a pull-request workflow.
- Live provider credentials are used only by `provider-live-tests.yml`, which
  runs on a schedule or manual dispatch and never on untrusted pull-request
  code.
- Package write permission is limited to release-triggered publication.
- Checkout credentials stay disabled unless a reviewed step needs them.
- Third-party actions are pinned to full commit SHAs with a version comment;
  Dependabot proposes the updates.

Never place a credential in a workflow input, a command line, a Compose file, a
backup archive, or a commit. Deployment credentials are generated locally with
`deploy/generate-env.sh`.

## Vulnerability response

1. Triage every new advisory from CodeQL, dependency review, the weekly audit,
   or a private report against the deployed release, not only `main`.
2. Patch or mitigate critical and high findings that are reachable from a
   deployed release first; then moderate and low findings on the normal update
   cadence.
3. Reproduce with a test before fixing, so the regression stays covered.
4. Ship the fix through the ordered release path in
   `docs/operations/release-runbook.md` and note the advisory identifier in the
   release notes.
5. When a fix requires rotating a credential, rotate before publishing the
   advisory resolution.

Report suspected vulnerabilities privately to the repository maintainers through
GitHub private vulnerability reporting rather than a public issue.

## Runtime hardening in the shipped deployments

- API, worker, and migration containers run as UID/GID `1654` with a read-only
  root filesystem, all capabilities dropped, `no-new-privileges`, bounded
  tmpfs mounts, and explicit CPU and memory limits.
- The database backend network is internal; only the reverse proxy joins an
  external network, and it binds to `127.0.0.1` by default.
- The API trusts exactly one proxy address with a one-hop forwarding limit.
- PostgreSQL provisions a schema-owner migration login plus separate DDL-free
  API and worker logins.
- MinIO receives a bucket-scoped application access key rather than root
  credentials.
- Health endpoints expose no versions, credentials, SQL, or topology.

## Credential rotation

| Credential | Cadence | Notes |
|---|---|---|
| API key pepper | Yearly, or immediately after suspected exposure | Add a new pepper version, move `CurrentPepperVersion` forward, and retain the previous version until stored keys are rehashed |
| Database logins | Yearly, or immediately after exposure | Rotate the migration login separately from the runtime logins |
| Object storage access key | Yearly, or immediately after exposure | Keep the bucket-scoped policy; never revert to root credentials |
| OIDC client configuration | On provider rotation | Replace the placeholder issuer values before enabling login |

Revoked API keys must stop working within 60 seconds; verify with the security
suites in `tests/Vistara.IntegrationTests` after any authentication change.

Backups hold credential material and audit history. Protect archives with at
least the access control and encryption applied to the live instance, and
account for the published backup expiry when answering deletion requests.
