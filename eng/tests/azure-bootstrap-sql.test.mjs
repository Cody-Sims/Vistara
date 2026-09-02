// `deploy/azure/sql/bootstrap-roles.sql` against a real PostgreSQL server.
//
// The file is the one part of the bootstrap that a stand-in cannot check: what
// matters is whether PostgreSQL accepts each statement from a principal with
// the rights an Azure flexible-server administrator actually has, which is
// less than the owner of the database and far less than a superuser. A grant
// that role cannot make is answered with a warning rather than an error, so a
// file that silently does nothing still exits zero — this is where that is
// caught.
//
// Opt in with VISTARA_POSTGRES_SQL_CHECK=1 and a working Docker daemon:
//
//   VISTARA_POSTGRES_SQL_CHECK=1 node --test eng/tests/azure-bootstrap-sql.test.mjs

import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import test from 'node:test';

import { AZURE_DIR, REPOSITORY_ROOT } from './azure-bootstrap-harness.mjs';

const IMAGE = process.env.VISTARA_PSQL_IMAGE ?? 'postgres:17-alpine';
const CONTAINER = `vistara-bootstrap-sql-${process.pid}`;
const WORK = resolve(REPOSITORY_ROOT, 'artifacts/azure-bootstrap-sql');
const ENABLED = process.env.VISTARA_POSTGRES_SQL_CHECK === '1';

const API_OID = '44444444-4444-4444-4444-444444444444';
const WORKER_OID = '55555555-5555-5555-5555-555555555555';
const MIGRATOR_OID = '66666666-6666-6666-6666-666666666666';

// Stands in for the extension the flexible server provides: creates the login
// role and records the directory object ID where the real server records it.
// Written as SECURITY DEFINER owned by the superuser, which is the least
// generous of the possible arrangements — the administrator that calls it is
// left with no authority over the role it creates.
const STUB = `
CREATE OR REPLACE FUNCTION pgaadauth_create_principal_with_oid(
  role_name text, object_id text, object_type text, is_admin boolean, is_mfa boolean)
RETURNS text LANGUAGE plpgsql SECURITY DEFINER AS $$
BEGIN
  EXECUTE format('CREATE ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION', role_name);
  EXECUTE format(
    'INSERT INTO pg_shseclabel (objoid, classoid, provider, label) VALUES (%L::regrole::oid, %L::regclass::oid, %L, %L)',
    role_name, 'pg_authid', 'pgaadauth', 'aadauth,oid=' || object_id || ',type=' || object_type);
  RETURN role_name;
END $$;

CREATE OR REPLACE FUNCTION pgaadauth_list_principals(is_admin boolean)
RETURNS TABLE (rolname text, principal_type text, object_id text)
LANGUAGE sql SECURITY DEFINER AS $$
  SELECT pg_roles.rolname::text,
         split_part(split_part(pg_shseclabel.label, 'type=', 2), ',', 1),
         split_part(split_part(pg_shseclabel.label, 'oid=', 2), ',', 1)
  FROM pg_shseclabel
  JOIN pg_roles ON pg_roles.oid = pg_shseclabel.objoid
  WHERE pg_shseclabel.provider = 'pgaadauth';
$$;
GRANT EXECUTE ON FUNCTION pgaadauth_create_principal_with_oid(text,text,text,boolean,boolean) TO PUBLIC;
GRANT EXECUTE ON FUNCTION pgaadauth_list_principals(boolean) TO PUBLIC;

-- The administrator group the flexible server puts the Entra administrator in,
-- and an administrator that is a member of it and nothing more.
CREATE ROLE azure_pg_admin_stub NOLOGIN CREATEROLE;
CREATE ROLE entraadmin LOGIN PASSWORD 'bootstrap-probe' CREATEROLE IN ROLE azure_pg_admin_stub;
`;

function docker(args, options = {}) {
  return spawnSync('docker', args, { encoding: 'utf8', ...options });
}

function dockerAvailable() {
  try {
    execFileSync('docker', ['info'], { stdio: 'ignore', timeout: 20_000 });
    return true;
  } catch {
    return false;
  }
}

function psql(user, database, variables = {}, file = '/bootstrap-roles.sql') {
  const setters = Object.entries(variables).flatMap(([name, value]) => [`--set=${name}=${value}`]);
  const result = docker([
    'exec',
    '--env',
    'PGPASSWORD=bootstrap-probe',
    CONTAINER,
    'psql',
    '--username',
    user,
    '--host',
    '127.0.0.1',
    '--dbname',
    database,
    '--no-psqlrc',
    '--set=ON_ERROR_STOP=1',
    ...setters,
    '--file',
    file,
  ]);
  return { status: result.status, output: `${result.stdout}${result.stderr}` };
}

function sql(user, database, statement) {
  const result = docker([
    'exec',
    '--env',
    'PGPASSWORD=bootstrap-probe',
    CONTAINER,
    'psql',
    '--username',
    user,
    '--host',
    '127.0.0.1',
    '--dbname',
    database,
    '--no-psqlrc',
    '--tuples-only',
    '--no-align',
    '--command',
    statement,
  ]);
  return { status: result.status, output: `${result.stdout}${result.stderr}`.trim() };
}

function bootstrapVariables(prefix, database, overrides = {}) {
  return {
    application_database: database,
    migrator_role: `${prefix}_migrator`,
    api_role: `${prefix}_api`,
    worker_role: `${prefix}_worker`,
    migrator_object_id: MIGRATOR_OID,
    api_object_id: API_OID,
    worker_object_id: WORKER_OID,
    ...overrides,
  };
}

