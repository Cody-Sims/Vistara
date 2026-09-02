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

-- A role that already exists is left alone, but only after its directory
-- mapping has been checked. Azure records the object ID in a security label,
-- and a role of the right name mapped to a previous identity authenticates
-- nothing: the deployment would come up and fail every token exchange. That is
-- worth stopping here, where the message can say what to do about it.
SELECT set_config('vistara.migrator_role', :'migrator_role', false);
SELECT set_config('vistara.api_role', :'api_role', false);
SELECT set_config('vistara.worker_role', :'worker_role', false);
SELECT set_config('vistara.migrator_object_id', :'migrator_object_id', false);
SELECT set_config('vistara.api_object_id', :'api_object_id', false);
SELECT set_config('vistara.worker_object_id', :'worker_object_id', false);

DO $$
DECLARE
  candidate record;
  stored_label text;
BEGIN
  FOR candidate IN
    SELECT current_setting('vistara.migrator_role') AS role_name,
           current_setting('vistara.migrator_object_id') AS object_id
    UNION ALL
    SELECT current_setting('vistara.api_role'), current_setting('vistara.api_object_id')
    UNION ALL
    SELECT current_setting('vistara.worker_role'), current_setting('vistara.worker_object_id')
  LOOP
    stored_label := NULL;

    -- Two ways to read the mapping, because neither is available everywhere.
    -- The server's own listing function is what the administrator is meant to
    -- use; the catalog underneath it is readable only by a superuser, which
    -- the flexible-server administrator is not. When neither can be read the
    -- check is skipped out loud rather than guessed at.
    BEGIN
      EXECUTE format(
        'SELECT string_agg(listed::text, '' '') FROM pgaadauth_list_principals(false) AS listed'
        || ' WHERE position(%L IN listed::text) > 0',
        candidate.role_name)
      INTO stored_label;
    EXCEPTION
      WHEN undefined_function OR insufficient_privilege OR invalid_schema_name THEN
        stored_label := NULL;
    END;

    IF stored_label IS NULL THEN
      BEGIN
        SELECT label INTO stored_label
        FROM pg_shseclabel
        JOIN pg_roles ON pg_roles.oid = pg_shseclabel.objoid
        WHERE pg_shseclabel.provider = 'pgaadauth'
          AND pg_roles.rolname = candidate.role_name;
      EXCEPTION
        WHEN insufficient_privilege THEN
          RAISE NOTICE 'could not read the directory mapping of %; continuing without that check', candidate.role_name;
          stored_label := NULL;
      END;
    END IF;

    IF stored_label IS NOT NULL AND position(candidate.object_id IN stored_label) = 0 THEN
      RAISE EXCEPTION
        'role % is mapped to a different directory object than %; drop it with DROP ROLE "%" and rerun up.sh',
        candidate.role_name, candidate.object_id, candidate.role_name;
    END IF;
  END LOOP;
END
$$;

SELECT pgaadauth_create_principal_with_oid(:'migrator_role', :'migrator_object_id', 'service', false, false)
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'migrator_role');

SELECT pgaadauth_create_principal_with_oid(:'api_role', :'api_object_id', 'service', false, false)
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'api_role');

SELECT pgaadauth_create_principal_with_oid(:'worker_role', :'worker_object_id', 'service', false, false)
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'worker_role');

-- Nothing below may create a database or another role. The attributes a
-- directory principal is created with are already these, so this only matters
-- for a role someone changed by hand; SUPERUSER and REPLICATION are left out
-- because only a superuser may clear them and the flexible-server
-- administrator is not one, which would abort the whole file.
DO $$
DECLARE
  role_name text;
BEGIN
  FOREACH role_name IN ARRAY ARRAY[
    current_setting('vistara.migrator_role'),
    current_setting('vistara.api_role'),
    current_setting('vistara.worker_role')
  ]
  LOOP
    BEGIN
      EXECUTE format('ALTER ROLE %I NOCREATEDB NOCREATEROLE', role_name);
    EXCEPTION
      WHEN insufficient_privilege THEN
        RAISE NOTICE 'left the attributes of % as they are: %', role_name, SQLERRM;
    END;
  END LOOP;
END
$$;

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

