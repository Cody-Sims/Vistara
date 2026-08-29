# SQLite migrations

This assembly owns the SQLite EF Core migration history for `VistaraDbContext`.
`VistaraDbContextFactory` uses an in-memory database solely for design-time model
discovery, so migration listing and script generation require no files,
credentials, environment variables, or reachable database.

SQLite keeps the application query filters and tenant-composite foreign keys
from Persistence. It intentionally adds no row-level-security emulation.
Provider-specific storage uses SQLite affinities (`TEXT` for UUIDs and UTC
timestamps, `INTEGER` for integral values and booleans).

Runtime composition must call `UseVistaraMigrations` on the SQLite provider
options. It must not call `Migrate` automatically; migration bundles or an
explicit deployment step own schema changes.

Verification:

```bash
dotnet ef migrations list --project src/Vistara.Migrations.Sqlite
dotnet test src/Vistara.Migrations.Sqlite/Verification/Vistara.Migrations.Sqlite.Verification.csproj
```

The verification project applies the migration to an empty in-memory database,
checks the frozen snapshot for pending changes, and validates generated SQL.
