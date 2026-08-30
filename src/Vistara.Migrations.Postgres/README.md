# PostgreSQL migrations

This assembly owns the PostgreSQL EF Core migration history for
`VistaraDbContext`. `VistaraDbContextFactory` configures Npgsql without a
connection string, credential, environment variable, or reachable database;
it is intended for migration listing and script generation.

Runtime composition must call `UseVistaraMigrations` on the Npgsql provider
options. It must not call `Migrate` automatically; migration bundles or an
explicit deployment step own schema changes.

## Tenant row-level security

The initial migration enables and forces RLS on every tenant-owned table and
creates an explicit `tenant_isolation` policy for reads and writes. Policies
compare `tenant_id` with the PostgreSQL setting `vistara.tenant_id`:

```sql
SELECT set_config('vistara.tenant_id', '01991a54-6c00-7000-8000-000000000001', true);
```

The third argument must be `true` so the value is transaction-local. The
application must set it after beginning each tenant transaction. Missing or
empty settings evaluate to `NULL`, so policies fail closed. `FORCE ROW LEVEL
SECURITY` also applies policies to the table owner; PostgreSQL superusers and
roles granted `BYPASSRLS` must not be used by the runtime.

## Worker tenant routing

`worker_tenant_catalog` is an intentionally non-RLS routing table. It contains
only the routed tenant ID, worker eligibility, tenant version, and update time;
it contains no user, credential, media, or tenant-content metadata. Its
migration reads `tenants` through a temporary migration-scoped RLS policy, then
removes that policy.

Production deployments with separate database roles create the conventional
`vistara_worker` role before applying migrations and grant the worker login
membership in it. The catalog migration grants that role `SELECT` on this table
only. It grants no tenant-table access and never grants `BYPASSRLS`.

Provider-specific storage uses PostgreSQL `uuid`, `bigint`, `boolean`, and
`timestamp with time zone` types. SQLite-specific affinities remain isolated
to its independent migration history.

Verification:

```bash
dotnet ef migrations list --project src/Vistara.Migrations.Postgres
dotnet test tests/Vistara.MigrationProviderTests/Postgres/Vistara.MigrationProviderTests.Postgres.csproj
```

The verification project checks normal and idempotent empty-database scripts,
all RLS policies, snapshot drift, and logical schema parity with SQLite.