test('bootstrap-roles.sql against PostgreSQL 17', { skip: ENABLED ? false : 'set VISTARA_POSTGRES_SQL_CHECK=1 with Docker running' }, async (t) => {
  assert.ok(dockerAvailable(), 'VISTARA_POSTGRES_SQL_CHECK=1 needs a running Docker daemon');

  mkdirSync(WORK, { recursive: true });
  writeFileSync(resolve(WORK, 'stub.sql'), STUB, 'utf8');

  // allow_system_table_mods lets the stub write the security label the real
  // extension writes; it is a property of the throwaway container only.
  const started = docker([
    'run',
    '--detach',
    '--rm',
    '--name',
    CONTAINER,
    '--env',
    'POSTGRES_PASSWORD=bootstrap-probe',
    IMAGE,
    'postgres',
    '-c',
    'allow_system_table_mods=on',
  ]);
  assert.equal(started.status, 0, started.stderr);

  t.after(() => {
    docker(['rm', '--force', CONTAINER]);
    rmSync(WORK, { recursive: true, force: true });
  });

  for (let attempt = 0; attempt < 60; attempt += 1) {
    if (docker(['exec', CONTAINER, 'pg_isready', '--username', 'postgres']).status === 0) {
      break;
    }
    await new Promise((done) => {
      setTimeout(done, 1000);
    });
  }
  assert.equal(docker(['exec', CONTAINER, 'pg_isready', '--username', 'postgres']).status, 0, 'server started');

  docker(['cp', resolve(WORK, 'stub.sql'), `${CONTAINER}:/stub.sql`]);
  docker(['cp', resolve(AZURE_DIR, 'sql/bootstrap-roles.sql'), `${CONTAINER}:/bootstrap-roles.sql`]);
  const stub = psql('postgres', 'postgres', {}, '/stub.sql');
  assert.equal(stub.status, 0, stub.output);

  await t.test('an administrator that owns the database gets the whole model, twice over', () => {
    assert.equal(sql('postgres', 'postgres', 'CREATE DATABASE owned').status, 0);

    const first = psql('postgres', 'postgres', bootstrapVariables('owned', 'owned'));
    assert.equal(first.status, 0, first.output);
    assert.ok(!/ERROR|WARNING/.test(first.output), `no statement was discarded:\n${first.output}`);

    const second = psql('postgres', 'postgres', bootstrapVariables('owned', 'owned'));
    assert.equal(second.status, 0, second.output);
    assert.ok(!/ERROR|WARNING/.test(second.output), `a rerun changes nothing:\n${second.output}`);

    assert.equal(
      sql('postgres', 'owned', `SELECT has_schema_privilege('owned_migrator','public','CREATE')`).output,
      't',
      'the migrator may create the schema objects',
    );
    assert.equal(
      sql(
        'postgres',
        'owned',
        `SELECT has_schema_privilege('owned_api','public','USAGE'), has_database_privilege('owned_worker','owned','CONNECT')`,
      ).output,
      't|t',
      'the runtime roles may reach the schema',
    );

    // What the migration will do next: create a table as the migrator.
    assert.equal(sql('postgres', 'owned', 'SET ROLE owned_migrator; CREATE TABLE later (id int)').status, 0);
    assert.equal(
      sql(
        'postgres',
        'owned',
        `SELECT has_table_privilege('owned_api','later','SELECT'), has_table_privilege('owned_worker','later','INSERT')`,
      ).output,
      't|t',
      'default privileges carried to a table the bootstrap never saw',
    );
    assert.equal(
      sql('postgres', 'owned', `SELECT has_table_privilege('owned_api','later','TRUNCATE')`).output,
      'f',
      'and no more than the data rights',
    );
  });

  await t.test('an administrator with no authority over the migrator is refused, with the remedy', () => {
    assert.equal(sql('postgres', 'postgres', 'CREATE DATABASE guarded OWNER azure_pg_admin_stub').status, 0);
    assert.equal(sql('postgres', 'guarded', 'ALTER SCHEMA public OWNER TO azure_pg_admin_stub').status, 0);

    const refused = psql('entraadmin', 'postgres', bootstrapVariables('guarded', 'guarded'));
    assert.notEqual(refused.status, 0, 'a bootstrap that cannot finish must not report success');
    assert.match(refused.output, /did not let entraadmin set default privileges for guarded_migrator/);
    assert.match(refused.output, /GRANT "guarded_migrator" TO "entraadmin"/, 'the message says what to run');
  });

  await t.test('the same administrator succeeds once it administers the migrator', () => {
    assert.equal(sql('postgres', 'postgres', 'GRANT guarded_migrator TO entraadmin').status, 0);

    const accepted = psql('entraadmin', 'postgres', bootstrapVariables('guarded', 'guarded'));
    assert.equal(accepted.status, 0, accepted.output);

    assert.equal(sql('postgres', 'guarded', 'SET ROLE guarded_migrator; CREATE TABLE later (id int)').status, 0);
    assert.equal(
      sql(
        'postgres',
        'guarded',
        `SELECT has_table_privilege('guarded_api','later','SELECT'), has_table_privilege('guarded_worker','later','INSERT')`,
      ).output,
      't|t',
    );
  });

  await t.test('a role mapped to a different directory object stops the run', () => {
    const mismatched = psql(
      'postgres',
      'postgres',
      bootstrapVariables('owned', 'owned', { api_object_id: '00000000-0000-0000-0000-000000000009' }),
    );
    assert.notEqual(mismatched.status, 0);
    assert.match(mismatched.output, /owned_api is mapped to a different directory object/);
    assert.match(mismatched.output, /DROP ROLE "owned_api"/, 'the message says how to recover');
  });
});
