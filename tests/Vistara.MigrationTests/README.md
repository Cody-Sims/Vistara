# Migration compatibility gates

The required test suite is deterministic and does not require a PostgreSQL
secret or Docker. It applies and rolls back SQLite in memory, inspects
PostgreSQL normal/idempotent SQL, detects snapshot drift, and compares logical
relational coverage.

Provider differences intentionally allowed by the parity gate are:

- SQLite storage affinities versus PostgreSQL native types.
- PostgreSQL's 63-byte identifier limit, which truncates only overlong
  constraint/index names while preserving their logical column coverage.
- PostgreSQL forced row-level security; SQLite relies on application tenant
  filters and composite tenant keys.

The suite upgrades the real initial migration as an N-1 baseline to the
additive upload-ingest migration, verifies existing tenant data survives, and
checks the required quota-usage backfill. It also verifies full
rollback/reapply behavior. No released N-2 baseline exists yet; committed
release baselines must extend coverage to the two prior supported releases once
they exist.