-- What the migrator creates from now on should be readable and writable by the
-- two runtime roles and by nothing else. Default privileges may only be
-- altered for a role the current user belongs to, and a managed-identity role
-- is created by the server's own extension rather than by the administrator,
-- so on some servers the administrator cannot take that membership at all.
-- Both statements are therefore attempted and reported rather than assumed:
-- the caller re-applies the explicit grants below after every migration, which
-- is what keeps the deployment correct when this is refused.
DO $$
DECLARE
  migrator text := current_setting('vistara.migrator_role');
  api text := current_setting('vistara.api_role');
  worker text := current_setting('vistara.worker_role');
BEGIN
  BEGIN
    EXECUTE format('GRANT %I TO CURRENT_USER', migrator);
  EXCEPTION
    WHEN duplicate_object OR insufficient_privilege THEN
      RAISE NOTICE 'kept the existing membership of %: %', migrator, SQLERRM;
  END;

  BEGIN
    EXECUTE format(
      'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public'
      || ' GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO %I, %I',
      migrator, api, worker);
    EXECUTE format(
      'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public'
      || ' GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO %I, %I',
      migrator, api, worker);
  EXCEPTION
    WHEN insufficient_privilege THEN
      RAISE NOTICE
        'this server will not let % set default privileges for %; the runtime grants are re-applied after each migration instead: %',
        current_user, migrator, SQLERRM;
  END;
END
$$;

-- A rerun against a server whose migrations already ran repairs the objects
-- that existed before the default privileges above were in force. Granting on
-- a table the current user does not administer is refused outright rather than
-- warned about, so the refusal is turned into the remedy for it.
DO $$
DECLARE
  migrator text := current_setting('vistara.migrator_role');
  api text := current_setting('vistara.api_role');
  worker text := current_setting('vistara.worker_role');
BEGIN
  EXECUTE format(
    'GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO %I, %I', api, worker);
  EXECUTE format(
    'GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO %I, %I', api, worker);
EXCEPTION
  WHEN insufficient_privilege THEN
    RAISE EXCEPTION
      'existing tables in schema public belong to a role % does not administer, so the API and worker cannot be granted access to them (%). A role that administers % must run: GRANT "%" TO "%"; then rerun ./deploy/azure/up.sh',
      current_user, SQLERRM, migrator, migrator, current_user;
END
$$;

-- PostgreSQL answers a grant the grantor does not hold with a WARNING, not an
-- error, so every statement above can be discarded while psql still exits 0
-- and the hook records a bootstrap that never happened. The failure would
-- surface much later as a migration job or a replica that cannot read its own
-- tables. Assert the outcome instead: what follows is the minimum the
-- deployment cannot run without.
DO $$
DECLARE
  migrator text := current_setting('vistara.migrator_role');
  runtime_role text;
  application_database text := current_database();
BEGIN
  IF NOT has_schema_privilege(migrator, 'public', 'CREATE') THEN
    RAISE EXCEPTION
      'role % cannot create objects in schema public of database %; connect as the Microsoft Entra administrator of the server and rerun',
      migrator, application_database;
  END IF;

  FOREACH runtime_role IN ARRAY ARRAY[
    current_setting('vistara.api_role'),
    current_setting('vistara.worker_role')
  ]
  LOOP
    IF NOT has_database_privilege(runtime_role, application_database, 'CONNECT') THEN
      RAISE EXCEPTION 'role % cannot connect to database %', runtime_role, application_database;
    END IF;
    IF NOT has_schema_privilege(runtime_role, 'public', 'USAGE') THEN
      RAISE EXCEPTION 'role % has no access to schema public of database %', runtime_role, application_database;
    END IF;
  END LOOP;

  IF NOT EXISTS (
    SELECT 1
    FROM pg_default_acl
    JOIN pg_roles ON pg_roles.oid = pg_default_acl.defaclrole
    WHERE pg_roles.rolname = migrator
      AND pg_default_acl.defaclobjtype = 'r'
  ) THEN
    RAISE EXCEPTION
      'this server did not let % set default privileges for %, so the API and worker would have no access to any table a later migration creates. A role that administers % must run: GRANT "%" TO "%"; then rerun ./deploy/azure/up.sh',
      current_user, migrator, migrator, migrator, current_user;
  END IF;
END
$$;
