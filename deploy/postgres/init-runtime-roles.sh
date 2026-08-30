#!/usr/bin/env bash
set -euo pipefail

: "${VISTARA_MIGRATOR_DB_PASSWORD:?VISTARA_MIGRATOR_DB_PASSWORD is required}"
: "${VISTARA_API_DB_PASSWORD:?VISTARA_API_DB_PASSWORD is required}"
: "${VISTARA_WORKER_DB_PASSWORD:?VISTARA_WORKER_DB_PASSWORD is required}"

psql --set=ON_ERROR_STOP=1 \
  --username "$POSTGRES_USER" \
  --dbname postgres \
  --set=migrator_password="$VISTARA_MIGRATOR_DB_PASSWORD" \
  --set=api_password="$VISTARA_API_DB_PASSWORD" \
  --set=worker_password="$VISTARA_WORKER_DB_PASSWORD" <<'SQL'
CREATE ROLE vistara_migrator
  LOGIN PASSWORD :'migrator_password'
  NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;
CREATE ROLE vistara_api_runtime
  LOGIN PASSWORD :'api_password'
  NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;
CREATE ROLE vistara_worker
  NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;
CREATE ROLE vistara_worker_runtime
  LOGIN PASSWORD :'worker_password'
  NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION
  IN ROLE vistara_worker;

CREATE DATABASE vistara OWNER vistara_migrator;
REVOKE ALL ON DATABASE vistara FROM PUBLIC;
GRANT CONNECT ON DATABASE vistara
  TO vistara_migrator, vistara_api_runtime, vistara_worker;
SQL

psql --set=ON_ERROR_STOP=1 \
  --username "$POSTGRES_USER" \
  --dbname vistara <<'SQL'
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO vistara_api_runtime, vistara_worker;

ALTER DEFAULT PRIVILEGES FOR ROLE vistara_migrator IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES
  TO vistara_api_runtime, vistara_worker;
ALTER DEFAULT PRIVILEGES FOR ROLE vistara_migrator IN SCHEMA public
  GRANT USAGE, SELECT, UPDATE ON SEQUENCES
  TO vistara_api_runtime, vistara_worker;
SQL
