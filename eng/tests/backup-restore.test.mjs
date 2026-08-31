import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import {
  existsSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { dirname, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const REPOSITORY_ROOT = resolve(HERE, '../..');
const BACKUP = resolve(REPOSITORY_ROOT, 'deploy/backup/vistara-backup.sh');
const RESTORE = resolve(REPOSITORY_ROOT, 'deploy/backup/vistara-restore.sh');
const DRILL = resolve(REPOSITORY_ROOT, 'deploy/backup/verify-restore-drill.sh');

const FOOTER_MAGIC = Buffer.from('VISTAR01', 'ascii');

const SCHEMA = `
CREATE TABLE "__EFMigrationsHistory" (
  "MigrationId" TEXT NOT NULL PRIMARY KEY,
  "ProductVersion" TEXT NOT NULL
);
CREATE TABLE tenants (id TEXT NOT NULL PRIMARY KEY, name TEXT NOT NULL);
CREATE TABLE users (id TEXT NOT NULL PRIMARY KEY, tenant_id TEXT NOT NULL);
CREATE TABLE assets (
  id TEXT NOT NULL PRIMARY KEY,
  tenant_id TEXT NOT NULL,
  status TEXT NOT NULL
);
CREATE TABLE blobs (
  id TEXT NOT NULL PRIMARY KEY,
  tenant_id TEXT NOT NULL,
  object_key TEXT NOT NULL,
  sha256 TEXT NOT NULL,
  size_bytes INTEGER NOT NULL,
  state TEXT NOT NULL
);
CREATE TABLE api_keys (id TEXT NOT NULL PRIMARY KEY, tenant_id TEXT NOT NULL);
CREATE TABLE resource_grants (id TEXT NOT NULL PRIMARY KEY, tenant_id TEXT NOT NULL);
CREATE TABLE tenant_memberships (id TEXT NOT NULL PRIMARY KEY, tenant_id TEXT NOT NULL);
CREATE TABLE audit_events (id TEXT NOT NULL PRIMARY KEY, tenant_id TEXT NOT NULL);
CREATE TABLE deletion_tombstones (id TEXT NOT NULL PRIMARY KEY, tenant_id TEXT NOT NULL);
CREATE TABLE shares (id TEXT NOT NULL PRIMARY KEY, tenant_id TEXT NOT NULL);
`;

function sqlite3(databasePath, sql) {
  const result = spawnSync('sqlite3', [databasePath], {
    input: sql,
    encoding: 'utf8',
  });
  if (result.error || result.status !== 0) {
    throw new Error(
      `sqlite3 failed: ${result.error?.message ?? ''}${result.stderr ?? ''}`,
    );
  }
  return result.stdout;
}

function run(script, args, options = {}) {
  return spawnSync('bash', [script, ...args], {
    cwd: REPOSITORY_ROOT,
    encoding: 'utf8',
    ...options,
  });
}

function writeLocalBlob(mediaRoot, objectKey, payload) {
  const hash = createHash('sha256').update(objectKey, 'utf8').digest('hex');
  const directory = resolve(
    mediaRoot,
    '.vistara/objects',
    hash.slice(0, 2),
    hash.slice(2, 4),
  );
  mkdirSync(directory, { recursive: true });
  const descriptor = Buffer.from(
    JSON.stringify({
      Key: objectKey,
      ContentLength: payload.length,
      ContentType: 'image/jpeg',
      Sha256: createHash('sha256').update(payload).digest('hex'),
    }),
    'utf8',
  );
  const length = Buffer.alloc(8);
  length.writeBigInt64LE(BigInt(descriptor.length));
  const path = resolve(directory, `${hash}.blob`);
  writeFileSync(path, Buffer.concat([payload, descriptor, FOOTER_MAGIC, length]));
  return path;
}

function createLiveInstance(root) {
  const databasePath = resolve(root, 'live/db/vistara.db');
  const mediaRoot = resolve(root, 'live/media');
  const configPath = resolve(root, 'live/config/appsettings.Production.json');
  mkdirSync(dirname(databasePath), { recursive: true });
  mkdirSync(mediaRoot, { recursive: true });
  mkdirSync(dirname(configPath), { recursive: true });
  writeFileSync(configPath, '{"Persistence":{"Provider":"Sqlite"}}\n');

  const tenant = '8a3f0a56-0000-4000-8000-000000000001';
  const blobs = [
    { key: 'tenants/t1/originals/a.jpg', payload: Buffer.from('first-original') },
    { key: 'tenants/t1/originals/b.jpg', payload: Buffer.from('second-original') },
  ];
  const blobRows = blobs
    .map((blob, index) => {
      writeLocalBlob(mediaRoot, blob.key, blob.payload);
      const digest = createHash('sha256').update(blob.payload).digest('hex');
      return `('b${index}','${tenant}','${blob.key}','${digest}',${blob.payload.length},'Active')`;
    })
    .join(',');

  sqlite3(
    databasePath,
    `${SCHEMA}
INSERT INTO "__EFMigrationsHistory" VALUES
  ('20260829000000_InitialCreate','10.0.0'),
  ('20260830101756_AddSharingPersistence','10.0.0');
INSERT INTO tenants VALUES ('${tenant}','primary');
INSERT INTO users VALUES ('u1','${tenant}');
INSERT INTO assets VALUES ('a1','${tenant}','Ready'),('a2','${tenant}','Trashed');
INSERT INTO blobs VALUES ${blobRows};
INSERT INTO api_keys VALUES ('k1','${tenant}');
INSERT INTO resource_grants VALUES ('g1','${tenant}');
INSERT INTO tenant_memberships VALUES ('m1','${tenant}');
INSERT INTO audit_events VALUES ('e1','${tenant}');
INSERT INTO shares VALUES ('s1','${tenant}');
`,
  );

  return { databasePath, mediaRoot, configPath, tenant };
}

function backup(root, instance, output) {
  return run(BACKUP, [
    '--profile',
    'starter',
    '--database',
    instance.databasePath,
    '--media-root',
    instance.mediaRoot,
    '--config',
    instance.configPath,
    '--output',
    output ?? resolve(root, 'archive'),
  ]);
}

function withWorkspace(body) {
  const root = resolve(HERE, `.scratch-backup-${process.pid}-${randomUUID()}`);
  mkdirSync(root, { recursive: true });
  try {
    body(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

function manifestValue(archive, key) {
  const line = readFileSync(resolve(archive, 'manifest.env'), 'utf8')
    .split('\n')
    .find((candidate) => candidate.startsWith(`${key}=`));
  return line === undefined ? undefined : line.slice(key.length + 1);
}

function corruptBlobPayload(mediaRoot) {
  const objects = resolve(mediaRoot, '.vistara/objects');
  const stack = [objects];
  while (stack.length > 0) {
    const current = stack.pop();
    for (const entry of readdirSync(current, { withFileTypes: true })) {
      const path = resolve(current, entry.name);
      if (entry.isDirectory()) {
        stack.push(path);
        continue;
      }
      if (entry.name.endsWith('.blob')) {
        const content = readFileSync(path);
        content[0] = content[0] ^ 0xff;
        writeFileSync(path, content);
        return path;
      }
    }
  }
  throw new Error('No blob file was found.');
}

test('creates a verifiable archive and passes a non-destructive restore drill', () => {
  withWorkspace((root) => {
    const instance = createLiveInstance(root);
    const archive = resolve(root, 'archive');
    const created = backup(root, instance, archive);
    assert.equal(created.status, 0, created.stderr);

    assert.ok(existsSync(resolve(archive, 'manifest.json')));
    assert.ok(existsSync(resolve(archive, 'SHA256SUMS')));
    assert.ok(existsSync(resolve(archive, 'database/vistara.db')));
    assert.ok(existsSync(resolve(archive, 'media/media.tar.gz')));
    assert.ok(
      existsSync(resolve(archive, 'config/appsettings.Production.json')),
    );
    assert.equal(manifestValue(archive, 'count_tenants'), '1');
    assert.equal(manifestValue(archive, 'count_assets'), '2');
    assert.equal(manifestValue(archive, 'count_blobs'), '2');
    assert.equal(
      manifestValue(archive, 'migration_head'),
      '20260830101756_AddSharingPersistence',
    );

    const liveDatabaseBefore = createHash('sha256')
      .update(readFileSync(instance.databasePath))
      .digest('hex');

    const drill = run(DRILL, [
      '--archive',
      archive,
      '--workdir',
      resolve(root, 'drill'),
    ]);
    assert.equal(drill.status, 0, `${drill.stdout}${drill.stderr}`);
    assert.match(drill.stdout, /restore drill passed/i);
    assert.ok(existsSync(resolve(root, 'drill/drill-report.json')));

    const report = JSON.parse(
      readFileSync(resolve(root, 'drill/drill-report.json'), 'utf8'),
    );
    assert.equal(report.result, 'passed');
    assert.equal(report.checks.blobReferences, 2);
    assert.equal(report.counts.tenants, 1);

    const liveDatabaseAfter = createHash('sha256')
      .update(readFileSync(instance.databasePath))
      .digest('hex');
    assert.equal(liveDatabaseAfter, liveDatabaseBefore);
  });
});

test('refuses to write a backup into a populated output directory', () => {
  withWorkspace((root) => {
    const instance = createLiveInstance(root);
    const archive = resolve(root, 'archive');
    mkdirSync(archive, { recursive: true });
    writeFileSync(resolve(archive, 'keep.txt'), 'existing');

    const created = backup(root, instance, archive);
    assert.notEqual(created.status, 0);
    assert.match(created.stderr, /not empty/i);
    assert.equal(readFileSync(resolve(archive, 'keep.txt'), 'utf8'), 'existing');
  });
});

test('refuses to overwrite an existing database during restore', () => {
  withWorkspace((root) => {
    const instance = createLiveInstance(root);
    const archive = resolve(root, 'archive');
    assert.equal(backup(root, instance, archive).status, 0);

    const targetDatabase = resolve(root, 'target/vistara.db');
    mkdirSync(dirname(targetDatabase), { recursive: true });
    writeFileSync(targetDatabase, 'production data');

    const restored = run(RESTORE, [
      '--archive',
      archive,
      '--target-database',
      targetDatabase,
      '--target-media',
      resolve(root, 'target/media'),
    ]);
    assert.notEqual(restored.status, 0);
    assert.match(restored.stderr, /already exists/i);
    assert.equal(readFileSync(targetDatabase, 'utf8'), 'production data');
  });
});

test('rejects an archive whose recorded checksums no longer match', () => {
  withWorkspace((root) => {
    const instance = createLiveInstance(root);
    const archive = resolve(root, 'archive');
    assert.equal(backup(root, instance, archive).status, 0);
    writeFileSync(
      resolve(archive, 'config/appsettings.Production.json'),
      '{"Persistence":{"Provider":"PostgreSql"}}\n',
    );

    const restored = run(RESTORE, [
      '--archive',
      archive,
      '--target-database',
      resolve(root, 'target/vistara.db'),
      '--target-media',
      resolve(root, 'target/media'),
    ]);
    assert.notEqual(restored.status, 0);
    assert.match(restored.stderr, /checksum/i);
  });
});

test('fails the drill when a restored original no longer matches its checksum', () => {
  withWorkspace((root) => {
    const instance = createLiveInstance(root);
    corruptBlobPayload(instance.mediaRoot);
    const archive = resolve(root, 'archive');
    assert.equal(backup(root, instance, archive).status, 0);

    const drill = run(DRILL, [
      '--archive',
      archive,
      '--workdir',
      resolve(root, 'drill'),
    ]);
    assert.notEqual(drill.status, 0);
    assert.match(drill.stderr, /checksum/i);
  });
});

test('fails the drill when a referenced original is absent from the backup', () => {
  withWorkspace((root) => {
    const instance = createLiveInstance(root);
    sqlite3(
      instance.databasePath,
      "INSERT INTO blobs VALUES ('b9','8a3f0a56-0000-4000-8000-000000000001'," +
        "'tenants/t1/originals/missing.jpg'," +
        "'0000000000000000000000000000000000000000000000000000000000000000',7,'Active');",
    );
    const archive = resolve(root, 'archive');
    assert.equal(backup(root, instance, archive).status, 0);

    const drill = run(DRILL, [
      '--archive',
      archive,
      '--workdir',
      resolve(root, 'drill'),
    ]);
    assert.notEqual(drill.status, 0);
    assert.match(drill.stderr, /missing/i);
  });
});

test('fails the drill when the restored schema is incomplete', () => {
  withWorkspace((root) => {
    const instance = createLiveInstance(root);
    const archive = resolve(root, 'archive');
    assert.equal(backup(root, instance, archive).status, 0);
    sqlite3(resolve(archive, 'database/vistara.db'), 'DROP TABLE api_keys;');

    const drill = run(DRILL, [
      '--archive',
      archive,
      '--workdir',
      resolve(root, 'drill'),
      '--skip-archive-checksums',
    ]);
    assert.notEqual(drill.status, 0);
    assert.match(drill.stderr, /api_keys/);
  });
});

test('fails the drill when restored tenant counts drift from the manifest', () => {
  withWorkspace((root) => {
    const instance = createLiveInstance(root);
    const archive = resolve(root, 'archive');
    assert.equal(backup(root, instance, archive).status, 0);
    sqlite3(
      resolve(archive, 'database/vistara.db'),
      "INSERT INTO tenants VALUES ('8a3f0a56-0000-4000-8000-000000000002','extra');",
    );

    const drill = run(DRILL, [
      '--archive',
      archive,
      '--workdir',
      resolve(root, 'drill'),
      '--skip-archive-checksums',
    ]);
    assert.notEqual(drill.status, 0);
    assert.match(drill.stderr, /tenants/i);
  });
});

test('fails the drill when authorization rows lose their tenant scope', () => {
  withWorkspace((root) => {
    const instance = createLiveInstance(root);
    const archive = resolve(root, 'archive');
    assert.equal(backup(root, instance, archive).status, 0);
    sqlite3(
      resolve(archive, 'database/vistara.db'),
      "UPDATE api_keys SET tenant_id = '8a3f0a56-0000-4000-8000-0000000000ff';",
    );

    const drill = run(DRILL, [
      '--archive',
      archive,
      '--workdir',
      resolve(root, 'drill'),
      '--skip-archive-checksums',
    ]);
    assert.notEqual(drill.status, 0);
    assert.match(drill.stderr, /authorization/i);
  });
});

test('refuses to run the drill inside a populated working directory', () => {
  withWorkspace((root) => {
    const instance = createLiveInstance(root);
    const archive = resolve(root, 'archive');
    assert.equal(backup(root, instance, archive).status, 0);
    const workdir = resolve(root, 'drill');
    mkdirSync(workdir, { recursive: true });
    writeFileSync(resolve(workdir, 'previous.json'), '{}');

    const drill = run(DRILL, ['--archive', archive, '--workdir', workdir]);
    assert.notEqual(drill.status, 0);
    assert.match(drill.stderr, /not empty/i);
    assert.ok(existsSync(resolve(workdir, 'previous.json')));
  });
});

test('rejects a stale SQLite sidecar before touching the target', () => {
  withWorkspace((root) => {
    const instance = createLiveInstance(root);
    const archive = resolve(root, 'archive');
    assert.equal(backup(root, instance, archive).status, 0);

    const targetDatabase = resolve(root, 'target/vistara.db');
    mkdirSync(dirname(targetDatabase), { recursive: true });
    writeFileSync(targetDatabase, 'production data');
    writeFileSync(`${targetDatabase}-wal`, 'uncheckpointed writes');

    const restored = run(RESTORE, [
      '--archive',
      archive,
      '--target-database',
      targetDatabase,
      '--target-media',
      resolve(root, 'target/media'),
      '--force',
    ]);
    assert.notEqual(restored.status, 0);
    assert.match(restored.stderr, /stale SQLite sidecar/i);
    assert.equal(readFileSync(targetDatabase, 'utf8'), 'production data');
    assert.equal(
      readFileSync(`${targetDatabase}-wal`, 'utf8'),
      'uncheckpointed writes',
    );
    assert.equal(existsSync(resolve(root, 'target/media')), false);
  });
});

test('rejects a stale sidecar even when the target database is absent', () => {
  withWorkspace((root) => {
    const instance = createLiveInstance(root);
    const archive = resolve(root, 'archive');
    assert.equal(backup(root, instance, archive).status, 0);

    const targetDatabase = resolve(root, 'target/vistara.db');
    mkdirSync(dirname(targetDatabase), { recursive: true });
    writeFileSync(`${targetDatabase}-shm`, 'shared memory index');

    const restored = run(RESTORE, [
      '--archive',
      archive,
      '--target-database',
      targetDatabase,
      '--target-media',
      resolve(root, 'target/media'),
    ]);
    assert.notEqual(restored.status, 0);
    assert.match(restored.stderr, /stale SQLite sidecar/i);
    assert.equal(existsSync(targetDatabase), false);
  });
});
