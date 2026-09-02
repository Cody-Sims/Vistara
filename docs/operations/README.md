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
| [Azure hosted bootstrap](azure-hosted-bootstrap.md) | You want the one-command path: `./deploy/azure/up.sh` provisions, migrates, and hands you a browser sign-in, and `./deploy/azure/down.sh` tears it down |
| [Azure free credits](azure-free-credits.md) | You want to understand the credit offers, how Azure bills these services, and the manual CLI path the bootstrap replaces |
| [Azure identity, RBAC, and secrets](azure-identity-and-secrets.md) | You are assigning managed identities, blob role assignments, Key Vault secrets, or private registry credentials by hand |

Start with the hosted bootstrap; the other two are background and manual
alternatives. All three are evaluation runbooks, not a production
architecture, and label every non-obvious claim as verified, inferred, or
unverified.

## Related

- [`../release-notes.md`](../release-notes.md) records the user-visible change
  history for each release.
- [`../specification.md`](../specification.md) remains the product, architecture,
  acceptance, and roadmap authority.
- [`../../README.md`](../../README.md) has the quick start, the exact build and
  test commands, and the configuration key reference.
