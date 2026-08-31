# Backup and restore

Vistara backups cover everything that cannot be recomputed: the database,
original blobs, the configuration and key identifiers required to read them,
audit records, and restoration/deletion tombstones. Derivatives are excluded
because the pipeline reproduces them deterministically.

Purged data may still exist inside an immutable backup until the published
backup expiry recorded on its deletion tombstone. Publish that expiry to
tenants; do not promise erasure faster than the retention of the backups that
still hold the bytes.

## Targets

| Profile | RPO | RTO | Default schedule |
|---|---|---|---|
| Production PostgreSQL | ≤15 minutes | ≤4 hours | Continuous archiving plus a daily base backup |
| Starter SQLite | ≤24 hours | ≤4 hours | Daily |

A restore drill runs at least quarterly in an isolated location and must meet
the RTO above.

## Tooling

| Script | Purpose |
|---|---|
| `deploy/backup/vistara-backup.sh` | Creates a checksum-manifested archive from a live instance |
| `deploy/backup/vistara-restore.sh` | Restores an archive into operator-supplied targets |
| `deploy/backup/verify-restore-drill.sh` | Runs the non-destructive drill and writes a drill report |

The starter profile needs `sqlite3` and `tar`. The PostgreSQL profile needs
`pg_dump`, `pg_restore`, and `psql` from a client at least as new as the server.
None of the scripts delete data: the backup refuses a non-empty output
directory, the restore refuses to replace an existing database or a populated
media directory unless `--force` is passed during a declared recovery, and the
drill refuses a non-empty working directory.

## Taking a backup

Starter (SQLite plus local media):

```bash
./deploy/backup/vistara-backup.sh \
  --profile starter \
  --database /var/lib/vistara/data/vistara.db \
  --media-root /var/lib/vistara/media \
  --config /etc/vistara/appsettings.Production.json \
  --output /srv/vistara-backups/$(date -u +%Y%m%dT%H%M%SZ)
```

Production PostgreSQL, where blobs live in object storage that is replicated by
its own provider:

```bash
PGHOST=postgres PGUSER=vistara_migrator PGPASSWORD="$VISTARA_BACKUP_PASSWORD" \
./deploy/backup/vistara-backup.sh \
  --profile postgres \
  --database vistara \
  --output /srv/vistara-backups/$(date -u +%Y%m%dT%H%M%SZ)
```

Supply the database password only through the environment. Never place it on a
command line, in a Compose file, or in the archive.

Each archive contains:

```text
manifest.json     human-readable summary
manifest.env      machine-readable summary consumed by the restore and drill
SHA256SUMS        checksum of every archived file
database/…        SQLite copy or custom-format PostgreSQL dump
media/…           original blobs, when they are part of the backup
config/…          copied configuration, mode 0600
```

Store archives with at-rest encryption and access control at least as strict as
the instance itself; `config/` holds key identifiers, and the database holds
peppered credential material and audit history.

## Restoring

```bash
./deploy/backup/vistara-restore.sh \
  --archive /srv/vistara-backups/20260830T041500Z \
  --target-database /var/lib/vistara/restored/vistara.db \
  --target-media /var/lib/vistara/restored/media
```

The restore verifies `SHA256SUMS` before writing anything and stops on the first
mismatch. Restoring over a live instance requires `--force` and must only happen
after the instance is stopped and the current state has itself been archived.

## Quarterly restore drill

The drill is non-destructive: it restores into an isolated working directory and
verifies the recovered instance without touching production.

```bash
./deploy/backup/verify-restore-drill.sh \
  --archive /srv/vistara-backups/20260830T041500Z \
  --workdir /srv/vistara-drills/2026-Q3 \
  --rto-minutes 240
```

The drill fails unless every check below passes:

- the restored database passes an integrity check;
- every required table is present, including tenants, assets, blobs, audit
  events, deletion tombstones, and authorization tables;
- the migration ledger head and row count match the recorded manifest;
- tenant, user, asset, blob, audit, tombstone, and share counts match the
  manifest;
- every active blob referenced by the database exists in the restored object
  store, carries a valid descriptor footer, and matches its recorded SHA-256;
- every authorization row still resolves to a restored tenant;
- the whole drill completes inside the RTO budget.

`workdir/drill-report.json` records the outcome, elapsed seconds, verified blob
count, and restored counts. Keep the report as the audit evidence for the
quarter and record the exercised archive, the operator, and any deviation.

A PostgreSQL archive is drilled the same way against a disposable database:

```bash
./deploy/backup/verify-restore-drill.sh \
  --archive /srv/vistara-backups/20260830T041500Z \
  --workdir /srv/vistara-drills/2026-Q3 \
  --scratch-database vistara_drill
```

## Automated coverage

`eng/tests/backup-restore.test.mjs` builds a synthetic instance and proves that
the archive verifies, that the drill passes, and that the drill fails on a
corrupted payload, a missing original, an incomplete schema, drifted tenant
counts, and unscoped authorization rows. Run it with:

```bash
node --test eng/tests/backup-restore.test.mjs
```
