# Compose deployments

All application images are built from digest-pinned .NET and Node base images.
The API, worker, and migration containers run as UID/GID `1654`, with read-only
root filesystems, dropped capabilities, bounded temporary storage, and explicit
resource/restart policies.

## Generate local credentials

Do not copy example passwords into a deployment. Generate an ignored environment
file with mode `0600`:

```bash
./deploy/generate-env.sh deploy/.env
```

Replace the generated `issuer.example.invalid` OIDC values before enabling
login. The API key pepper and all database/object-storage passwords are random;
no usable credential is committed. Pass the file explicitly:

```bash
docker compose --env-file deploy/.env -f deploy/compose.starter.yml up --build
```

## Topologies

- `compose.development.yml`: PostgreSQL, MinIO, migration task, API, worker,
  Vite development server, reverse proxy, and local OTLP collector.
- `compose.starter.yml`: one-host SQLite and local media named volumes, one API,
  and exactly one worker.
- `compose.postgres.yml`: migration task, separately credentialed API and worker
  database roles, PostgreSQL 18, MinIO with a bucket-scoped application access
  key, and an unprivileged reverse proxy. Only the proxy joins the external
  network.

The current API executable does **not** register worker hosted services, so the
specification's embedded-worker starter process is not supported by the runtime.
The starter therefore uses one separately runnable worker container. Both
processes mount the same Docker-local SQLite volume on one host; do not place it
on NFS/SMB, copy it into another container host, or scale either role. This is
the safest topology available without crossing the deployment ownership
boundary to change application composition.

Start the PostgreSQL example:

```bash
docker compose --env-file deploy/.env -f deploy/compose.postgres.yml up --build
```

The migration container must complete successfully before API or worker start.
PostgreSQL initializes a schema-owner migration login and distinct DDL-free API
and worker logins. Database and object data use local named volumes. For external
S3-compatible storage, replace the MinIO service and `Media__Storage__S3__*`
settings while retaining endpoint allowlisting and TLS. External S3 or OIDC
metadata also requires attaching only the API/worker roles that need it to an
operator-controlled egress network or proxy. Docker Compose bridge networks do
not enforce hostname-level egress allowlists.

The bundled Nginx listener is HTTP-only for local evaluation. Terminate TLS at a
trusted edge proxy before exposing Vistara outside a private network.

## Validation

```bash
docker compose --env-file deploy/.env -f deploy/compose.development.yml config
docker compose --env-file deploy/.env -f deploy/compose.starter.yml config
docker compose --env-file deploy/.env -f deploy/compose.postgres.yml config
docker build -f deploy/containers/api.Dockerfile .
docker build -f deploy/containers/worker.Dockerfile .
docker build -f deploy/containers/migration.Dockerfile .
```
