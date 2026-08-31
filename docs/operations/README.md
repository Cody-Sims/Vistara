# Operations documentation

Runbooks for people who run a Vistara instance.

## Run an instance

| Document | Use it when |
|---|---|
| [Deployment topologies](../../deploy/README.md) | Choosing between the starter, PostgreSQL, and development Compose files, generating credentials, or changing proxy trust |
| [Administration and cloud onboarding](../security/admin-and-cloud-onboarding.md) | Creating the first owner, granting administrative access, or validating and applying cloud storage |
| [Backup and restore](backup-and-restore.md) | Scheduling backups, restoring data, or running the quarterly restore drill |
| [Release, migration, and rollback runbook](release-runbook.md) | Publishing a release, applying migrations, or rolling one back |
| [Security operations](../security/security-operations.md) | Reviewing scanning coverage, handling an advisory, or rotating credentials |

## Evaluate on Azure

| Document | Use it when |
|---|---|
| [Azure free credits](azure-free-credits.md) | Standing up a non-production evaluation instance on current Microsoft Azure free-credit offers |
| [Azure identity, RBAC, and secrets](azure-identity-and-secrets.md) | Assigning managed identities, blob role assignments, Key Vault secrets, or private registry credentials |

Both Azure guides are evaluation runbooks, not a production architecture, and
label every non-obvious claim as verified, inferred, or unverified.

## Related

- [`../release-notes.md`](../release-notes.md) records the user-visible change
  history for each release.
- [`../specification.md`](../specification.md) remains the product, architecture,
  acceptance, and roadmap authority.
- [`../../README.md`](../../README.md) has the quick start, the exact build and
  test commands, and the configuration key reference.
