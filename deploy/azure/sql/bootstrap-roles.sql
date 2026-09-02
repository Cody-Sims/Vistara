-- Vistara hosted bootstrap — PostgreSQL roles and grants.
--
-- Run by `hooks/postprovision-database.sh` as the Microsoft Entra
-- administrator of the flexible server, connected to the `postgres` database.
-- Every statement is safe to run again: the script is replayed on every
-- provisioning pass and after any failed run.
--
-- The three runtime principals are managed identities, so they have no
-- password and cannot be created with CREATE ROLE. `pgaadauth_create_principal_with_oid`
-- takes five arguments — role name, object ID, object type, isAdmin, isMfa —
-- and the widely copied four-argument form does not exist on this server.
--
-- The privilege model mirrors `deploy/postgres/init-runtime-roles.sh`: the
-- migrator owns the schema and is the only role that may change it, while the
-- API and worker get data rights on what the migrator creates and nothing
-- else.

\set ON_ERROR_STOP on

-- ---------------------------------------------------------------------------
-- Cluster principals (database `postgres`)
-- ---------------------------------------------------------------------------

SELECT pgaadauth_create_principal_with_oid(:'migrator_role', :'migrator_object_id', 'service', false, false)
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'migrator_role');

SELECT pgaadauth_create_principal_with_oid(:'api_role', :'api_object_id', 'service', false, false)
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'api_role');

SELECT pgaadauth_create_principal_with_oid(:'worker_role', :'worker_object_id', 'service', false, false)
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'worker_role');

-- Nothing below may create a database, a role, or another login.
ALTER ROLE :"migrator_role" NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;
ALTER ROLE :"api_role" NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;
ALTER ROLE :"worker_role" NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;

-- ---------------------------------------------------------------------------
-- Application database
-- ---------------------------------------------------------------------------

\connect :"application_database"

-- Values are carried into the anonymous blocks below through session settings,
-- because psql does not substitute variables inside dollar-quoted bodies.
SELECT set_config('vistara.migrator_role', :'migrator_role', false);
SELECT set_config('vistara.api_role', :'api_role', false);
SELECT set_config('vistara.worker_role', :'worker_role', false);

REVOKE ALL ON DATABASE :"application_database" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"application_database" TO :"migrator_role", :"api_role", :"worker_role";
GRANT CREATE ON DATABASE :"application_database" TO :"migrator_role";

-- The database and its public schema are created by the ARM template and owned
-- by the server administrator role. Handing the schema to the migrator is what
-- lets the migration bundle create tables without any broader right. A server
-- that refuses the transfer is reported and left alone: the explicit grants
-- below still let the migrator work.
DO $$
DECLARE
  migrator text := current_setting('vistara.migrator_role');
BEGIN
  BEGIN
    EXECUTE format('ALTER SCHEMA public OWNER TO %I', migrator);
  EXCEPTION
    WHEN insufficient_privilege OR undefined_object THEN
      RAISE NOTICE 'left schema public owned by its current owner: %', SQLERRM;
  END;

  BEGIN
    EXECUTE format('GRANT CREATE, USAGE ON SCHEMA public TO %I', migrator);
  EXCEPTION
    WHEN insufficient_privilege THEN
      RAISE NOTICE 'could not grant schema rights to %: %', migrator, SQLERRM;
  END;
END
$$;

REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO :"api_role", :"worker_role";

-- Default privileges may only be altered for a role the current user belongs
-- to. The Entra administrator creates these roles, so the membership normally
-- already exists; taking it explicitly keeps a reused server working.
DO $$
DECLARE
  migrator text := current_setting('vistara.migrator_role');
BEGIN
  EXECUTE format('GRANT %I TO CURRENT_USER', migrator);
EXCEPTION
  WHEN duplicate_object OR insufficient_privilege THEN
    RAISE NOTICE 'kept the existing membership of %: %', migrator, SQLERRM;
END
$$;

-- What the migrator creates from now on is readable and writable by the two
-- runtime roles, and by nothing else.
ALTER DEFAULT PRIVILEGES FOR ROLE :"migrator_role" IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO :"api_role", :"worker_role";
ALTER DEFAULT PRIVILEGES FOR ROLE :"migrator_role" IN SCHEMA public
  GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO :"api_role", :"worker_role";

-- A rerun against a server whose migrations already ran repairs the objects
-- that existed before the default privileges above were in force.
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public
  TO :"api_role", :"worker_role";
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public
  TO :"api_role", :"worker_role";
